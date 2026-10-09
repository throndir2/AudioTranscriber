using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AudioTranscriber.App.Templates;
using Xunit;

namespace AudioTranscriber.App.Tests;

public sealed class LlmClientContextTests
{
    [Fact]
    public async Task OllamaReportsItsTrainedWindowAndChatSendsNumCtxToTheNativeApi()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        JsonNode? chat = null;
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var context = await listener.GetContextAsync();
                var body = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
                var reply = context.Request.Url!.AbsolutePath switch
                {
                    "/api/show" => """{"details":{},"model_info":{"general.architecture":"gemma3","gemma3.context_length":131072}}""",
                    "/api/chat" => """{"message":{"role":"assistant","content":"done"},"done":true}""",
                    _ => null
                };
                if (context.Request.Url.AbsolutePath == "/api/chat") chat = JsonNode.Parse(body);
                context.Response.StatusCode = reply is null ? 404 : 200;
                var bytes = Encoding.UTF8.GetBytes(reply ?? "{}");
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        });

        var baseUrl = $"http://localhost:{port}/v1";
        var window = await LlmClient.ContextWindowAsync(baseUrl, null, "gemma3:4b", CancellationToken.None);
        Assert.Equal(new ContextWindow(131072, true, "Ollama"), window);

        var answer = await LlmClient.CompleteAsync(baseUrl, null, "gemma3:4b", "system", "user", null, null, CancellationToken.None,
            [[1, 2, 3]], ollamaContext: 16384);
        await server;
        Assert.Equal("done", answer);
        Assert.Equal(16384, (int)chat!["options"]!["num_ctx"]!);
        Assert.Equal("AQID", (string)chat["messages"]![1]!["images"]![0]!);
    }

    private static int FreePort()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }
}
