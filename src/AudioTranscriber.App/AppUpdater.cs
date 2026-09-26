using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioTranscriber.App;

public sealed record UpdateRelease(string Tag, Version Version, string PageUrl, string ZipUrl, long ZipBytes,
    string? ZipSha256, string? ChecksumUrl);

/// <summary>
/// Checks the repository's latest GitHub release, downloads and verifies its Windows ZIP into a staging folder,
/// and applies it after the app exits through a small PowerShell helper (files cannot be replaced while running).
/// </summary>
public sealed class AppUpdater
{
    public const string Repository = "throndir2/AudioTranscriber";
    private const string ProvenanceFile = "BUILD-PROVENANCE.json";
    private static readonly HttpClient Http = CreateClient();
    private readonly string settingsPath;

    public AppUpdater()
    {
        InstallDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        CurrentTag = ReadTag(InstallDirectory);
        CurrentVersion = CurrentTag is not null && TryParseTag(CurrentTag, out var version) ? version : null;
        settingsPath = Path.Combine(UpdateRoot, "settings.json");
        AutoUpdate = LoadAutoUpdate();
        DetectStaged();
    }

    public static string UpdateRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioTranscriber.Updates");
    public string InstallDirectory { get; }
    public string? CurrentTag { get; }
    public Version? CurrentVersion { get; }
    /// <summary>Only release builds (which carry BUILD-PROVENANCE.json) update themselves.</summary>
    public bool IsSupported => CurrentVersion is not null;
    public bool AutoUpdate { get; private set; }
    public UpdateRelease? Available { get; private set; }
    public string? StagedTag { get; private set; }
    public bool RelaunchAfterApply { get; set; }
    private string StagedDirectory => Path.Combine(UpdateRoot, "staged");

