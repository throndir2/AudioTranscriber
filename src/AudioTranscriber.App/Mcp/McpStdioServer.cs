using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AudioTranscriber.App.Mcp;

/// <summary>Minimal MCP (JSON-RPC 2.0, newline-delimited stdio) server exposing tools only.</summary>
public sealed class McpStdioServer(string name, string instructions, IReadOnlyList<McpTool> tools)
{
    private static readonly string[] KnownVersions = ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25"];
    private readonly ConcurrentDictionary<string, CancellationTokenSource> inflight = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private Stream? output;

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        this.output = output;
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        var pending = new List<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? message;
            try { message = JsonNode.Parse(line); }
            catch (JsonException)
            {
                await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = Error(-32700, "Parse error") });
                continue;
            }
            if (message is JsonArray batch)
                foreach (var item in batch) pending.Add(HandleAsync(item as JsonObject, cancellationToken));
            else pending.Add(HandleAsync(message as JsonObject, cancellationToken));
            pending.RemoveAll(task => task.IsCompleted);
        }
        foreach (var cancellation in inflight.Values) cancellation.Cancel();
        await Task.WhenAll(pending);
    }

    private async Task HandleAsync(JsonObject? message, CancellationToken cancellationToken)
    {
        if (message is null) return;
        var id = message["id"]?.DeepClone();
        var method = message["method"]?.GetValue<string>();
        var parameters = message["params"] as JsonObject;
        if (method is null) return; // Responses to server-initiated requests are not used.
        try
        {
            switch (method)
            {
                case "initialize":
                    var requested = parameters?["protocolVersion"]?.GetValue<string>();
                    await ReplyAsync(id, new JsonObject
                    {
                        ["protocolVersion"] = requested is not null && KnownVersions.Contains(requested) ? requested : KnownVersions[^1],
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = name,
                            ["version"] = typeof(McpStdioServer).Assembly.GetName().Version?.ToString() ?? "0"
                        },
                        ["instructions"] = instructions
                    });
                    return;
                case "ping":
                    await ReplyAsync(id, new JsonObject());
                    return;
                case "tools/list":
                    await ReplyAsync(id, new JsonObject
                    {
                        ["tools"] = new JsonArray(tools.Select(tool => (JsonNode)new JsonObject
                        {
                            ["name"] = tool.Name,
                            ["description"] = tool.Description,
                            ["inputSchema"] = tool.InputSchema.DeepClone()
                        }).ToArray())
                    });
                    return;
                case "tools/call":
                    await CallToolAsync(id, parameters, cancellationToken);
                    return;
                case "notifications/cancelled":
                    if (parameters?["requestId"]?.ToJsonString() is { } key && inflight.TryGetValue(key, out var cancellation))
                        cancellation.Cancel();
                    return;
                default:
                    if (id is not null && !method.StartsWith("notifications/", StringComparison.Ordinal))
                        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = Error(-32601, "Method not found: " + method) });
                    return;
            }
        }
        catch (Exception error) when (id is not null)
        {
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = Error(-32603, error.Message) });
        }
    }

    private async Task CallToolAsync(JsonNode? id, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var toolName = parameters?["name"]?.GetValue<string>();
        var tool = tools.FirstOrDefault(item => item.Name == toolName);
        if (tool is null)
        {
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = Error(-32602, "Unknown tool: " + toolName) });
            return;
        }
        var key = id?.ToJsonString() ?? Guid.NewGuid().ToString();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        inflight[key] = cancellation;
        McpToolResult result;
        try
        {
            var arguments = new McpArguments(parameters?["arguments"] as JsonObject ?? new JsonObject());
            result = await Task.Run(() => tool.Handler(arguments, cancellation.Token), cancellation.Token);
        }
        catch (OperationCanceledException) { result = McpToolResult.Fail("Canceled."); }
        catch (Exception error) { result = McpToolResult.Fail($"{error.GetType().Name}: {error.Message}"); }
        finally { inflight.TryRemove(key, out _); }
        await ReplyAsync(id, new JsonObject
        {
            ["content"] = new JsonArray(result.Content.Select(item => (JsonNode)item.DeepClone()).ToArray()),
            ["isError"] = result.IsError
        });
    }

    private Task ReplyAsync(JsonNode? id, JsonObject result) =>
        id is null ? Task.CompletedTask : SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });

    private static JsonObject Error(int code, string message) => new() { ["code"] = code, ["message"] = message };

    private async Task SendAsync(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await writeGate.WaitAsync();
        try
        {
            await output!.WriteAsync(bytes);
            await output.FlushAsync();
        }
        finally { writeGate.Release(); }
    }
}

public sealed record McpTool(string Name, string Description, JsonObject InputSchema,
    Func<McpArguments, CancellationToken, Task<McpToolResult>> Handler)
{
    /// <summary>Parameter spec: (name, JSON type, description, required).</summary>
    public static McpTool Create(string name, string description,
        Func<McpArguments, CancellationToken, Task<McpToolResult>> handler,
        params (string Name, string Type, string Description, bool Required)[] parameters)
    {
        var properties = new JsonObject();
        foreach (var parameter in parameters)
            properties[parameter.Name] = new JsonObject { ["type"] = parameter.Type, ["description"] = parameter.Description };
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        var required = parameters.Where(parameter => parameter.Required).Select(parameter => (JsonNode)parameter.Name).ToArray();
        if (required.Length > 0) schema["required"] = new JsonArray(required);
        return new(name, description, schema, handler);
    }

    public static McpTool Create(string name, string description, Func<McpArguments, McpToolResult> handler,
        params (string Name, string Type, string Description, bool Required)[] parameters) =>
        Create(name, description, (arguments, _) => Task.FromResult(handler(arguments)), parameters);
}

public sealed record McpToolResult(IReadOnlyList<JsonObject> Content, bool IsError = false)
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, NewLine = "\n" };
    public static McpToolResult Text(string text) => new([TextBlock(text)]);
    public static McpToolResult Json(object value) => Text(JsonSerializer.Serialize(value, JsonOptions));
    public static McpToolResult Fail(string message) => new([TextBlock(message)], true);
    public static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };
    public static JsonObject ImageBlock(byte[] png) =>
        new() { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" };
}

public sealed class McpArguments(JsonObject values)
{
    public string? String(string name) => values[name] is JsonValue value
        ? value.TryGetValue<string>(out var text) ? (string.IsNullOrWhiteSpace(text) ? null : text) : value.ToJsonString()
        : null;
    public string RequireString(string name) => String(name) ?? throw new ArgumentException($"'{name}' is required.");
    public int Int(string name, int fallback) => values[name] is JsonValue value
        ? value.TryGetValue<int>(out var number) ? number
          : value.TryGetValue<double>(out var real) ? (int)real
          : value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : fallback
        : fallback;
    public bool Bool(string name, bool fallback) => values[name] is JsonValue value
        ? value.TryGetValue<bool>(out var flag) ? flag
          : value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) ? parsed : fallback
        : fallback;
    public Guid RequireGuid(string name) => Guid.TryParse(RequireString(name), out var id) ? id
        : throw new ArgumentException($"'{name}' must be a GUID.");
}
