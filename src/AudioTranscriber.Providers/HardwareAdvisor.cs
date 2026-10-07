using System.Globalization;
using Microsoft.Win32;

namespace AudioTranscriber.Providers;

/// <summary>A graphics card with dedicated memory. NVIDIA cards come from nvidia-smi, others from the Windows display driver.</summary>
public sealed record GpuInfo(string Name, double MemoryGb, bool IsNvidia, bool CanRunParakeet, int? NvidiaIndex = null);

public sealed record HardwareProfile(string CpuName, int LogicalCores, double RamGb, IReadOnlyList<GpuInfo> Gpus)
{
    public string Describe() =>
        $"{CpuName} ({LogicalCores} threads), {RamGb:0} GB RAM; " +
        (Gpus.Count == 0 ? "no graphics card with dedicated memory"
            : string.Join(", ", Gpus.Select(gpu => $"{gpu.Name} ({gpu.MemoryGb:0.#} GB)")));
}

public enum PlanDevice { Cpu, Gpu, Hosted }

public sealed record VramUse(string Label, double Gb);

/// <summary>What fits on this PC: where each engine should run and which model sizes to use.</summary>
public sealed record HardwarePlan(
    HardwareProfile Hardware,
    bool LocalLlm,
    GpuInfo? Gpu,
    double UsableVramGb,
    IReadOnlyList<VramUse> VramUses,
    bool ParakeetOnGpu,
    string ParakeetReason,
    string WhisperModelId,
    bool WhisperOnGpu,
    string WhisperReason,
    string? LlmModel,
    PlanDevice LlmDevice,
    string LlmReason)
{
    public double VramPlannedGb => VramUses.Sum(use => use.Gb);

    public string Summary
    {
        get
        {
            var lines = new List<string>
            {
                $"Transcription (Parakeet): {(ParakeetOnGpu ? "GPU" : "CPU")}. {ParakeetReason}",
                $"Whisper (optional engine): {WhisperModelId} on the {(WhisperOnGpu ? "GPU" : "CPU")}. {WhisperReason}",
                "Template LLM: " + (LlmDevice switch
                {
                    PlanDevice.Gpu => $"{LlmModel} in Ollama on the GPU. ",
                    PlanDevice.Cpu => $"{LlmModel} in Ollama on the CPU. ",
                    _ => LocalLlm ? "a hosted connection. " : "hosted (your templates use an internet connection). "
                }) + LlmReason
            };
            if (Gpu is not null)
                lines.Add($"GPU memory: {VramPlannedGb:0.#} of {UsableVramGb:0.#} GB usable on {Gpu.Name} planned" +
                          (VramUses.Count == 0 ? "." : " (" + string.Join(", ", VramUses.Select(use => $"{use.Label} {use.Gb:0.#} GB")) + ")."));
            return string.Join(Environment.NewLine, lines);
        }
    }
}

public sealed record LlmFootprint(string Model, string DisplayName, double SizeGb)
{
    /// <summary>Weights plus Ollama's runtime (0.5 GB) and a long-transcript context (1 GB).</summary>
    public double VramGb => SizeGb + 1.5;
}

/// <summary>
/// Fits transcription and the template LLM onto this PC, like a self-hosting calculator. Footprints are planning
/// estimates (upstream model sizes plus runtime), not measurements. Pure: it reads nothing.
/// </summary>
public static class HardwareAdvisor
{
    public const double ParakeetGpuGb = 3.0;
    public const double MinimumReserveGb = 0.8;

    /// <summary>Ollama models for templates, smartest first (sizes match Martlet's footprint catalog).</summary>
    public static IReadOnlyList<LlmFootprint> LlmLadder { get; } =
    [
        new("gemma4:26b", "Gemma 4 26B", 18.7),
        new("gemma4:12b", "Gemma 4 12B", 8.0),
        new("gemma4:e4b", "Gemma 4 E4B", 6.6),
        new("gemma4:e2b", "Gemma 4 E2B", 4.6)
    ];

    /// <summary>whisper.cpp memory while transcribing, by catalog model id.</summary>
    public static IReadOnlyDictionary<string, double> WhisperGb { get; } = new Dictionary<string, double>
    {
        ["tiny"] = 0.4, ["base"] = 0.5, ["small"] = 1.0, ["large-v3-turbo"] = 2.5, ["large-v3"] = 4.0
    };

    /// <summary>Memory left for the desktop and driver: at least 0.8 GB, 10% of bigger cards.</summary>
    public static double Reserve(double memoryGb) => Math.Max(MinimumReserveGb, memoryGb * 0.1);

