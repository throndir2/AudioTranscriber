using System.Net.Http.Headers;
using Microsoft.Identity.Client;

namespace AudioTranscriber.Integrations;

public sealed record TeamsTranscriptRequest(
    string TenantId, string ClientId, string UserId, string MeetingId, string TranscriptId,
    bool Unattributed = false);

public sealed record DeviceSignInPrompt(string UserCode, string VerificationUrl, DateTimeOffset ExpiresOn);

public sealed class TeamsTranscriptException(string message) : Exception(message);

public sealed class TeamsTranscriptClient
{
    public async Task DownloadAsync(
        TeamsTranscriptRequest request, string destination,
        Func<DeviceSignInPrompt, Task> showSignIn, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(request.TenantId, out var tenant) ||
            !Guid.TryParse(request.ClientId, out var client))
            throw new ArgumentException("Teams requires a work/school tenant ID and public client application ID.");
        if (new[] { request.UserId, request.MeetingId, request.TranscriptId }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("User, meeting, and transcript identifiers are required.");

        var app = PublicClientApplicationBuilder.Create(client.ToString())
            .WithAuthority($"https://login.microsoftonline.com/{tenant:D}")
            .WithRedirectUri("http://localhost")
            .Build();
        AuthenticationResult authentication;
        try
        {
            authentication = await app.AcquireTokenWithDeviceCode(
                ["https://graph.microsoft.com/OnlineMeetingTranscript.Read.All"],
                result => showSignIn(new(result.UserCode, result.VerificationUrl, result.ExpiresOn)))
                .ExecuteAsync(cancellationToken);
        }
        catch (MsalException)
        {
            throw new TeamsTranscriptException(
                "Microsoft sign-in or tenant consent failed. Check the tenant, public-client device-code setting, and OnlineMeetingTranscript.Read.All permission.");
        }

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        var route = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(request.UserId)}" +
                    $"/onlineMeetings/{Uri.EscapeDataString(request.MeetingId)}" +
                    $"/transcripts/{Uri.EscapeDataString(request.TranscriptId)}/content";
        using var message = new HttpRequestMessage(HttpMethod.Get, route);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authentication.AccessToken);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(request.Unattributed
            ? "application/vnd.microsoft.graph.transcript+text" : "text/vtt"));
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new TeamsTranscriptException(
                $"Graph returned HTTP {(int)response.StatusCode}. Check meeting access and tenant transcript/attribution policy. No identity restriction is bypassed and no alternate content is fetched automatically.");

        const long limit = 64L * 1024 * 1024;
        if (response.Content.Headers.ContentLength is > limit)
            throw new TeamsTranscriptException("The transcript exceeds the 64 MiB download limit.");
        var partial = destination + ".download";
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             65_536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65_536];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total += read;
                    if (total > limit) throw new TeamsTranscriptException("The transcript exceeds the 64 MiB download limit.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await target.FlushAsync(cancellationToken);
                target.Flush(true);
            }
            File.Move(partial, destination, false);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }
}
