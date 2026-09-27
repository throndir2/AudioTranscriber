using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AudioTranscriber.Providers;

public sealed record NvidiaGpu(int Index, string Name, int MemoryMiB, double ComputeCapability, string DriverVersion);

/// <summary>An NVIDIA speech NIM container profile that can serve Parakeet on this PC's GPU.</summary>
public sealed record LocalNimProfile(
    string Id, string DisplayName, string Container, string TagsSelector, string Locale,
    double GpuMemoryGb, int MinimumVramMiB)
{
    public string Image => "nvcr.io/nim/nvidia/" + Container + ":latest";
}

public static class LocalNimCatalog
{
    // Docker Desktop runs NIM through WSL 2, where NVIDIA supports only the Parakeet CTC models (ASR NIM support
    // matrix, 2026-09). CTC 1.1B is the more accurate of the two; its offline profile needs 5.83 GB of GPU memory.
    public static LocalNimProfile ParakeetCtc { get; } = new(
        "local-gpu-parakeet-ctc-1.1b", "Parakeet CTC 1.1B on this PC's GPU", "parakeet-1-1b-ctc-en-us",
        "mode=ofl,vad=default,diarizer=disabled", "en-US", 5.83, 7_600);
    public const double MinimumComputeCapability = 8.0;

    /// <summary>Picks the largest NVIDIA GPU that NVIDIA's speech NIM supports, when it has room for the profile.</summary>
    public static (NvidiaGpu Gpu, LocalNimProfile Profile)? Select(IEnumerable<NvidiaGpu> gpus)
    {
        var best = gpus.Where(gpu => gpu.ComputeCapability >= MinimumComputeCapability)
            .OrderByDescending(gpu => gpu.MemoryMiB).FirstOrDefault();
        return best is not null && best.MemoryMiB >= ParakeetCtc.MinimumVramMiB ? (best, ParakeetCtc) : null;
    }
}

public static class GpuProbe
{
    public static async Task<IReadOnlyList<NvidiaGpu>> QueryNvidiaGpusAsync(CancellationToken cancellationToken = default)
    {
        var smi = FindNvidiaSmi();
        if (smi is null) return [];
        try
        {
            var result = await ToolProcess.RunAsync(smi,
                ["--query-gpu=index,name,memory.total,compute_cap,driver_version", "--format=csv,noheader,nounits"],
                TimeSpan.FromSeconds(20), cancellationToken);
            return result.ExitCode == 0 ? Parse(result.Output) : [];
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or TimeoutException) { return []; }
    }

    internal static IReadOnlyList<NvidiaGpu> Parse(string csv)
    {
        var gpus = new List<NvidiaGpu>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length < 5 ||
                !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
                !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var memory) ||
                !double.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var capability))
                continue;
            gpus.Add(new(index, fields[1], memory, capability, fields[4]));
        }
        return gpus;
    }

    private static string? FindNvidiaSmi()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? ToolProcess.FindOnPath("nvidia-smi.exe");
    }
}

/// <summary>
/// Runs NVIDIA's Parakeet speech NIM in Docker on this PC's GPU. The container is created once, reused across
/// app runs (so its model download and TensorRT build are cached), bound to 127.0.0.1 only, and stopped on exit.
/// </summary>
public sealed class LocalNimHost
{
    public const string ContainerName = "audiotranscriber-parakeet";
    public const int GrpcPort = 59051;
    public const int HttpPort = 59000;
    private const string ProfileLabel = "audiotranscriber.profile";
    private readonly string docker;

    public LocalNimHost(string docker) => this.docker = docker;

    public static string? FindDocker() => ToolProcess.FindOnPath("docker.exe") ??
        new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe") }
            .FirstOrDefault(File.Exists);

    public async Task<bool> IsDockerRunningAsync(CancellationToken cancellationToken)
    {
        try { return (await Docker(["info", "--format", "{{.ServerVersion}}"], TimeSpan.FromSeconds(30), cancellationToken)).ExitCode == 0; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or TimeoutException) { return false; }
    }