    public static HardwarePlan Plan(HardwareProfile hardware, bool localLlm, bool parakeetGpuSupported = true)
    {
        // Everything shares the largest card; Ollama, whisper.cpp and the Parakeet worker all pick it first.
        var gpu = hardware.Gpus.OrderByDescending(item => item.MemoryGb).FirstOrDefault();
        var usable = gpu is null ? 0 : Math.Max(0, gpu.MemoryGb - Reserve(gpu.MemoryGb));
        var free = usable;
        var uses = new List<VramUse>();

        // 1. The LLM first: it is unusably slow on the CPU, while Parakeet is fast there already.
        string? llmModel = null;
        PlanDevice llmDevice;
        string llmReason;
        if (!localLlm)
        {
            llmDevice = PlanDevice.Hosted;
            llmReason = "No GPU memory is kept for it.";
        }
        else if (gpu is not null && LlmLadder.FirstOrDefault(model => model.VramGb <= free) is { } fit)
        {
            llmModel = fit.Model;
            llmDevice = PlanDevice.Gpu;
            free -= fit.VramGb;
            uses.Add(new(fit.DisplayName, fit.VramGb));
            llmReason = $"The largest Gemma 4 that fits fully in GPU memory ({fit.VramGb:0.#} GB with context).";
        }
        else if (hardware.RamGb >= 16)
        {
            var smallest = LlmLadder[^1];
            llmModel = smallest.Model;
            llmDevice = PlanDevice.Cpu;
            llmReason = (gpu is null ? "No graphics card with dedicated memory, so " : $"{gpu.Name} is too small for {smallest.DisplayName} ({smallest.VramGb:0.#} GB), so ") +
                        "it runs on the CPU: slow (minutes per template). A hosted connection (OpenRouter, NVIDIA Build) is much faster.";
        }
        else
        {
            llmDevice = PlanDevice.Hosted;
            llmReason = $"Not enough GPU memory or RAM ({hardware.RamGb:0} GB) for a local model. Use a hosted connection (OpenRouter, NVIDIA Build).";
        }

        // 2. Parakeet on the GPU only when the card can run CUDA 12 and has room left after the LLM.
        var cudaGpu = !parakeetGpuSupported ? null : gpu?.CanRunParakeet == true ? gpu
            : hardware.Gpus.Where(item => item.CanRunParakeet).OrderByDescending(item => item.MemoryGb).FirstOrDefault();
        var cudaFree = cudaGpu is null ? 0 : cudaGpu == gpu ? free : cudaGpu.MemoryGb - Reserve(cudaGpu.MemoryGb);
        var parakeetGpu = cudaGpu is not null && cudaFree >= ParakeetGpuGb;
        string parakeetReason;
        if (cudaGpu is null && !parakeetGpuSupported)
            parakeetReason = "GPU Parakeet is Windows-only for now; the CPU is fast already (about 15× real time).";
        else if (cudaGpu is null)
            parakeetReason = hardware.Gpus.FirstOrDefault(item => item.IsNvidia) is { } unconfirmed
                ? $"nvidia-smi couldn't confirm that {unconfirmed.Name} runs CUDA 12 (compute 6.0+, 4 GB+, driver 527.41+). The CPU is fast already (about 15× real time)."
                : "No NVIDIA GPU that runs CUDA 12 (GTX 10-series or newer, 4 GB+). The CPU is fast already (about 15× real time).";
        else if (parakeetGpu && cudaGpu != gpu)
            parakeetReason = $"Runs on {cudaGpu.Name}, leaving the larger card to the rest.";
        else if (parakeetGpu)
        {
            free -= ParakeetGpuGb;
            uses.Add(new("Parakeet", ParakeetGpuGb));
            parakeetReason = llmDevice == PlanDevice.Gpu
                ? $"Fits beside {llmModel} ({ParakeetGpuGb:0} GB)."
                : $"Needs about {ParakeetGpuGb:0} GB of the GPU; faster on long imports with almost no CPU load.";
        }
        else if (cudaGpu == gpu && llmDevice == PlanDevice.Gpu)
            parakeetReason = $"The GPU is too small for both, so {llmModel} gets it; Parakeet is fast on the CPU (about 15× real time).";
        else
            parakeetReason = $"{cudaGpu.Name} has too little free memory ({cudaFree:0.#} GB, needs {ParakeetGpuGb:0}); the CPU is fast already.";

        // 3. Whisper with whatever is left: the biggest model the GPU or CPU handles in real time.
        string whisper;
        bool whisperGpu;
        string whisperReason;
        var cores = hardware.LogicalCores;
        if (gpu is not null && free >= WhisperGb["large-v3-turbo"])
        {
            (whisper, whisperGpu) = ("large-v3-turbo", true);
            whisperReason = "Fits in the GPU memory left over (through Vulkan).";
        }
        else if (cores >= 12 && hardware.RamGb >= 8)
        {
            (whisper, whisperGpu) = ("large-v3-turbo", false);
            whisperReason = gpu is null ? $"No usable GPU; {cores} CPU threads run it fast enough."
                : $"The GPU is full with the engines above; {cores} CPU threads run it fast enough.";
        }
        else if (gpu is not null && free >= WhisperGb["small"])
        {
            (whisper, whisperGpu) = ("small", true);
            whisperReason = $"Only {free:0.#} GB of GPU memory is left, enough for the small model.";
        }
        else if (cores >= 6)
        {
            (whisper, whisperGpu) = ("small", false);
            whisperReason = $"{cores} CPU threads keep up with the small model; large-v3-turbo would fall behind.";
        }
        else
        {
            (whisper, whisperGpu) = ("base", false);
            whisperReason = $"Only {cores} CPU threads; larger models would fall behind live audio.";
        }
        if (whisperGpu) uses.Add(new($"Whisper {whisper}", WhisperGb[whisper]));

        return new(hardware, localLlm, gpu, Math.Round(usable, 1), uses, parakeetGpu, parakeetReason.Trim(),
            whisper, whisperGpu, whisperReason, llmModel, llmDevice, llmReason);
    }
}