    public void SetAutoUpdate(bool enabled)
    {
        AutoUpdate = enabled;
        try
        {
            Directory.CreateDirectory(UpdateRoot);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(new { autoUpdate = enabled }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        var feed = Environment.GetEnvironmentVariable("AUDIOTRANSCRIBER_UPDATE_FEED") is { Length: > 0 } custom
            ? custom : $"https://api.github.com/repos/{Repository}/releases/latest";
        using var response = await Http.GetAsync(feed, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) { Available = null; return null; }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseTag(tag, out var version)) { Available = null; return null; }
        var zipName = $"AudioTranscriber-{tag}-win-x64.zip";
        string? zipUrl = null, digest = null, checksumUrl = null;
        long size = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (name == zipName)
            {
                zipUrl = asset.GetProperty("browser_download_url").GetString();
                size = asset.GetProperty("size").GetInt64();
                if (asset.TryGetProperty("digest", out var d) && d.GetString() is { } value &&
                    value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    digest = value[7..].ToLowerInvariant();
            }
            else if (name == zipName + ".sha256") checksumUrl = asset.GetProperty("browser_download_url").GetString();
        }
        Available = zipUrl is null || size <= 0 ? null : new UpdateRelease(tag, version,
            root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "", zipUrl, size, digest, checksumUrl);
        return Available;
    }

    public bool IsNewer(UpdateRelease release) => CurrentVersion is not null && release.Version > CurrentVersion;

    /// <summary>Downloads, verifies (GitHub SHA-256 digest and the published .sha256 file) and stages the release.</summary>
    public async Task DownloadAsync(UpdateRelease release, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(UpdateRoot);
        var zipPath = Path.Combine(UpdateRoot, Path.GetFileName(new Uri(release.ZipUrl).LocalPath) + ".partial");
        var published = release.ChecksumUrl is null ? null : await ReadChecksumAsync(release.ChecksumUrl, cancellationToken);
        if (release.ZipSha256 is null && published is null)
            throw new InvalidDataException("The release has no SHA-256 to verify the download against.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var response = await Http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            var buffer = new byte[1 << 16];
            long total = 0, reported = -1;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                total += read;
                var percent = total * 100 / release.ZipBytes;
                if (percent != reported)
                {
                    reported = percent;
                    progress?.Report($"Downloading {release.Tag}: {percent}% ({total / 1048576:N0} / {release.ZipBytes / 1048576:N0} MiB)");
                }
            }
            if (total != release.ZipBytes) throw new InvalidDataException("The update download is incomplete.");
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if ((release.ZipSha256 is not null && actual != release.ZipSha256) || (published is not null && actual != published))
        {
            File.Delete(zipPath);
            throw new InvalidDataException("The update download does not match its published SHA-256.");
        }
        progress?.Report($"Verified {release.Tag}; unpacking…");
        var unpacking = Path.Combine(UpdateRoot, "unpacking");
        if (Directory.Exists(unpacking)) Directory.Delete(unpacking, true);
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, unpacking), cancellationToken);
        File.Delete(zipPath);
        if (!File.Exists(Path.Combine(unpacking, "AudioTranscriber.App.exe")) || ReadTag(unpacking) != release.Tag)
        {
            Directory.Delete(unpacking, true);
            throw new InvalidDataException("The downloaded package is not a complete AudioTranscriber release.");
        }
        if (Directory.Exists(StagedDirectory)) Directory.Delete(StagedDirectory, true);
        Directory.Move(unpacking, StagedDirectory);
        StagedTag = release.Tag;
    }

    /// <summary>Starts the helper that waits for this process to exit, copies the staged files, and optionally relaunches.</summary>
    public bool ApplyOnExit(IReadOnlyList<string> arguments)
    {
        if (StagedTag is null || !Directory.Exists(StagedDirectory)) return false;
        var script = Path.Combine(UpdateRoot, "apply-update.ps1");
        File.WriteAllText(script, ApplyScript, Encoding.UTF8);
        var relaunchArguments = string.Join(" ", arguments.Select(QuoteArgument));
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = !CanWrite(InstallDirectory),
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (info.UseShellExecute) info.Verb = "runas";
        foreach (var argument in new[]
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
            "-ProcessId", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-Source", StagedDirectory, "-Target", InstallDirectory, "-Log", Path.Combine(UpdateRoot, "update.log"),
            "-Relaunch", RelaunchAfterApply ? "1" : "0",
            "-ArgumentsBase64", Convert.ToBase64String(Encoding.UTF8.GetBytes(relaunchArguments))
        }) info.ArgumentList.Add(argument);
        try { Process.Start(info)?.Dispose(); return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0);
        if (!tag.StartsWith('v')) return false;
        var core = tag[1..].Split('-', '+')[0];
        return core.Count(c => c == '.') == 2 && Version.TryParse(core, out version!);
    }

    private void DetectStaged()
    {
        try
        {
            if (!Directory.Exists(StagedDirectory)) return;
            var tag = ReadTag(StagedDirectory);
            if (tag is not null && TryParseTag(tag, out var version) && CurrentVersion is not null && version > CurrentVersion &&
                File.Exists(Path.Combine(StagedDirectory, "AudioTranscriber.App.exe")))
                StagedTag = tag;
            else Directory.Delete(StagedDirectory, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private bool LoadAutoUpdate()
    {
        try
        {
            if (!File.Exists(settingsPath)) return true;
            using var json = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return !json.RootElement.TryGetProperty("autoUpdate", out var value) || value.GetBoolean();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return true; }
    }

    private static string? ReadTag(string directory)
    {
        try
        {
            var path = Path.Combine(directory, ProvenanceFile);
            if (!File.Exists(path)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.TryGetProperty("tag", out var tag) ? tag.GetString() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static async Task<string?> ReadChecksumAsync(string url, CancellationToken cancellationToken)
    {
        var text = await Http.GetStringAsync(url, cancellationToken);
        var token = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        return token is { Length: 64 } && token.All(Uri.IsHexDigit) ? token : throw new InvalidDataException("The published checksum is malformed.");
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".update-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string QuoteArgument(string argument) =>
        argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0 ? argument : "\"" + argument.Replace("\"", "\\\"") + "\"";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AudioTranscriber-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    private const string ApplyScript = """
        param([int]$ProcessId, [string]$Source, [string]$Target, [string]$Log, [string]$Relaunch, [string]$ArgumentsBase64)
        $ErrorActionPreference = 'Stop'
        function Write-Log([string]$Message) { Add-Content -LiteralPath $Log -Value "$(Get-Date -Format s) $Message" }
        $failed = $false
        try {
            Write-Log "Waiting for process $ProcessId to exit before updating $Target"
            try { Wait-Process -Id $ProcessId -Timeout 600 -ErrorAction SilentlyContinue } catch { }
            $deadline = (Get-Date).AddMinutes(2)
            while ((Get-Date) -lt $deadline -and @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
                try { $_.Path -and $_.Path.StartsWith($Target + '\', [StringComparison]::OrdinalIgnoreCase) } catch { $false } }).Count -gt 0) {
                Start-Sleep -Milliseconds 500
            }
            # Copy everything except the version marker first, so a partial copy is retried on the next exit.
            foreach ($pass in @(@('/XF', 'BUILD-PROVENANCE.json'), @('/IF', 'BUILD-PROVENANCE.json', '/IS'))) {
                $code = 16
                for ($attempt = 0; $attempt -lt 15 -and $code -ge 8; $attempt++) {
                    if ($attempt -gt 0) { Start-Sleep -Seconds 2 }
                    & robocopy.exe $Source $Target /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP @pass | Out-Null
                    $code = $LASTEXITCODE
                }
                if ($code -ge 8) { throw "robocopy failed with exit code $code" }
            }
            Remove-Item -LiteralPath $Source -Recurse -Force -ErrorAction SilentlyContinue
            Write-Log 'Update applied.'
        }
        catch { $failed = $true; Write-Log "Update failed: $_" }
        if ($Relaunch -eq '1') {
            $arguments = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ArgumentsBase64))
            $exe = Join-Path $Target 'AudioTranscriber.App.exe'
            if ($arguments) { Start-Process -FilePath $exe -ArgumentList $arguments } else { Start-Process -FilePath $exe }
        }
        """;
}
