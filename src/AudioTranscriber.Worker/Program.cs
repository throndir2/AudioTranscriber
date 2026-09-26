using AudioTranscriber.Diarization;

namespace AudioTranscriber.Worker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--install-models", var directory])
            {
                await DiarizationModels.InstallAsync(directory);
                Console.WriteLine("Verified local models installed (artifact download budget: 33,488,994 bytes; see bundled model notices).");
                return 0;
            }
            if (args is not ["--diarize", var requestPath, var resultPath])
            {
                Console.Error.WriteLine("Usage: AudioTranscriber.Worker --diarize <local-request.json> <new-local-result.json> OR --install-models <local-model-directory>");
                return 2;
            }
            var request = await DiarizationWorkerProtocol.ReadAsync<DiarizationWorkerRequest>(requestPath);
            if (request.Version != DiarizationWorkerProtocol.Version)
                throw new InvalidDataException("Unsupported worker protocol.");
            await using var engine = new SherpaDiarizationService(request.Models, request.Matching);
            var result = request.Enrollment is { } enrollment
                ? await engine.EnrollAsync(request.Audio, request.Registry, enrollment)
                : await engine.DiarizeAsync(request.Audio, request.Registry);
            DiarizationWorkerProtocol.ValidateResult(result, request.Audio, request.Registry.Revision);
            await DiarizationWorkerProtocol.WriteAsync(resultPath, new DiarizationWorkerResponse(1, result));
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Do not echo request contents, private paths, or native/provider error bodies.
            Console.Error.WriteLine(ex switch
            {
                FileNotFoundException => "A required local audio/model file is missing.",
                InvalidDataException => "Local audio, model checksum, registry, or worker payload validation failed.",
                DllNotFoundException or BadImageFormatException => "The Windows x64 native diarization runtime could not be loaded.",
                OperationCanceledException => "Local diarization was canceled.",
                _ => "Local diarization failed. Verify the model installation and bounded PCM16 request."
            });
            return 1;
        }
    }
}
