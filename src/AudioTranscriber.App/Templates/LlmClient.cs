using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AudioTranscriber.App.Templates;

public interface ILlmToolHost
{
    JsonArray Definitions { get; }
    Task<string> InvokeAsync(string name, string arguments, CancellationToken cancellationToken);
}

/// <summary>Calls any OpenAI-compatible /chat/completions endpoint (OpenRouter, NVIDIA Build, Ollama, OpenAI, LM Studio…).</summary>
public static partial class LlmClient
{
    private const int MaxToolRounds = 10;
    // Long transcripts on a local model can take many minutes to read in.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public static string ChatUrl(string baseUrl) => Endpoint(baseUrl, "chat/completions");

    /// <summary>The API root (usually ending in /v1) and the server root without /v1, or false when the URL is empty.</summary>
    private static bool TryRoots(string baseUrl, out string root, out string serverRoot)
    {
        try { root = Endpoint(baseUrl, "")[..^1]; }
        catch (LlmException) { root = serverRoot = ""; return false; }
        serverRoot = root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root[..^3] : root;
        return true;
    }

    private static string Endpoint(string baseUrl, string path)
    {
        var trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
        if (trimmed.Length == 0) throw new LlmException("The connection has no base URL.");
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^"/chat/completions".Length];
        return trimmed + "/" + path;
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        // OpenRouter shows these in its dashboard; other servers ignore them.
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/throndir2/AudioTranscriber");
        request.Headers.TryAddWithoutValidation("X-Title", "AudioTranscriber");
        return request;
    }

    public static async Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, Endpoint(baseUrl, "models"), apiKey);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new LlmException(Describe(response, body));
        JsonNode? json;
        try { json = JsonNode.Parse(body); }
        catch (JsonException) { throw new LlmException("The endpoint did not return JSON. Check the base URL (it usually ends in /v1)."); }
        var items = json?["data"] as JsonArray ?? json?["models"] as JsonArray ?? json as JsonArray ?? [];
        return items.Select(item => (string?)(item?["id"] ?? item?["name"] ?? item?["model"]))
            .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!)
            .Distinct().OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Asks the server whether <paramref name="model"/> accepts images. Null when no probe gives a conclusive answer.</summary>
    public static async Task<bool?> SupportsImagesAsync(string baseUrl, string? apiKey, string model, CancellationToken ct)
    {
        model = (model ?? "").Trim();
        if (model.Length == 0 || !TryRoots(baseUrl, out var root, out var serverRoot)) return null;

        // Ollama
        var ollama = await ProbeAsync(HttpMethod.Post, serverRoot + "/api/show", apiKey,
            new JsonObject { ["model"] = model }.ToJsonString(), ct);
        if (ollama is JsonObject show)
        {
            if (show["capabilities"] is JsonArray caps)
                return caps.Any(c => string.Equals(c?.ToString(), "vision", StringComparison.OrdinalIgnoreCase));
            if (show["projector_info"] is not null) return true;
            if (show["model_info"] is JsonObject info && info.Any(p => p.Key.Contains("vision", StringComparison.OrdinalIgnoreCase))) return true;
        }

        // LM Studio
        var lms = await ProbeAsync(HttpMethod.Get, serverRoot + "/api/v0/models/" + Uri.EscapeDataString(model), apiKey, null, ct);
        switch (lms?["type"]?.ToString()?.ToLowerInvariant())
        {
            case "vlm": return true;
            case "llm": return false;
        }

        // OpenRouter and other OpenAI-compatible model lists
        var list = await ProbeAsync(HttpMethod.Get, root + "/models", apiKey, null, ct);
        var items = list?["data"] as JsonArray ?? list as JsonArray;
        var entry = items?.FirstOrDefault(i => string.Equals(i?["id"]?.ToString(), model, StringComparison.OrdinalIgnoreCase));
        if (entry?["architecture"] is JsonObject arch)
        {
            if (arch["input_modalities"] is JsonArray inputs)
                return inputs.Any(m => string.Equals(m?.ToString(), "image", StringComparison.OrdinalIgnoreCase));
            if (arch["modality"]?.ToString() is { Length: > 0 } modality)
                return modality.Split("->")[0].Contains("image", StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }

    /// <summary>
    /// Asks the server how many tokens <paramref name="model"/> takes in. Tokens is 0 when no probe gives an answer.
    /// Ollama is flagged because its OpenAI endpoint can't set the context size, so templates use its own /api/chat.
    /// </summary>
    public static async Task<ContextWindow> ContextWindowAsync(string baseUrl, string? apiKey, string model, CancellationToken ct)
    {
        model = (model ?? "").Trim();
        if (model.Length == 0 || !TryRoots(baseUrl, out var root, out var serverRoot)) return new(0, false, "");

        if (await ProbeAsync(HttpMethod.Post, serverRoot + "/api/show", apiKey, new JsonObject { ["model"] = model }.ToJsonString(), ct) is JsonObject show &&
            (show.ContainsKey("model_info") || show.ContainsKey("details")))
        {
            var trained = show["model_info"] is JsonObject info
                ? info.Where(p => p.Key.EndsWith(".context_length", StringComparison.Ordinal)).Select(p => Int(p.Value)).FirstOrDefault(v => v > 0)
                : 0;
            return new(trained, true, "Ollama");
        }

        if (await ProbeAsync(HttpMethod.Get, serverRoot + "/api/v0/models/" + Uri.EscapeDataString(model), apiKey, null, ct) is JsonObject lms)
        {
            if (Int(lms["loaded_context_length"]) is > 0 and var loaded) return new(loaded, false, "LM Studio, as loaded");
            if (Int(lms["max_context_length"]) is > 0 and var max) return new(max, false, "LM Studio");
        }

        if (await ProbeAsync(HttpMethod.Get, serverRoot + "/props", apiKey, null, ct) is JsonObject props &&
            Int(Get(props["default_generation_settings"], "n_ctx") ?? props["n_ctx"]) is > 0 and var slot)
            return new(slot, false, "llama.cpp server");

        var list = await ProbeAsync(HttpMethod.Get, root + "/models", apiKey, null, ct);
        var items = list is JsonObject o ? (o["data"] ?? o["models"]) as JsonArray : list as JsonArray;
        if (items?.FirstOrDefault(i => i is JsonObject e &&
                (string.Equals(e["id"]?.ToString(), model, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(e["name"]?.ToString(), model, StringComparison.OrdinalIgnoreCase))) is JsonObject entry)
        {
            // OpenRouter/Together, Groq, vLLM, Mistral, LiteLLM, Gemini, llama.cpp.
            var tokens = new[]
            {
                entry["context_length"], Get(entry["top_provider"], "context_length"), entry["context_window"], entry["max_model_len"],
                entry["max_context_length"], entry["max_input_tokens"], entry["inputTokenLimit"], Get(entry["meta"], "n_ctx"), Get(entry["meta"], "n_ctx_train")
            }.Select(Int).FirstOrDefault(v => v > 0);
            if (tokens > 0) return new(tokens, false, "model list");
        }
        return new(0, false, "");
    }

    private static JsonNode? Get(JsonNode? node, string key) => node is JsonObject o ? o[key] : null;

    private static int Int(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, 0, int.MaxValue);
        if (v.TryGetValue<double>(out var d)) return (int)Math.Clamp(d, 0, int.MaxValue);
        return v.TryGetValue<string>(out var s) && long.TryParse(s, out l) ? (int)Math.Clamp(l, 0, int.MaxValue) : 0;
    }

    private static async Task<JsonNode?> ProbeAsync(HttpMethod method, string url, string? apiKey, string? json, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = Request(method, url, apiKey);
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>
    /// Runs a chat with optional tools until the model returns a final answer. Images (JPEG) go in the user message.
    /// When <paramref name="ollamaContext"/> is set, the request goes to Ollama's own /api/chat with that num_ctx,
    /// because Ollama's OpenAI endpoint always uses the server's default (often only 4096 tokens).
    /// </summary>
    public static async Task<string> CompleteAsync(string baseUrl, string? apiKey, string model, string system, string user,
        ILlmToolHost? tools, IProgress<string>? progress, CancellationToken cancellationToken, IReadOnlyList<byte[]>? images = null,
        int ollamaContext = 0)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new LlmException("The connection has no model. Choose or type a model name.");
        var native = ollamaContext > 0;
        var url = ChatUrl(baseUrl);
        if (native && TryRoots(baseUrl, out _, out var serverRoot)) url = serverRoot + "/api/chat";
        var userMessage = new JsonObject { ["role"] = "user", ["content"] = user };
        if (images is { Count: > 0 } && native)
            userMessage["images"] = new JsonArray(images.Select(image => (JsonNode)Convert.ToBase64String(image)).ToArray());
        else if (images is { Count: > 0 })
        {
            var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = user } };
            foreach (var image in images)
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(image) }
                });
            userMessage["content"] = parts;
        }
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = system },
            userMessage
        };
        var useTools = tools is not null;
        for (var round = 0; ; round++)
        {
            var lastRound = round >= MaxToolRounds;
            var payload = new JsonObject
            {
                ["model"] = model.Trim(),
                ["messages"] = messages.DeepClone(),
                ["stream"] = false
            };
            if (native) payload["options"] = new JsonObject { ["num_ctx"] = ollamaContext };
            if (useTools && !lastRound) payload["tools"] = tools!.Definitions.DeepClone();
            progress?.Report(round == 0 ? "Asking the model…" : $"Asking the model (step {round + 1})…");
            using var request = Request(HttpMethod.Post, url, apiKey);
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Many local models reject the tools field; carry on without file access rather than failing.
                if (useTools && round == 0 && (int)response.StatusCode is 400 or 404 or 422 &&
                    body.Contains("tool", StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report("This model does not support tools; continuing without file access.");
                    useTools = false;
                    round = -1;
                    continue;
                }
                if (images is { Count: > 0 } && VisionError().IsMatch(body))
                    throw new LlmException(Describe(response, body) + " This model can't read images. Pick a vision model such as gemma4:e4b.");
                throw new LlmException(Describe(response, body));
            }
            JsonNode? json;
            try { json = JsonNode.Parse(body); }
            catch (JsonException) { throw new LlmException("The endpoint did not return JSON. Check the base URL (it usually ends in /v1)."); }
            if (json?["error"] is { } error)
                throw new LlmException("The endpoint returned an error: " + Trim(ErrorText(error)));
            var message = (native ? json?["message"] : json?["choices"]?[0]?["message"]) as JsonObject
                ?? throw new LlmException("The endpoint returned no message. Check the base URL and model.");
            if (useTools && !lastRound && message["tool_calls"] is JsonArray { Count: > 0 } calls)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = message["content"]?.DeepClone() ?? "",
                    ["tool_calls"] = calls.DeepClone()
                });
                foreach (var call in calls)
                {
                    var name = call?["function"]?["name"]?.ToString() ?? "";
                    var arguments = call?["function"]?["arguments"] is JsonValue value && value.TryGetValue<string>(out var text)
                        ? text : call?["function"]?["arguments"]?.ToJsonString() ?? "{}";
                    progress?.Report($"Model is using {name}…");
                    string result;
                    try { result = await tools!.InvokeAsync(name, arguments, cancellationToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { result = "Error: " + ex.Message; }
                    var reply = new JsonObject { ["role"] = "tool", ["content"] = result, ["name"] = name };
                    if (native) reply["tool_name"] = name;
                    if (call?["id"]?.ToString() is { Length: > 0 } id) reply["tool_call_id"] = id;
                    messages.Add(reply);
                }
                continue;
            }
            var content = message["content"] switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonArray parts => string.Concat(parts.Select(p => p?["text"]?.ToString())),
                _ => ""
            };
            return StripThinking(content).Trim();
        }
    }

    public static string StripThinking(string text) => ThinkBlock().Replace(text ?? "", "");

    [GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"image|vision|multimodal|multi-modal", RegexOptions.IgnoreCase)]
    private static partial Regex VisionError();

    private static string Describe(HttpResponseMessage response, string body)
    {
        string? detail = null;
        try { detail = JsonNode.Parse(body)?["error"] is { } e ? ErrorText(e) : null; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        detail ??= body;
        var hint = (int)response.StatusCode switch
        {
            401 or 403 => " Check the API key.",
            404 => " Check the base URL and model name.",
            429 => " The service is rate limiting; the next update tries again.",
            _ => ""
        };
        return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Trim(detail)}{hint}";
    }

    // OpenAI-style servers send {"error":{"message":…}}; Ollama's own API sends {"error":"…"}.
    private static string ErrorText(JsonNode error) =>
        error is JsonObject o ? o["message"]?.ToString() ?? o.ToJsonString() : error.ToString();

    private static string Trim(string text)
    {
        text = (text ?? "").ReplaceLineEndings(" ").Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }
}

public sealed class LlmException(string message) : Exception(message);

/// <summary>How many tokens a model takes in (0 when unknown), whether the server is Ollama, and where the number came from.</summary>
public sealed record ContextWindow(int Tokens, bool Ollama, string Source);