    public async Task StartAsync(LocalNimProfile profile, NvidiaGpu gpu, NvidiaCredential key,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var existing = await Docker(["inspect", "-f", "{{.State.Running}}|{{index .Config.Labels \"" + ProfileLabel + "\"}}", ContainerName],
            TimeSpan.FromSeconds(30), cancellationToken);
        var state = existing.ExitCode == 0 ? existing.Output.Trim().Split('|') : [];
        if (state.Length == 2 && state[1] == profile.Id)
        {
            if (state[0] != "true")
            {
                progress?.Report($"Starting {profile.DisplayName} on {gpu.Name}…");
                await Require(Docker(["start", ContainerName], TimeSpan.FromMinutes(2), cancellationToken), "start the Parakeet container");
            }
        }
        else
        {
            if (existing.ExitCode == 0)
                await Require(Docker(["rm", "-f", ContainerName], TimeSpan.FromMinutes(1), cancellationToken), "replace the old Parakeet container");
            var secret = Encoding.UTF8.GetString(key.Encode());
            await Require(Docker(["login", "nvcr.io", "--username", "$oauthtoken", "--password-stdin"], TimeSpan.FromMinutes(2),
                cancellationToken, standardInput: secret), "sign in to NVIDIA's container registry with the NVIDIA key");
            var layers = 0;
            await Require(Docker(["pull", profile.Image], Timeout.InfiniteTimeSpan, cancellationToken, onLine: line =>
            {
                if (line.Contains("Pull complete", StringComparison.Ordinal) || line.Contains("Already exists", StringComparison.Ordinal))
                    progress?.Report($"Downloading NVIDIA's Parakeet GPU container (one time, about 9 GB): {++layers} layers done…");
            }), "download " + profile.Image);
            progress?.Report($"Creating the Parakeet container on {gpu.Name}…");
            // NGC_API_KEY is passed by name so the key comes from this process's environment, never the command line.
            await Require(Docker(RunArguments(profile, gpu.Index), TimeSpan.FromMinutes(5), cancellationToken,
                environment: new Dictionary<string, string> { ["NGC_API_KEY"] = secret }), "create the Parakeet container");
        }
        await WaitUntilReadyAsync(profile, gpu, progress, cancellationToken);
    }

    internal static IReadOnlyList<string> RunArguments(LocalNimProfile profile, int gpuIndex) =>
    [
        "run", "-d", "--name", ContainerName, "--label", ProfileLabel + "=" + profile.Id,
        "--gpus", "device=" + gpuIndex.ToString(CultureInfo.InvariantCulture), "--shm-size=8g", "--ulimit", "nofile=2048:2048",
        "-e", "NGC_API_KEY", "-e", "NIM_TAGS_SELECTOR=" + profile.TagsSelector,
        "-e", "NIM_HTTP_API_PORT=9000", "-e", "NIM_GRPC_API_PORT=50051",
        "-p", $"127.0.0.1:{HttpPort}:9000", "-p", $"127.0.0.1:{GrpcPort}:50051",
        profile.Image
    ];

    private async Task WaitUntilReadyAsync(LocalNimProfile profile, NvidiaGpu gpu, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var started = DateTime.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{HttpPort}/v1/health/ready", cancellationToken);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            var running = await Docker(["inspect", "-f", "{{.State.Running}}", ContainerName], TimeSpan.FromSeconds(30), cancellationToken);
            if (running.ExitCode != 0 || running.Output.Trim() != "true")
            {
                var logs = await Docker(["logs", "--tail", "3", ContainerName], TimeSpan.FromSeconds(30), cancellationToken);
                var last = (logs.Output + "\n" + logs.Error).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
                throw new InvalidOperationException("the Parakeet container stopped" + (last.Length > 0 ? ": " + last[..Math.Min(200, last.Length)] : "") +
                    $". See `docker logs {ContainerName}`.");
            }
            var minutes = (int)(DateTime.UtcNow - started).TotalMinutes;
            if (minutes >= 90) throw new TimeoutException("the Parakeet container did not become ready within 90 minutes.");
            progress?.Report($"Preparing {profile.DisplayName} on {gpu.Name} ({minutes} min; the first start downloads and optimizes the model, up to ~30 min)…");
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    public async Task StopAsync()
    {
        try { await Docker(["stop", "-t", "10", ContainerName], TimeSpan.FromSeconds(30), CancellationToken.None); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or TimeoutException) { }
    }

    private Task<ToolResult> Docker(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken,
        string? standardInput = null, IReadOnlyDictionary<string, string>? environment = null, Action<string>? onLine = null) =>
        ToolProcess.RunAsync(docker, arguments, timeout, cancellationToken, standardInput, environment, onLine);

    private static async Task Require(Task<ToolResult> run, string action)
    {
        var result = await run;
        if (result.ExitCode == 0) return;
        var detail = (result.Error + "\n" + result.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? $"exit code {result.ExitCode}";
        throw new InvalidOperationException($"Docker could not {action}: {detail[..Math.Min(240, detail.Length)]}");
    }
}

internal sealed record ToolResult(int ExitCode, string Output, string Error);

internal static class ToolProcess
{
    public static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), fileName)).FirstOrDefault(File.Exists);

    public static async Task<ToolResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken, string? standardInput = null, IReadOnlyDictionary<string, string>? environment = null,
        Action<string>? onLine = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = standardInput is not null,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null) foreach (var (name, value) in environment) start.Environment[name] = value;
        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is null) return; lock (output) output.AppendLine(e.Data); onLine?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is null) return; lock (error) error.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan) limit.CancelAfter(timeout);
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(fileName)} did not finish in time.");
        }
        process.WaitForExit();
        lock (output) lock (error) return new(process.ExitCode, output.ToString(), error.ToString());
    }
}