/// <summary>Reads this PC's CPU, RAM and graphics cards.</summary>
public static class HardwareProbe
{
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static async Task<HardwareProfile> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var nvidia = await GpuProbe.QueryNvidiaGpusAsync(cancellationToken);
        var gpus = nvidia.Select(gpu => new GpuInfo(gpu.Name, Math.Round(gpu.MemoryMiB / 1024.0, 1), true,
            GpuProbe.SelectCudaGpu([gpu]) is not null, gpu.Index)).ToList();
        foreach (var (name, bytes) in DisplayAdapters())
        {
            if (nvidia.Count > 0 && name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) continue;
            // Integrated GPUs report a small carve-out of shared memory; they don't count as dedicated memory.
            var gb = Math.Round(bytes / 1073741824.0, 1);
            if (gb < 2.5 || name.Contains("UHD", StringComparison.OrdinalIgnoreCase) || name.Contains("Iris", StringComparison.OrdinalIgnoreCase)) continue;
            gpus.Add(new(name, gb, name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase), false));
        }
        return new(CpuName(), Environment.ProcessorCount, Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0), gpus);
    }

    private static string CpuName()
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
                return line?.Split(':', 2) is [_, var model] && model.Trim().Length > 0 ? model.Trim() : "CPU";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return "CPU"; }
        }
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() is { Length: > 0 } name ? name : "CPU";
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return "CPU"; }
    }

    private static IEnumerable<(string Name, long Bytes)> DisplayAdapters()
    {
        var found = new List<(string, long)>();
        if (!OperatingSystem.IsWindows()) return LinuxDisplayAdapters();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (root is null) return found;
            foreach (var child in root.GetSubKeyNames().Where(name => name.All(char.IsDigit)))
            {
                using var key = root.OpenSubKey(child);
                if (key?.GetValue("DriverDesc") is not string name) continue;
                var bytes = ReadSize(key.GetValue("HardwareInformation.qwMemorySize")) ?? ReadSize(key.GetValue("HardwareInformation.MemorySize"));
                if (bytes is > 0) found.Add((name.Trim(), bytes.Value));
            }
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        return found.DistinctBy(item => item.Item1);
    }

    /// <summary>Linux: dedicated VRAM from the amdgpu sysfs counter; NVIDIA cards come from nvidia-smi.</summary>
    private static List<(string Name, long Bytes)> LinuxDisplayAdapters()
    {
        var found = new List<(string, long)>();
        try
        {
            foreach (var device in Directory.EnumerateDirectories("/sys/class/drm", "card*").Select(card => Path.Combine(card, "device")).Distinct())
            {
                var vram = Path.Combine(device, "mem_info_vram_total");
                if (!File.Exists(vram) || !long.TryParse(File.ReadAllText(vram).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)) continue;
                var uevent = Path.Combine(device, "uevent");
                var slot = File.Exists(uevent)
                    ? File.ReadLines(uevent).FirstOrDefault(l => l.StartsWith("PCI_SLOT_NAME=", StringComparison.Ordinal))?[14..] : null;
                found.Add((LspciName(slot) ?? "GPU", bytes));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return found.DistinctBy(item => item.Item1).ToList();
    }

    private static string? LspciName(string? slot)
    {
        if (slot is null) return null;
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("lspci", $"-mm -s {slot}")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            // -mm prints: slot "class" "vendor" "device" ...
            var fields = output.Split('"').Where((_, index) => index % 2 == 1).ToArray();
            return fields.Length >= 3 ? $"{fields[1]} {fields[2]}".Trim() : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    private static long? ReadSize(object? value) => value switch
    {
        long number => number,
        int number => (uint)number,
        byte[] { Length: >= 8 } raw => BitConverter.ToInt64(raw, 0),
        byte[] { Length: >= 4 } raw => BitConverter.ToUInt32(raw, 0),
        string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };
}
