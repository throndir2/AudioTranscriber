using System.Diagnostics;
using AudioTranscriber.Core;

namespace AudioTranscriber.Diarization;

/// <summary>Production boundary: cancellation terminates only this service's owned native worker tree.</summary>
public sealed class WorkerDiarizationService : IDiarizationService, ISpeakerEnrollmentService, ISpeakerEmbeddingService
{
    private readonly string executable;
    private readonly string? workerAssembly;
    private readonly string workDirectory;
    private readonly DiarizationModelPaths models;
    private readonly SpeakerMatchingOptions matching;
    private readonly TimeSpan timeout;
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    public event Action<int>? WorkerStarted;

    public WorkerDiarizationService(string workerExecutable, string workDirectory, DiarizationModelPaths models,
        SpeakerMatchingOptions? matching = null, TimeSpan? timeout = null, string? dotnetHostPath = null)
    {
        var worker = LocalPaths.RequireFile(workerExecutable);
        if (Path.GetExtension(worker).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            workerAssembly = worker;
            executable = LocalPaths.RequireFile(dotnetHostPath ??
                throw new ArgumentException("A worker DLL requires an explicit local dotnet host path."));
        }
        else
        {
            executable = worker;
            if (dotnetHostPath is not null)
                throw new ArgumentException("Specify a worker DLL when using an explicit dotnet host.");
        }
        if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Diarization requires a local Windows executable host.");
        this.workDirectory = LocalPaths.RequireDirectoryPath(workDirectory);
        this.models = new(Path.GetFullPath(models.SegmentationModelPath), Path.GetFullPath(models.EmbeddingModelPath));
        this.matching = matching ?? new();
        this.matching.Validate();
        this.timeout = timeout ?? TimeSpan.FromMinutes(5);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public Task<DiarizationResult> DiarizeAsync(DiarizationRequest request, SpeakerRegistrySnapshot registry,
        CancellationToken cancellationToken = default) => RunAsync(request, registry, null, cancellationToken);

    public Task<DiarizationResult> EnrollAsync(DiarizationRequest request, SpeakerRegistrySnapshot registry,
        SpeakerEnrollment enrollment, CancellationToken cancellationToken = default)
    {
        SherpaDiarizationService.ValidateEnrollment(enrollment);
        return RunAsync(request, registry, enrollment, cancellationToken);
    }

    public async Task<IReadOnlyList<float[]?>> EmbedAsync(string audioPath, long sampleCount, IReadOnlyList<SpeakerEmbeddingClip> clips,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        SherpaDiarizationService.ValidateEmbedding(audioPath, sampleCount, clips);
        var response = await RunWorkerAsync<SpeakerEmbeddingWorkerRequest, SpeakerEmbeddingWorkerResponse>("--embed",
            new(1, Path.GetFullPath(audioPath), sampleCount, clips.ToArray(), models), cancellationToken);
        if (response.Version != DiarizationWorkerProtocol.Version) throw new InvalidDataException("Unsupported diarization worker protocol.");
        SherpaDiarizationService.ValidateEmbeddings(response.Embeddings, clips.Count);
        return response.Embeddings;
    }

    private async Task<DiarizationResult> RunAsync(DiarizationRequest request, SpeakerRegistrySnapshot registry,
        SpeakerEnrollment? enrollment, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        SherpaDiarizationService.ValidateRequest(request, registry);
        CoreRegistrySerializer.ToState(registry);
        var response = await RunWorkerAsync<DiarizationWorkerRequest, DiarizationWorkerResponse>("--diarize",
            new(1, request with { AudioPath = Path.GetFullPath(request.AudioPath) }, models, registry, matching, enrollment), cancellationToken);
        if (response.Version != DiarizationWorkerProtocol.Version)
            throw new InvalidDataException("Unsupported diarization worker protocol.");
        DiarizationWorkerProtocol.ValidateResult(response.Result, request, registry.Revision);
        return response.Result;
    }

    private async Task<TResponse> RunWorkerAsync<TRequest, TResponse>(string mode, TRequest workerRequest, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token, deadline.Token);
        await gate.WaitAsync(linked.Token);
        var jobDirectory = Path.Combine(workDirectory, "diarization-" + Guid.NewGuid().ToString("N"));
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Directory.CreateDirectory(jobDirectory);
            var requestPath = Path.Combine(jobDirectory, "request.json");
            var resultPath = Path.Combine(jobDirectory, "result.json");
            await DiarizationWorkerProtocol.WriteAsync(requestPath, workerRequest, linked.Token);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = Path.GetDirectoryName(executable)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true
                }
            };
            var inherited = process.StartInfo.Environment;
            var runtimeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SystemRoot", "WINDIR", "PATH", "ProgramFiles", "ProgramFiles(x86)",
                "DOTNET_ROOT", "DOTNET_ROOT_X64"
            };
            foreach (var name in inherited.Keys.Where(name => !runtimeKeys.Contains(name)).ToArray())
                inherited.Remove(name);
            if (workerAssembly is not null) process.StartInfo.ArgumentList.Add(workerAssembly);
            process.StartInfo.ArgumentList.Add(mode);
            process.StartInfo.ArgumentList.Add(requestPath);
            process.StartInfo.ArgumentList.Add(resultPath);
            linked.Token.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("Local diarization worker did not start.");
            // Speaker analysis is off the transcript's critical path; never let it slow recognition.
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.StandardInput.Close();
            using var registration = linked.Token.Register(() => KillOwnedProcess(process));
            try
            {
                WorkerStarted?.Invoke(process.Id);
                var stdout = DrainBoundedAsync(process.StandardOutput, process, linked.Token);
                var stderr = DrainBoundedAsync(process.StandardError, process, linked.Token);
                await Task.WhenAll(process.WaitForExitAsync(linked.Token), stdout, stderr);
                linked.Token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"Local diarization worker failed (exit {process.ExitCode}). Check installed models/runtime; no audio was uploaded.");
                return await DiarizationWorkerProtocol.ReadAsync<TResponse>(resultPath, linked.Token);
            }
            finally
            {
                KillOwnedProcess(process);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !shutdown.IsCancellationRequested)
        {
            throw new TimeoutException("The bounded local diarization worker exceeded its time limit.");
        }
        finally
        {
            if (Directory.Exists(jobDirectory)) Directory.Delete(jobDirectory, true);
            gate.Release();
        }
    }

    private static async Task DrainBoundedAsync(StreamReader reader, Process process, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var count = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if ((count += read) <= DiarizationWorkerProtocol.MaximumOutputCharacters) continue;
            KillOwnedProcess(process);
            throw new InvalidDataException("Local worker output exceeded its limit.");
        }
    }

    private static void KillOwnedProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await shutdown.CancelAsync();
        await gate.WaitAsync();
        gate.Release();
    }
}
