using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AudioTranscriber.Providers;

public sealed record NvidiaGpu(int Index, string Name, int MemoryMiB, double ComputeCapability, string DriverVersion);

public static class GpuProbe
{
    // Parakeet's GPU runtime is CUDA 12.x + cuDNN 9.x: Windows driver 527.41+, Pascal (6.0) or newer.
    public const double MinimumComputeCapability = 6.0;
    public const int MinimumMemoryMiB = 4096;
    public static readonly Version MinimumDriver = new(527, 41);

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

    /// <summary>The largest NVIDIA GPU that can run Parakeet with CUDA 12, or null.</summary>
    public static NvidiaGpu? SelectCudaGpu(IEnumerable<NvidiaGpu> gpus) => gpus
        .Where(gpu => gpu.ComputeCapability >= MinimumComputeCapability && gpu.MemoryMiB >= MinimumMemoryMiB &&
                      Version.TryParse(gpu.DriverVersion, out var driver) && driver >= MinimumDriver)
        .OrderByDescending(gpu => gpu.MemoryMiB).FirstOrDefault();

    /// <summary>Why none of the GPUs qualifies, for display.</summary>
    public static string DescribeUnsupported(IReadOnlyList<NvidiaGpu> gpus) => gpus.Count == 0
        ? "No NVIDIA GPU was found."
        : string.Join("; ", gpus.Select(gpu =>
            $"{gpu.Name} ({gpu.MemoryMiB / 1024.0:0.#} GB, compute {gpu.ComputeCapability:0.0}, driver {gpu.DriverVersion})")) +
          $" can't run Parakeet with CUDA 12: it needs compute capability {MinimumComputeCapability:0.0}+ (GTX 10-series or newer), " +
          $"{MinimumMemoryMiB / 1024} GB of GPU memory, and NVIDIA driver {MinimumDriver}+.";

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

internal sealed record ToolResult(int ExitCode, string Output, string Error);

internal static class ToolProcess
{
    public static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), fileName)).FirstOrDefault(File.Exists);

    public static async Task<ToolResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(fileName)} did not finish in time.");
        }
        return new(process.ExitCode, await output, await error);
    }
}
