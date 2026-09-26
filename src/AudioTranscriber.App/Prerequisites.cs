using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using AudioTranscriber.Audio;

namespace AudioTranscriber.App;

/// <summary>Startup check for external runtime pieces the app cannot run without.</summary>
public static class Prerequisites
{
    // Whisper.net's native libraries import these; builds from MSVC 14.40+ crash against older msvcp140.
    private static readonly string[] VcRuntimeFiles = ["msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll"];
    private static readonly Version MinimumVcRuntime = new(14, 40);
    private const string VcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    public sealed record Report(string? FFmpeg, string? FFprobe, bool VcRuntimeReady)
    {
        public bool MediaToolsReady => FFmpeg is not null && FFprobe is not null;
        public bool AllReady => MediaToolsReady && VcRuntimeReady;

        public string Summary
        {
            get
            {
                var lines = new List<string>
                {
                    MediaToolsReady ? $"FFmpeg: ready ({FFmpeg})"
                        : "FFmpeg: MISSING. Recording normalization and imports will pause. " + MediaToolLocator.MissingMessage(FFmpeg is null ? "ffmpeg" : "ffprobe"),
                    VcRuntimeReady ? "Microsoft Visual C++ runtime (x64): ready"
                        : $"Microsoft Visual C++ runtime (x64): missing or older than {MinimumVcRuntime}. Local Whisper cannot run until it is installed."
                };
                return string.Join(Environment.NewLine, lines);
            }
        }
    }

    public static Report Check() => new(MediaToolLocator.TryFind("ffmpeg"), MediaToolLocator.TryFind("ffprobe"), IsVcRuntimeReady());

    public static bool IsVcRuntimeReady()
    {
        foreach (var file in VcRuntimeFiles)
        {
            var path = Path.Combine(Environment.SystemDirectory, file);
            if (!File.Exists(path)) return false;
            var info = FileVersionInfo.GetVersionInfo(path);
            if (new Version(info.FileMajorPart, info.FileMinorPart) < MinimumVcRuntime) return false;
        }
        return true;
    }

    /// <summary>Downloads Microsoft's official x64 redistributable and runs it (UAC prompt). Returns a user-facing result.</summary>
    public static async Task<string> InstallVcRuntimeAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "AudioTranscriber");
        Directory.CreateDirectory(folder);
        var installer = Path.Combine(folder, "vc_redist.x64.exe");
        progress?.Report("Downloading the Microsoft Visual C++ runtime installer…");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        using (var response = await http.GetAsync(VcRedistUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var host = response.RequestMessage?.RequestUri;
            if (host is null || host.Scheme != Uri.UriSchemeHttps ||
                !(host.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase) || host.Host == "aka.ms"))
                throw new InvalidDataException("The runtime download was redirected to an unexpected host.");
            await using var file = new FileStream(installer, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(file, cancellationToken);
        }
        progress?.Report("Waiting for the Microsoft Visual C++ runtime installer (approve the Windows prompt)…");
        int exitCode;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(installer, "/install /passive /norestart")
            { UseShellExecute = true, Verb = "runas" }) ?? throw new InvalidOperationException("The installer did not start.");
            await process.WaitForExitAsync(cancellationToken);
            exitCode = process.ExitCode;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return "Visual C++ runtime installation was canceled at the Windows prompt. Local Whisper stays unavailable until it is installed.";
        }
        finally { try { File.Delete(installer); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        return exitCode switch
        {
            0 or 1638 when IsVcRuntimeReady() => "Microsoft Visual C++ runtime is installed. Local Whisper can run.",
            3010 or 1641 => "Microsoft Visual C++ runtime is installed; restart Windows before using local Whisper.",
            1602 => "Visual C++ runtime installation was canceled. Local Whisper stays unavailable until it is installed.",
            _ => IsVcRuntimeReady() ? "Microsoft Visual C++ runtime is installed. Local Whisper can run."
                : $"The Visual C++ runtime installer exited with code {exitCode}. Install it manually from {VcRedistUrl}."
        };
    }
}
