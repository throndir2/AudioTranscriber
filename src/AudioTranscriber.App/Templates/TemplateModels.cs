using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AudioTranscriber.App.Templates;

public sealed record LlmPreset(string Name, string BaseUrl, string Model, bool NeedsKey, string Help)
{
    public static IReadOnlyList<LlmPreset> All { get; } =
    [
        new("OpenRouter", "https://openrouter.ai/api/v1", "google/gemini-2.5-flash", true,
            "OpenRouter: one key for hundreds of hosted models (openrouter.ai/keys). Pick a model that supports tools to let it read your files."),
        new("NVIDIA Build", "https://integrate.api.nvidia.com/v1", "meta/llama-3.3-70b-instruct", true,
            "NVIDIA Build (build.nvidia.com): free hosted models with an nvapi- key. Finite free quotas apply."),
        new("Ollama", "http://localhost:11434/v1", "llama3.1", false,
            "Ollama on this PC, or another machine: replace localhost with its address (start Ollama there with OLLAMA_HOST=0.0.0.0). Nothing leaves your network."),
        new("LM Studio", "http://localhost:1234/v1", "", false,
            "LM Studio's local server (Developer tab → Start server). Nothing leaves your network."),
        new("OpenAI", "https://api.openai.com/v1", "gpt-4.1-mini", true, "OpenAI API key from platform.openai.com."),
        new("Custom (OpenAI-compatible)", "http://localhost:8000/v1", "", false,
            "Any server with an OpenAI-compatible /v1/chat/completions endpoint (vLLM, llama.cpp server, LiteLLM, Groq, Together…).")
    ];
}

public sealed class LlmConnection : ObservableObject
{
    private string name = "", baseUrl = "", model = "";
    private string? protectedKey;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => name; set => Set(ref name, value ?? ""); }
    public string Kind { get; set; } = "";
    public string BaseUrl { get => baseUrl; set => Set(ref baseUrl, value ?? ""); }
    public string Model { get => model; set => Set(ref model, value ?? ""); }

    /// <summary>API key encrypted for the current OS user (DPAPI on Windows); never stored in plain text.</summary>
    public string? ProtectedKey
    {
        get => protectedKey;
        set { if (Set(ref protectedKey, value)) { Changed(nameof(HasKey)); Changed(nameof(KeyStatus)); } }
    }

    [JsonIgnore] public bool HasKey => !string.IsNullOrEmpty(ProtectedKey);
    [JsonIgnore] public string KeyStatus => HasKey ? $"An API key is saved for this connection ({AudioTranscriber.Providers.UserSecretProtection.Description})." : "No API key saved (not needed for Ollama / LM Studio).";

    public string? GetKey()
    {
        if (!HasKey) return null;
        try { return Encoding.UTF8.GetString(AudioTranscriber.Providers.UserSecretProtection.Unprotect(Convert.FromBase64String(ProtectedKey!))); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }

    public void SetKey(string? key) => ProtectedKey = string.IsNullOrWhiteSpace(key)
        ? null
        : Convert.ToBase64String(AudioTranscriber.Providers.UserSecretProtection.Protect(Encoding.UTF8.GetBytes(key.Trim())));

    public override string ToString() => Name;
}

public sealed class OutputTemplate : ObservableObject
{
    private string name = "", prompt = "", outputPath = "", output = "", status = "Not run yet.";
    private Guid? connectionId;
    private bool autoUpdate, writeToFile, includePrevious = true, useReferences = true, running;
    private int intervalSeconds = 60, maxTranscriptChars = 60000;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => name; set => Set(ref name, value ?? ""); }
    public string Prompt { get => prompt; set => Set(ref prompt, value ?? ""); }
    public Guid? ConnectionId { get => connectionId; set => Set(ref connectionId, value); }
    public bool AutoUpdate { get => autoUpdate; set { if (Set(ref autoUpdate, value)) Changed(nameof(Summary)); } }
    public int IntervalSeconds { get => intervalSeconds; set => Set(ref intervalSeconds, Math.Clamp(value, 10, 24 * 3600)); }
    public bool IncludePrevious { get => includePrevious; set => Set(ref includePrevious, value); }
    public bool UseReferences { get => useReferences; set => Set(ref useReferences, value); }
    public int MaxTranscriptChars { get => maxTranscriptChars; set => Set(ref maxTranscriptChars, Math.Clamp(value, 1000, 2_000_000)); }
    public bool WriteToFile { get => writeToFile; set => Set(ref writeToFile, value); }
    public string OutputPath { get => outputPath; set => Set(ref outputPath, value ?? ""); }
    public string Output { get => output; set => Set(ref output, value ?? ""); }
    public Guid? OutputSessionId { get; set; }

    [JsonIgnore] public string Status { get => status; set => Set(ref status, value ?? ""); }
    [JsonIgnore] public bool IsRunning { get => running; set { if (Set(ref running, value)) Changed(nameof(Summary)); } }
    [JsonIgnore] public DateTime LastRunUtc { get; set; } = DateTime.MinValue;
    [JsonIgnore] public string? LastFingerprint { get; set; }
    [JsonIgnore] public string Summary => IsRunning ? "updating…" : AutoUpdate ? "live" : "manual";

    public static IEnumerable<OutputTemplate> Starters() =>
    [
        new()
        {
            Name = "DM guidance for the current scene",
            Prompt = "I am the Dungeon Master / Game Master running this tabletop session. From the most recent part of the transcript, work out the current scene: where the party is, who they are dealing with, and what they are trying to do. Then give concise, practical guidance for running it right now: relevant rules and DCs, NPC motivations and likely reactions, secrets, clues or encounters from the adventure material that apply here, possible complications, and 2–3 suggested next beats. Search the reference files for the names and places mentioned and cite file and page. Focus on the current scene, not the whole session. Use short Markdown headings and bullets."
        },
        new()
        {
            Name = "Session summary",
            Prompt = "Write a running summary of this session so far: a 3–5 sentence overview, then the key events in order as bullets, decisions made, open threads and unresolved questions, and next steps or action items (with who owns them when mentioned). Be concise and factual; do not invent details."
        },
        new()
        {
            Name = "NPCs and other entities",
            Prompt = "Maintain a list of the people, NPCs, creatures, factions and places mentioned in this session. For each give: name (spelled as in the reference files when you can find it), type, a one-line description, their relationship to the players or speakers, and the latest thing that happened with them. Group by type using Markdown bullets. Keep entries from your previous output unless they were clearly wrong, and add new ones."
        },
        new()
        {
            Name = "Items and loot",
            Prompt = "Maintain an inventory of items, loot, money and other notable objects mentioned in this session: what it is, who has it now, where it came from, and its properties (look up item details in the reference files when available). Mark items that were used up, sold, given away or lost. Use Markdown bullets grouped by owner."
        }
    ];
}

public sealed class TemplateSettings
{
    public List<LlmConnection> Connections { get; set; } = [];
    public List<OutputTemplate> Templates { get; set; } = [];
    public string ContextFolder { get; set; } = "";
    public string PinnedFiles { get; set; } = "";
}
