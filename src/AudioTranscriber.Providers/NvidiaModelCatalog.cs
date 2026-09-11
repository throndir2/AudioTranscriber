namespace AudioTranscriber.Providers;

public sealed record NvidiaModel(
    string Id, string DisplayName, string AdvertisedModel, string FunctionId,
    string EnglishLocale, bool VerifiedWordTiming, Uri ApiSource)
{
    public TimeSpan MaximumAudioDuration => TimeSpan.FromSeconds(30);
    public bool NativeDiarization => false;
    public DateOnly MetadataCheckedOn => new(2026, 9, 11);

    public string GetLocale(string sourceLanguage)
    {
        // Only the first-use English locale is verified for these hosted routes.
        if (sourceLanguage.Equals("en", StringComparison.OrdinalIgnoreCase) ||
            sourceLanguage.Equals(EnglishLocale, StringComparison.OrdinalIgnoreCase))
            return EnglishLocale;
        throw new ProviderException(ProviderFailureKind.InvalidRequest, "unsupported-language");
    }
}

public static class NvidiaModelCatalog
{
    public static NvidiaModel Parakeet { get; } = new(
        "nvidia-parakeet-tdt-v3", "Parakeet TDT v3", "nvidia/parakeet-tdt-0_6b",
        "2b940e91-a70e-4483-a958-d50408b96589", "en-GB", true,
        new("https://build.nvidia.com/nvidia/parakeet-tdt-0_6b/api"));
    public static NvidiaModel Canary { get; } = new(
        "nvidia-canary", "Canary-1B-Flash2.0", "nvidia/canary-1b-asr",
        "b0e8b4a5-217c-40b7-9b96-17d84e666317", "en-US", false,
        new("https://build.nvidia.com/nvidia/canary-1b-asr/api"));
    public static NvidiaModel Whisper { get; } = new(
        "nvidia-whisper-large-v3", "Hosted Whisper large-v3", "openai/whisper-large-v3",
        "b702f636-f60c-4a3d-a6f4-f3568c13bd7d", "en", false,
        new("https://build.nvidia.com/openai/whisper-large-v3/api"));
    public static IReadOnlyList<NvidiaModel> All { get; } =
        Array.AsReadOnly(new[] { Parakeet, Canary, Whisper });

    public static NvidiaModel Get(string id) => All.SingleOrDefault(m => m.Id == id)
        ?? throw new ProviderException(ProviderFailureKind.InvalidRequest, "unknown-model");

    internal static void RequireCatalogEntry(NvidiaModel model)
    {
        if (!All.Contains(model))
            throw new ProviderException(ProviderFailureKind.InvalidRequest, "unapproved-route");
    }
}
