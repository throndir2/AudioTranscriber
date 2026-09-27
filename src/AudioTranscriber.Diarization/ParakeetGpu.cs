using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using AudioTranscriber.Core;
using ICSharpCode.SharpZipLib.BZip2;
using SherpaOnnx;

namespace AudioTranscriber.Diarization;

/// <summary>
/// Everything Parakeet needs to run on an NVIDIA GPU, pinned by size and SHA256: NVIDIA's redistributable CUDA 12.9
/// runtime, cuBLAS and cuDNN 9.21, sherpa-onnx 1.13.8's CUDA build of ONNX Runtime 1.28.2, and the full-precision
/// Parakeet TDT 0.6B v3 export (the CPU int8 export uses quantized operators that ONNX Runtime runs only on the CPU).
/// Only the NVIDIA display driver is needed on the PC; nothing is installed system-wide.
/// </summary>
public static class ParakeetGpuPackage
{
    public const string ModelName = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3 (fp32)";
    private const string SherpaRoot = "sherpa-onnx-v1.13.8-cuda-12.x-cudnn-9.x-onnxruntime1.28.2-win-x64-cuda";
    private const string CudnnRoot = "cudnn-windows-x86_64-9.21.1.3_cuda12-archive/bin/x64/";
    private const string ModelRevision = "1a468a35cbba69418f126de829e75261dea4a4e4";

    internal sealed record PinnedFile(string Entry, string Name, long Bytes, string Sha256, bool Model = false);
    internal sealed record Source(string Url, long Bytes, string Sha256, string Kind, PinnedFile[] Files);

