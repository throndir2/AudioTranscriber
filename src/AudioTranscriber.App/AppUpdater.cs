using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioTranscriber.App;

public sealed record UpdateRelease(string Tag, Version Version, string PageUrl, string ZipUrl, long ZipBytes,
    string? ZipSha256, string? ChecksumUrl);

/// <summary>
/// Checks GitHub releases, downloads and verifies the platform package, then applies it after the app exits.
/// Windows keeps the historical ZIP updater; Linux uses the tarball for writable installs or DEB/RPM for system installs.
/// </summary>
public sealed class AppUpdater
{
    public const string Repository = "throndir2/AudioTranscriber";
    private const string ProvenanceFile = "BUILD-PROVENANCE.json";
    private const string MarkerFile = "stage.json";
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
        var assetName = PreferredAssetName(tag, version);
        string? assetUrl = null, digest = null, checksumUrl = null;
        long size = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (name == assetName)
            {
                assetUrl = asset.GetProperty("browser_download_url").GetString();
                size = asset.GetProperty("size").GetInt64();
                if (asset.TryGetProperty("digest", out var d) && d.GetString() is { } value &&
                    value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    digest = value[7..].ToLowerInvariant();
            }
            else if (name == assetName + ".sha256") checksumUrl = asset.GetProperty("browser_download_url").GetString();
        }
        Available = assetUrl is null || size <= 0 ? null : new UpdateRelease(tag, version,
            root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "", assetUrl, size, digest, checksumUrl);
        return Available;
    }

    public bool IsNewer(UpdateRelease release) => CurrentVersion is not null && release.Version > CurrentVersion;

    public async Task DownloadAsync(UpdateRelease release, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(UpdateRoot);
        var assetName = Path.GetFileName(new Uri(release.ZipUrl).LocalPath);
        var downloadPath = Path.Combine(UpdateRoot, assetName + ".partial");
        var published = release.ChecksumUrl is null ? null : await ReadChecksumAsync(release.ChecksumUrl, cancellationToken);
        if (release.ZipSha256 is null && published is null)
            throw new InvalidDataException("The release has no SHA-256 to verify the download against.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var response = await Http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            var buffer = new byte[1 << 16];
            long total = 0, reported = -1;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                total += read;
                var percent = Math.Min(100, total * 100 / Math.Max(1, release.ZipBytes));
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
            File.Delete(downloadPath);
            throw new InvalidDataException("The update download does not match its published SHA-256.");
        }
        progress?.Report($"Verified {release.Tag}; staging…");
        var unpacking = Path.Combine(UpdateRoot, "unpacking");
        if (Directory.Exists(unpacking)) Directory.Delete(unpacking, true);
        Directory.CreateDirectory(unpacking);
        if (OperatingSystem.IsWindows())
        {
            ZipFile.ExtractToDirectory(downloadPath, unpacking);
            File.Delete(downloadPath);
            if (!File.Exists(Path.Combine(unpacking, WindowsExecutableName)) || ReadTag(unpacking) != release.Tag)
                throw InvalidPackage(unpacking);
            ReplaceStaged(unpacking, new StageInfo("windows-zip", release.Tag, release.PageUrl, null));
        }
        else if (assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            ExtractTarGz(downloadPath, unpacking);
            File.Delete(downloadPath);
            var payload = Directory.Exists(Path.Combine(unpacking, "AudioTranscriber")) ? Path.Combine(unpacking, "AudioTranscriber") : unpacking;
            if (!File.Exists(Path.Combine(payload, LinuxExecutableName)) || ReadTag(payload) != release.Tag)
                throw InvalidPackage(unpacking);
            ReplaceStaged(payload, new StageInfo("linux-tar", release.Tag, release.PageUrl, null));
            if (payload != unpacking && Directory.Exists(unpacking)) Directory.Delete(unpacking, true);
        }
        else
        {
            var packageName = assetName.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase) ? "update.rpm" : "update.deb";
            File.Move(downloadPath, Path.Combine(unpacking, packageName), true);
            ReplaceStaged(unpacking, new StageInfo(packageName.EndsWith(".rpm") ? "linux-rpm" : "linux-deb", release.Tag, release.PageUrl, packageName));
        }
        StagedTag = release.Tag;
    }

    public bool ApplyOnExit(IReadOnlyList<string> arguments)
    {
        if (StagedTag is null || !Directory.Exists(StagedDirectory)) return false;
        return OperatingSystem.IsWindows() ? ApplyOnWindows(arguments) : ApplyOnLinux(arguments);
    }

    public static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0);
        if (!tag.StartsWith('v')) return false;
        var core = tag[1..].Split('-', '+')[0];
        return core.Count(c => c == '.') == 2 && Version.TryParse(core, out version!);
    }

    private bool ApplyOnWindows(IReadOnlyList<string> arguments)
    {
        var script = Path.Combine(UpdateRoot, "apply-update.ps1");
        File.WriteAllText(script, WindowsApplyScript, Encoding.UTF8);
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
        catch (Win32Exception) { return false; }
    }

    private bool ApplyOnLinux(IReadOnlyList<string> arguments)
    {
        var info = ReadStageInfo();
        var script = Path.Combine(UpdateRoot, "apply-update.sh");
        File.WriteAllText(script, LinuxApplyScript, Encoding.UTF8);
        TryChmod(script, "755");
        var start = new ProcessStartInfo("sh") { UseShellExecute = false };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(StagedDirectory);
        start.ArgumentList.Add(InstallDirectory);
        start.ArgumentList.Add(Path.Combine(UpdateRoot, "update.log"));
        start.ArgumentList.Add(RelaunchAfterApply ? "1" : "0");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(" ", arguments.Select(QuoteArgument)))));
        start.ArgumentList.Add(info.Kind);
        start.ArgumentList.Add(info.PackageName ?? "");
        start.ArgumentList.Add(info.PageUrl ?? "");
        try { Process.Start(start)?.Dispose(); return true; }
        catch (Win32Exception) { return false; }
    }

    private void DetectStaged()
    {
        try
        {
            if (!Directory.Exists(StagedDirectory)) return;
            var info = ReadStageInfo();
            if (TryParseTag(info.Tag, out var version) && CurrentVersion is not null && version > CurrentVersion)
                StagedTag = info.Tag;
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

    private string PreferredAssetName(string tag, Version version)
    {
        if (OperatingSystem.IsWindows()) return $"AudioTranscriber-{tag}-win-x64.zip";
        if (CanWrite(InstallDirectory)) return $"AudioTranscriber-{tag}-linux-x64.tar.gz";
        var semver = version.ToString(3);
        return PreferRpm() ? $"audiotranscriber-{semver}-1.x86_64.rpm" : $"audiotranscriber_{semver}_amd64.deb";
    }

    private static bool PreferRpm() =>
        (File.Exists("/etc/redhat-release") || File.Exists("/etc/fedora-release") || CommandExists("rpm")) && !CommandExists("dpkg");

    private static bool CommandExists(string name)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Any(dir => File.Exists(Path.Combine(dir, name)));
        }
        catch { return false; }
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

    private void ReplaceStaged(string source, StageInfo info)
    {
        if (Directory.Exists(StagedDirectory)) Directory.Delete(StagedDirectory, true);
        Directory.Move(source, StagedDirectory);
        File.WriteAllText(Path.Combine(StagedDirectory, MarkerFile), JsonSerializer.Serialize(info));
    }

    private StageInfo ReadStageInfo()
    {
        var marker = Path.Combine(StagedDirectory, MarkerFile);
        if (File.Exists(marker)) return JsonSerializer.Deserialize<StageInfo>(File.ReadAllText(marker)) ?? throw new InvalidDataException("Invalid update stage marker.");
        var tag = ReadTag(StagedDirectory) ?? throw new InvalidDataException("Invalid update stage marker.");
        return new StageInfo(OperatingSystem.IsWindows() ? "windows-zip" : "linux-tar", tag, null, null);
    }

    private static InvalidDataException InvalidPackage(string directory)
    {
        try { Directory.Delete(directory, true); } catch { }
        return new InvalidDataException("The downloaded package is not a complete AudioTranscriber release.");
    }

    private static void ExtractTarGz(string archive, string destination)
    {
        var process = Process.Start(new ProcessStartInfo("tar")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            ArgumentList = { "-xzf", archive, "-C", destination }
        }) ?? throw new IOException("Could not start tar to extract the update.");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidDataException("Could not extract update tarball: " + stderr);
    }

    private static void TryChmod(string path, string mode)
    {
        try { Process.Start("chmod", new[] { mode, path })?.WaitForExit(); } catch { }
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

    private static string WindowsExecutableName => "AudioTranscriber.App.exe";
    private static string LinuxExecutableName => "AudioTranscriber.App";
    private sealed record StageInfo(string Kind, string Tag, string? PageUrl, string? PackageName);

    private const string WindowsApplyScript = """
        param([int]$ProcessId, [string]$Source, [string]$Target, [string]$Log, [string]$Relaunch, [string]$ArgumentsBase64)
        $ErrorActionPreference = 'Stop'
        function Write-Log([string]$Message) { Add-Content -LiteralPath $Log -Value "$(Get-Date -Format s) $Message" }
        $failed = $false
        try {
            Write-Log "Waiting for process $ProcessId to exit before updating $Target"
            try { Wait-Process -Id $ProcessId -Timeout 600 -ErrorAction SilentlyContinue } catch { }
            $deadline = (Get-Date).AddMinutes(2)
            while ((Get-Date) -lt $deadline -and @(Get-Process -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -and $_.Path.StartsWith($Target + '\', [StringComparison]::OrdinalIgnoreCase) } catch { $false } }).Count -gt 0) { Start-Sleep -Milliseconds 500 }
            foreach ($pass in @(@('/XF', 'BUILD-PROVENANCE.json'), @('/IF', 'BUILD-PROVENANCE.json', '/IS'))) {
                $code = 16
                for ($attempt = 0; $attempt -lt 15 -and $code -ge 8; $attempt++) { if ($attempt -gt 0) { Start-Sleep -Seconds 2 }; & robocopy.exe $Source $Target /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP @pass | Out-Null; $code = $LASTEXITCODE }
                if ($code -ge 8) { throw "robocopy failed with exit code $code" }
            }
            $uninstall = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioTranscriber'
            if (Test-Path $uninstall) { try { $tag = (Get-Content (Join-Path $Target 'BUILD-PROVENANCE.json') -Raw | ConvertFrom-Json).tag; Set-ItemProperty -LiteralPath $uninstall -Name DisplayVersion -Value $tag.TrimStart('v') } catch { } }
            Remove-Item -LiteralPath $Source -Recurse -Force -ErrorAction SilentlyContinue
            Write-Log 'Update applied.'
        }
        catch { $failed = $true; Write-Log "Update failed: $_" }
        if ($Relaunch -eq '1') { $arguments = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ArgumentsBase64)); $exe = Join-Path $Target 'AudioTranscriber.App.exe'; if ($arguments) { Start-Process -FilePath $exe -ArgumentList $arguments } else { Start-Process -FilePath $exe } }
        """;

    private const string LinuxApplyScript = """
        #!/bin/sh
        PID="$1"; SOURCE="$2"; TARGET="$3"; LOG="$4"; RELAUNCH="$5"; ARGS64="$6"; KIND="$7"; PACKAGE="$8"; PAGE="$9"
        log() { printf '%s %s\n' "$(date -Iseconds)" "$*" >> "$LOG"; }
        i=0; while kill -0 "$PID" 2>/dev/null && [ "$i" -lt 600 ]; do sleep 1; i=$((i+1)); done
        if [ "$KIND" = "linux-tar" ]; then
          log "Copying staged update to $TARGET"
          cp -a "$SOURCE"/. "$TARGET"/ && rm -rf "$SOURCE"
          if [ "$RELAUNCH" = "1" ]; then ARGS=$(printf '%s' "$ARGS64" | base64 -d 2>/dev/null || true); sh -c 'exec "$0" $1' "$TARGET/AudioTranscriber.App" "$ARGS" >/dev/null 2>&1 & fi
          exit 0
        fi
        FILE="$SOURCE/$PACKAGE"
        if command -v pkexec >/dev/null 2>&1; then
          if [ "$KIND" = "linux-deb" ]; then pkexec env DEBIAN_FRONTEND=noninteractive apt-get install -y "$FILE" >> "$LOG" 2>&1 && exit 0; fi
          if [ "$KIND" = "linux-rpm" ]; then if command -v dnf >/dev/null 2>&1; then pkexec dnf install -y "$FILE" >> "$LOG" 2>&1 && exit 0; else pkexec rpm -Uvh "$FILE" >> "$LOG" 2>&1 && exit 0; fi; fi
        fi
        log "Could not start a privileged package installer. Opening release page."
        if command -v xdg-open >/dev/null 2>&1 && [ -n "$PAGE" ]; then xdg-open "$PAGE" >/dev/null 2>&1 & fi
        exit 1
        """;
}