    internal static readonly Source[] Sources =
    [
        new("https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.13.8/" + SherpaRoot + ".tar.bz2", 595_017_373,
            "066c5b54dbafaa1388001a9c9837ac1374dbba6d6678f193ca06aa0d8e94d8c3", "tar.bz2",
        [
            new(SherpaRoot + "/lib/sherpa-onnx-c-api.dll", "sherpa-onnx-c-api.dll", 4_605_952, "5332afa35b8dc7cb432df015a3664c82b12a056d69b68dc9db6e2d84426a4f73"),
            new(SherpaRoot + "/lib/onnxruntime.dll", "onnxruntime.dll", 16_283_448, "f202b5c9b0025993996d60f5b528cdb92ad12aa9b709b12a8089bb3403da30bd"),
            new(SherpaRoot + "/lib/onnxruntime_providers_cuda.dll", "onnxruntime_providers_cuda.dll", 496_742_712, "0204055e745b092b0d946558b914b61aa395a9fa41fc6e082904c1441de3ef4d"),
            new(SherpaRoot + "/lib/onnxruntime_providers_shared.dll", "onnxruntime_providers_shared.dll", 21_816, "f55a8a6216a63c5678f0ccca531403fd34e7325e0b43c771e134a3898c3e5120")
        ]),
        new("https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-12.9.79-archive.zip",
            3_521_238, "179e9c43b0735ffe67207b3da556eb5a0c50f3047961882b7657d3b822d34ef8", "zip",
        [
            new("cuda_cudart-windows-x86_64-12.9.79-archive/bin/cudart64_12.dll", "cudart64_12.dll", 583_680, "760c38928bbe5759f7b31ed6692599eb7ec83cedd5702e84c2b72028a89837e1")
        ]),
        new("https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-12.9.1.4-archive.zip",
            549_755_186, "d534d98b0b453a98914dbf3adf47d7e84b55037abf02f87466439e1dcef581ed", "zip",
        [
            new("libcublas-windows-x86_64-12.9.1.4-archive/bin/cublas64_12.dll", "cublas64_12.dll", 102_518_272, "90052a83efd1b57a8e3616a6590b335855f81b814a4f16eecb7b5bf6d1b1d4eb"),
            new("libcublas-windows-x86_64-12.9.1.4-archive/bin/cublasLt64_12.dll", "cublasLt64_12.dll", 668_669_952, "c3a05ea244c937314afec09f87b91f814c7e27977681f6c67eb51bb06ced3a4a")
        ]),
        new("https://developer.download.nvidia.com/compute/cudnn/redist/cudnn/windows-x86_64/cudnn-windows-x86_64-9.21.1.3_cuda12-archive.zip",
            676_848_984, "b08e44a464e8d534f334cf9b9aadaecb0614c81bcfeb8b0a30c1fd9890620120", "zip",
        [
            new(CudnnRoot + "cudnn64_9.dll", "cudnn64_9.dll", 264_304, "1ac8e84bb6b4c049259dfc32942b91c4b5a0db165b8f14bc64086eb32ad4ff27"),
            new(CudnnRoot + "cudnn_adv64_9.dll", "cudnn_adv64_9.dll", 269_016_688, "48bf01f154d8b973d4eecaba7eac10a6c8dc978f48e989adc76262cd27311262"),
            new(CudnnRoot + "cudnn_cnn64_9.dll", "cudnn_cnn64_9.dll", 2_984_560, "7c025c5cd90a68420c4ea4570489ba47945d874fa010ec4b8b23871f25fc5e2d"),
            new(CudnnRoot + "cudnn_engines_precompiled64_9.dll", "cudnn_engines_precompiled64_9.dll", 482_077_808, "ade50dd872948ee10f61babf1b51ed890df3e96ac4b3e703bf69823155a4cfa5"),
            new(CudnnRoot + "cudnn_engines_runtime_compiled64_9.dll", "cudnn_engines_runtime_compiled64_9.dll", 31_965_808, "eb716310c9a8d41ff2c10bcf766e4e067c3eec6f3d6301ab2febdcefbad1701f"),
            new(CudnnRoot + "cudnn_engines_tensor_ir64_9.dll", "cudnn_engines_tensor_ir64_9.dll", 155_248, "8a90fd3ce2e5110741017a8a434fc5d31cf61ba5adcdeb8b0085a8226c793580"),
            new(CudnnRoot + "cudnn_graph64_9.dll", "cudnn_graph64_9.dll", 99_894_384, "16b2868d4952ec68d2ad6f13a85f423844033df579276dc47fc806a521cbdcd0"),
            new(CudnnRoot + "cudnn_heuristic64_9.dll", "cudnn_heuristic64_9.dll", 61_770_864, "ad6b92dd11365d4fda9942c79b79cd7c45e0184e3b419bc3f00ab68b308f9635"),
            new(CudnnRoot + "cudnn_ops64_9.dll", "cudnn_ops64_9.dll", 105_601_136, "43a7847e8b9e05ea31e56978b2d27fc878cd92ba816e1278bff02f1bff839758")
        ]),
        ModelFile("encoder.onnx", 41_766_257, "3eed7ce424bf8339ad09233533c687e2dbd07e74ccf5027b5e7344019ea373b0"),
        ModelFile("encoder.weights", 2_435_420_160, "3af3f51af5f2d01dbbf5af47d42c7962a2c205f11004254bb4f2b979862f39a8"),
        ModelFile("decoder.onnx", 47_233_743, "d593cdb0e571f5a457ec2219af9968cbf6b0e8198e8f7839b40a8754593bf68c"),
        ModelFile("joiner.onnx", 25_286_330, "b9b0bcf88ac571902e69a6536223ed2d94885e981b85045410f1403d53121a63"),
        ModelFile("tokens.txt", 93_939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d")
    ];

    public static long DownloadBytes => Sources.Sum(source => source.Bytes);
    public static long InstalledBytes => Sources.SelectMany(source => source.Files).Sum(file => file.Bytes);
    public static string Directory(string parent) => Path.Combine(parent, "parakeet-gpu");
    public static string RuntimeDirectory(string parent) => Path.Combine(Directory(parent), "runtime");
    public static string ModelDirectory(string parent) => Path.Combine(Directory(parent), "model");

    public const string LicenseNotice =
        "NVIDIA CUDA runtime and cuBLAS: CUDA Toolkit EULA, https://docs.nvidia.com/cuda/eula/\n" +
        "NVIDIA cuDNN: cuDNN Software License Agreement, https://docs.nvidia.com/deeplearning/cudnn/latest/reference/eula.html\n" +
        "ONNX Runtime (MIT) and sherpa-onnx (Apache-2.0) CUDA build, https://github.com/k2-fsa/sherpa-onnx\n" +
        "NVIDIA Parakeet TDT 0.6B v3: CC BY 4.0, https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3 (ONNX export by sherpa-onnx)\n";

    private static Source ModelFile(string name, long bytes, string sha256) => new(
        $"https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3/resolve/{ModelRevision}/{name}",
        bytes, sha256, "file", [new(name, name, bytes, sha256, Model: true)]);

    private static string Target(string parent, PinnedFile file) =>
        Path.Combine(file.Model ? ModelDirectory(parent) : RuntimeDirectory(parent), file.Name);

    /// <summary>True when every pinned file is present with its size (hashes are checked when installed).</summary>
    public static bool IsInstalled(string parent) => Sources.SelectMany(source => source.Files)
        .All(file => new FileInfo(Target(parent, file)) is { Exists: true } info && info.Length == file.Bytes);

    public static async Task InstallAsync(string parent, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        parent = LocalPaths.RequireDirectoryPath(parent);
        System.IO.Directory.CreateDirectory(RuntimeDirectory(parent));
        System.IO.Directory.CreateDirectory(ModelDirectory(parent));
        var downloads = Path.Combine(Directory(parent), "downloads");
        System.IO.Directory.CreateDirectory(downloads);
        var remaining = Sources.Where(source => !source.Files.All(file =>
            new FileInfo(Target(parent, file)) is { Exists: true } info && info.Length == file.Bytes)).ToArray();
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(parent))!).AvailableFreeSpace;
        var needed = remaining.Sum(source => source.Files.Sum(file => file.Bytes)) + remaining.Max(source => (long?)source.Bytes) ?? 0;
        if (free < needed + 512L * 1024 * 1024)
            throw new IOException($"Not enough free disk space for the GPU runtime: {needed / 1_073_741_824.0:0.0} GB needed, {free / 1_073_741_824.0:0.0} GB free.");
        var total = remaining.Sum(source => source.Bytes);
        long done = 0;
        using var client = new HttpClient { Timeout = TimeSpan.FromHours(3) };
        foreach (var source in remaining)
        {
            var archive = Path.Combine(downloads, Path.GetFileName(new Uri(source.Url).LocalPath));
            var before = done;
            await DiarizationModels.DownloadAsync(client, new Uri(source.Url), archive, source.Bytes, source.Sha256,
                new InlineProgress<long>(bytes => progress?.Report(
                    $"Downloading the GPU runtime for Parakeet: {(before + bytes) * 100 / total}% " +
                    $"({(before + bytes) / 1_048_576:N0} / {total / 1_048_576:N0} MiB)…")), cancellationToken, maximumBytes: source.Bytes);
            done += source.Bytes;
            try
            {
                if (source.Kind == "file") await PublishAsync(archive, Target(parent, source.Files[0]), source.Files[0], move: true, cancellationToken);
                else
                {
                    progress?.Report($"Unpacking {Path.GetFileName(archive)}…");
                    if (source.Kind == "zip") await ExtractZipAsync(archive, parent, source.Files, cancellationToken);
                    else await ExtractTarBz2Async(archive, parent, source.Files, cancellationToken);
                }
            }
            finally { if (File.Exists(archive)) File.Delete(archive); }
        }
        await File.WriteAllTextAsync(Path.Combine(Directory(parent), "NOTICE.txt"), LicenseNotice, cancellationToken);
        System.IO.Directory.Delete(downloads, true);
        if (!IsInstalled(parent)) throw new InvalidDataException("The GPU runtime installation is incomplete.");
    }

    internal static async Task ExtractZipAsync(string archive, string parent, IReadOnlyList<PinnedFile> files, CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var file in files)
        {
            var entry = zip.GetEntry(file.Entry) ?? throw new InvalidDataException("A pinned file is missing from its archive.");
            if (entry.Length != file.Bytes) throw new InvalidDataException("A pinned archive entry has an unexpected size.");
            var partial = Target(parent, file) + ".partial";
            await using (var input = entry.Open())
                await CopyBoundedAsync(input, partial, file.Bytes, cancellationToken);
            await PublishAsync(partial, Target(parent, file), file, move: true, cancellationToken);
        }
    }

    internal static async Task ExtractTarBz2Async(string archive, string parent, IReadOnlyList<PinnedFile> files, CancellationToken cancellationToken)
    {
        var wanted = files.ToDictionary(file => file.Entry, StringComparer.Ordinal);
        await using var stream = File.OpenRead(archive);
        using var decompressed = new BZip2InputStream(stream);
        using var reader = new TarReader(decompressed, leaveOpen: true);
        var entries = 0;
        TarEntry? entry;
        while (wanted.Count > 0 && (entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken)) is not null)
        {
            if (++entries > 500) throw new InvalidDataException("The runtime archive has too many entries.");
            if (!wanted.Remove(entry.Name, out var file)) continue;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.Length != file.Bytes ||
                entry.DataStream is null)
                throw new InvalidDataException("A pinned archive entry is not the expected file.");
            var partial = Target(parent, file) + ".partial";
            await CopyBoundedAsync(entry.DataStream, partial, file.Bytes, cancellationToken);
            await PublishAsync(partial, Target(parent, file), file, move: true, cancellationToken);
        }
        if (wanted.Count > 0) throw new InvalidDataException("A pinned file is missing from its archive.");
    }

    private static async Task CopyBoundedAsync(Stream input, string destination, long bytes, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
        var buffer = new byte[1 << 20];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if ((written += read) > bytes) throw new InvalidDataException("An archive entry exceeded its pinned size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (written != bytes) throw new InvalidDataException("An archive entry was truncated.");
    }

    private static async Task PublishAsync(string source, string target, PinnedFile file, bool move, CancellationToken cancellationToken)
    {
        try
        {
            await DiarizationModels.VerifyFileAsync(source, file.Bytes, file.Sha256, cancellationToken);
            if (move) File.Move(source, target, true);
        }
        finally { if (File.Exists(source) && move) File.Delete(source); }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

/// <summary>
/// App side of the GPU worker: a long-lived child process that keeps Parakeet loaded on the GPU and decodes one
/// PCM16 chunk per request (JSON lines over stdin/stdout). A native CUDA failure only ends that process.
/// </summary>
public sealed class ParakeetGpuWorker : IAsyncDisposable
{
    private readonly ProcessStartInfo start;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Channel<string> lines = Channel.CreateUnbounded<string>();
    private readonly Queue<string> errors = new();
    private Process? process;
    private bool disposed;
    public string Provider { get; }
    public long LoadMilliseconds { get; private set; }

    /// <param name="workerExecutable">AudioTranscriber.Worker.exe, or its .dll with <paramref name="dotnetHostPath"/>.</param>
    public ParakeetGpuWorker(string workerExecutable, string? dotnetHostPath, string modelsParent, int deviceIndex, string provider = "cuda")
    {
        Provider = provider;
        var runtime = ParakeetGpuPackage.RuntimeDirectory(modelsParent);
        start = new ProcessStartInfo(dotnetHostPath ?? workerExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(workerExecutable)!,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (dotnetHostPath is not null) start.ArgumentList.Add(workerExecutable);
        foreach (var argument in new[] { "--parakeet-server", runtime, ParakeetGpuPackage.ModelDirectory(modelsParent), provider })
            start.ArgumentList.Add(argument);
        // cuDNN loads its sub-libraries by name; PCI order makes the index match nvidia-smi's.
        start.Environment["PATH"] = runtime + Path.PathSeparator + start.Environment["PATH"];
        start.Environment["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID";
        start.Environment["CUDA_VISIBLE_DEVICES"] = deviceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public string LastErrors { get { lock (errors) return string.Join(" | ", errors); } }

    /// <summary>Starts the worker and waits until the model is loaded and a warm-up chunk has decoded on the device.</summary>
    public async Task StartAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        process = Process.Start(start) ?? throw new InvalidOperationException("The GPU worker did not start.");
        _ = PumpAsync(process.StandardOutput, line => lines.Writer.TryWrite(line), () => lines.Writer.TryComplete());
        _ = PumpAsync(process.StandardError, line =>
        {
            lock (errors) { errors.Enqueue(line.Length > 300 ? line[..300] : line); while (errors.Count > 8) errors.Dequeue(); }
        }, () => { });
        var ready = await ReadAsync(timeout, cancellationToken);
        using var document = JsonDocument.Parse(ready);
        if (!document.RootElement.TryGetProperty("ready", out var flag) || !flag.GetBoolean())
            throw new InvalidOperationException("The GPU worker did not report ready.");
        LoadMilliseconds = document.RootElement.GetProperty("loadMs").GetInt64();
    }

    public async Task<string> DecodeAsync(string pcmPath, long samples, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (process is not { HasExited: false } running) throw new InvalidOperationException("The GPU worker is not running.");
            await running.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { path = pcmPath, samples }).AsMemory(), cancellationToken);
            await running.StandardInput.FlushAsync(cancellationToken);
            using var document = JsonDocument.Parse(await ReadAsync(TimeSpan.FromSeconds(60), cancellationToken));
            if (document.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException("The GPU worker failed a chunk: " + error.GetString());
            return document.RootElement.GetProperty("json").GetString() ?? throw new InvalidDataException("Empty GPU result.");
        }
        finally { gate.Release(); }
    }

    private async Task<string> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            if (await lines.Reader.WaitToReadAsync(limit.Token) && lines.Reader.TryRead(out var line)) return line;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill();
            throw new TimeoutException("The GPU worker did not respond in time.");
        }
        await Task.Delay(200, CancellationToken.None);
        var code = process is { HasExited: true } exited ? exited.ExitCode : 0;
        throw new InvalidOperationException($"The GPU worker stopped (exit {code}): {LastErrors}");
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine, Action onEnd)
    {
        try { while (await reader.ReadLineAsync() is { } line) onLine(line); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally { onEnd(); }
    }

    private void Kill()
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (process is null) return;
        try { process.StandardInput.Close(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(wait.Token); } catch (OperationCanceledException) { Kill(); }
        process.Dispose();
    }
}

/// <summary>Worker-process side: loads the CUDA build of sherpa-onnx / ONNX Runtime from the GPU runtime folder.</summary>
public static class ParakeetGpuServer
{
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string path);

    public static async Task<int> RunAsync(string runtimeDirectory, string modelDirectory, string provider, TextReader input, TextWriter output)
    {
        var runtime = Path.GetFullPath(runtimeDirectory);
        SetDllDirectory(runtime);
        // Bind the sherpa P/Invokes (managed wrapper and ours) to the CUDA build, and its onnxruntime.dll before anything else.
        NativeLibrary.Load(Path.Combine(runtime, "onnxruntime.dll"));
        var sherpa = NativeLibrary.Load(Path.Combine(runtime, "sherpa-onnx-c-api.dll"));
        DllImportResolver resolve = (name, _, _) => name.StartsWith("sherpa-onnx-c-api", StringComparison.OrdinalIgnoreCase) ? sherpa : IntPtr.Zero;
        NativeLibrary.SetDllImportResolver(typeof(OfflineRecognizer).Assembly, resolve);
        NativeLibrary.SetDllImportResolver(typeof(SherpaParakeetProvider).Assembly, resolve);

        var watch = Stopwatch.StartNew();
        using var recognizer = SherpaParakeetProvider.CreateRecognizer(modelDirectory, "", 2, provider);
        SherpaParakeetProvider.DecodeToJson(recognizer, new float[16000]);
        await output.WriteLineAsync(JsonSerializer.Serialize(new { ready = true, provider, loadMs = watch.ElapsedMilliseconds }));
        await output.FlushAsync();
        while (await input.ReadLineAsync() is { } line)
        {
            string reply;
            try
            {
                using var request = JsonDocument.Parse(line);
                var path = request.RootElement.GetProperty("path").GetString() ?? throw new InvalidDataException("path");
                var samples = request.RootElement.GetProperty("samples").GetInt64();
                var pcm = await Pcm16Audio.ReadAsync(path, samples);
                reply = JsonSerializer.Serialize(new { json = SherpaParakeetProvider.DecodeToJson(recognizer, Pcm16Audio.ToFloatSamples(pcm)) });
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or KeyNotFoundException or
                ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            { reply = JsonSerializer.Serialize(new { error = error.GetType().Name }); }
            await output.WriteLineAsync(reply);
            await output.FlushAsync();
        }
        return 0;
    }
}
