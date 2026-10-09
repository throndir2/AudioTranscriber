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
        new("Ollama", "http://localhost:11434/v1", "gemma4:e4b", false,
            "Ollama on this PC, or another machine: replace localhost with its address (start Ollama there with OLLAMA_HOST=0.0.0.0). Default model gemma4:e4b reads images and can use tools; install it with: ollama pull gemma4:e4b. Nothing leaves your network."),
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
    private bool? supportsImages;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => name; set => Set(ref name, value ?? ""); }
    public string Kind { get; set; } = "";
    public string BaseUrl { get => baseUrl; set => Set(ref baseUrl, value ?? ""); }
    public string Model
    {
        get => model;
        set
        {
            if (!Set(ref model, value ?? "")) return;
            if (!string.Equals(model, ImagesCheckedFor, StringComparison.Ordinal)) SupportsImages = null;
            Changed(nameof(ImageSupportStatus));
        }
    }

    /// <summary>Whether <see cref="ImagesCheckedFor"/> accepts images; null when unknown or not checked.</summary>
    public bool? SupportsImages
    {
        get => supportsImages;
        set { if (Set(ref supportsImages, value)) Changed(nameof(ImageSupportStatus)); }
    }
    public string? ImagesCheckedFor { get; set; }

    [JsonIgnore]
    public string ImageSupportStatus =>
        !string.Equals(ImagesCheckedFor, Model, StringComparison.Ordinal) ? "Images: not checked yet (choose Check image support)."
        : SupportsImages switch
        {
            true => "Images: supported",
            false => "Images: not supported by this model",
            _ => "Images: unknown (the server doesn't say)"
        };

    public void SetImageSupport(string checkedModel, bool? supported)
    {
        ImagesCheckedFor = checkedModel;
        SupportsImages = supported;
        Changed(nameof(ImageSupportStatus));
    }

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
    private bool autoUpdate, writeToFile, includePrevious = true, useReferences = true, useTranscript = true, useScreenshot, running, keepVersions = true, includeTimestamps = true;
    private int intervalSeconds = 60, maxTranscriptChars = 60000, maxVersions;
    private List<Guid> inputTemplateIds = [];

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => name; set => Set(ref name, value ?? ""); }
    public string Prompt { get => prompt; set => Set(ref prompt, value ?? ""); }
    public Guid? ConnectionId { get => connectionId; set => Set(ref connectionId, value); }
    public bool AutoUpdate { get => autoUpdate; set { if (Set(ref autoUpdate, value)) Changed(nameof(Summary)); } }
    public int IntervalSeconds { get => intervalSeconds; set => Set(ref intervalSeconds, Math.Clamp(value, 10, 24 * 3600)); }
    public bool IncludePrevious { get => includePrevious; set => Set(ref includePrevious, value); }
    public bool UseReferences { get => useReferences; set => Set(ref useReferences, value); }
    public bool UseTranscript { get => useTranscript; set => Set(ref useTranscript, value); }
    /// <summary>Send each transcript line with its start and end time; off sends only "Speaker: text", so more of the session fits.</summary>
    public bool IncludeTimestamps { get => includeTimestamps; set => Set(ref includeTimestamps, value); }
    /// <summary>Other templates whose latest output is fed into this one; a change in any of them triggers an automatic update.</summary>
    public List<Guid> InputTemplateIds { get => inputTemplateIds; set { if (Set(ref inputTemplateIds, value ?? [])) Changed(nameof(Summary)); } }
    /// <summary>Attach a screenshot of the shared capture target (the user's virtual tabletop) to every run.</summary>
    public bool UseScreenshot { get => useScreenshot; set => Set(ref useScreenshot, value); }
    public int MaxTranscriptChars { get => maxTranscriptChars; set => Set(ref maxTranscriptChars, Math.Clamp(value, 1000, 2_000_000)); }
    public bool WriteToFile { get => writeToFile; set => Set(ref writeToFile, value); }
    public string OutputPath { get => outputPath; set => Set(ref outputPath, value ?? ""); }
    public string Output { get => output; set => Set(ref output, value ?? ""); }
    public Guid? OutputSessionId { get; set; }
    /// <summary>Save every changed output as a timestamped copy (see <see cref="TemplateVersions"/>).</summary>
    public bool KeepVersions { get => keepVersions; set => Set(ref keepVersions, value); }
    /// <summary>How many versioned copies to keep, newest first; 0 keeps all of them.</summary>
    public int MaxVersions { get => maxVersions; set => Set(ref maxVersions, Math.Clamp(value, 0, 100_000)); }

    [JsonIgnore] public string Status { get => status; set => Set(ref status, value ?? ""); }
    [JsonIgnore] public bool IsRunning { get => running; set { if (Set(ref running, value)) Changed(nameof(Summary)); } }
    [JsonIgnore] public DateTime LastRunUtc { get; set; } = DateTime.MinValue;
    [JsonIgnore] public string? LastFingerprint { get; set; }
    [JsonIgnore] public string Summary => (IsRunning ? "updating…" : AutoUpdate ? "live" : "manual") +
        (InputTemplateIds.Count > 0 ? $" · uses {InputTemplateIds.Count}" : "");

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
            IncludeTimestamps = false,
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

    /// <summary>
    /// Virtual tabletop (DM assistant) chain: narrow screenshot readers feed text-only combiners (movement, combat log, reminders).
    /// Each call does one small job so small vision models (e.g. gemma4:e4b) stay accurate. The last item is the reminders template.
    /// </summary>
    public static IReadOnlyList<OutputTemplate> TableStarters()
    {
        var turnOrder = new OutputTemplate
        {
            Name = "Table: turn order",
            UseScreenshot = true, UseTranscript = false, UseReferences = false, IncludePrevious = false, KeepVersions = false,
            AutoUpdate = true, IntervalSeconds = 20,
            Prompt = "Look ONLY at the turn order / initiative tracker in this virtual tabletop screenshot. Ignore everything else.\n" +
                     "If a turn order is visible: list every combatant from top to bottom, one per line, as \"Name - initiative number\". " +
                     "Mark whose turn it is (the highlighted entry, or the top entry if nothing is highlighted) with \"<- current turn\".\n" +
                     "Only list entries you can actually read. Do not guess hidden or cut-off entries.\n" +
                     "If no turn order is visible, reply exactly: No turn order visible.\n" +
                     "Reply with the list only, nothing else."
        };
        var tokens = new OutputTemplate
        {
            Name = "Table: token positions",
            UseScreenshot = true, UseTranscript = false, UseReferences = false, IncludePrevious = false, KeepVersions = false,
            AutoUpdate = true, IntervalSeconds = 20,
            Prompt = "Look ONLY at the battle map in this virtual tabletop screenshot. Ignore chat, menus and sidebars.\n" +
                     "For each visible token write one line: name or label (if shown, otherwise a short description), PC or monster/NPC, and approximate location " +
                     "(grid coordinate if the grid has labels, otherwise a map region like \"north-west, near the door\").\n" +
                     "Then add a line \"Close together:\" listing which tokens are adjacent or very close to each other.\n" +
                     "Do not guess tokens you cannot see.\n" +
                     "If no battle map is visible, reply exactly: No battle map visible.\n" +
                     "Reply with the list only, nothing else."
        };
        var movement = new OutputTemplate
        {
            Name = "Table: movement",
            UseScreenshot = false, UseTranscript = false, UseReferences = false, IncludePrevious = true, KeepVersions = false,
            AutoUpdate = true, IntervalSeconds = 20,
            InputTemplateIds = [tokens.Id],
            Prompt = "The input \"Table: token positions\" is the battle map as it looks right now. Your previous output, if any, ends with a " +
                     "\"Positions:\" list of where the tokens were last time.\n" +
                     "Compare the two and write \"Moved:\" followed by one line per change:\n" +
                     "- \"Name: old location -> new location\" for a token that moved,\n" +
                     "- \"Name: appeared at location\" for a new token,\n" +
                     "- \"Name: no longer visible\" for a token that is gone.\n" +
                     "Wording of locations can vary a little between readings; only count a token as moved when its location is clearly different. " +
                     "If nothing changed, write \"Moved: nothing\".\n" +
                     "Then write \"Positions:\" and copy the current token list from the input unchanged. Reply with these two parts only."
        };
        var health = new OutputTemplate
        {
            Name = "Table: health and conditions",
            UseScreenshot = true, UseTranscript = false, UseReferences = false, IncludePrevious = false, KeepVersions = false,
            AutoUpdate = true, IntervalSeconds = 30,
            Prompt = "Look ONLY at hit points and status in this virtual tabletop screenshot: health bars or numbers on tokens, status marker icons " +
                     "on tokens, and any visible party, character sheet or combat tracker HP. Ignore everything else.\n" +
                     "One line per creature: name or label, HP as shown (for example \"12/30\" or \"bar about half\"), then any conditions or status markers " +
                     "(name them if labelled, otherwise describe the icon briefly, e.g. \"red skull icon\"). Write \"DOWN\" for 0 HP or a dead/unconscious marker.\n" +
                     "Only list what you can actually see. If no health or status information is visible, reply exactly: No health information visible.\n" +
                     "Reply with the list only, nothing else."
        };
        var rolls = new OutputTemplate
        {
            Name = "Table: dice rolls",
            UseScreenshot = true, UseTranscript = false, UseReferences = false, IncludePrevious = false, KeepVersions = false,
            AutoUpdate = true, IntervalSeconds = 20,
            Prompt = "Look ONLY at the chat log / dice roll panel in this virtual tabletop screenshot. Ignore the map and everything else.\n" +
                     "List the most recent dice rolls you can read, oldest first, at most 8, one per line: who rolled, what for (attack, damage, save, " +
                     "check, initiative, spell, or the roll's title), and the total. Add \"natural 20\" or \"natural 1\" when shown.\n" +
                     "Only list rolls you can actually read. If no chat log or rolls are visible, reply exactly: No dice rolls visible.\n" +
                     "Reply with the list only, nothing else."
        };
        var scene = new OutputTemplate
        {
            Name = "Table: scene and map",
            UseScreenshot = true, UseTranscript = false, UseReferences = false, IncludePrevious = false, KeepVersions = false,
            AutoUpdate = true, IntervalSeconds = 120,
            Prompt = "Look ONLY at the map in this virtual tabletop screenshot (not the tokens, chat or menus) and describe the scene in at most 8 short bullets: " +
                     "what kind of place it is, lighting and unrevealed (fog of war) areas, doors and exits, notable terrain, cover, hazards and objects, " +
                     "and the map's rough size in grid squares if a grid is shown.\n" +
                     "Only describe what is actually visible. If no map is visible, reply exactly: No map visible.\n" +
                     "Reply with the bullets only, nothing else."
        };
        var combatLog = new OutputTemplate
        {
            Name = "Table: combat log",
            UseScreenshot = false, UseTranscript = true, UseReferences = false, IncludePrevious = true,
            MaxTranscriptChars = 8000, AutoUpdate = true, IntervalSeconds = 60,
            InputTemplateIds = [turnOrder.Id, rolls.Id, health.Id],
            Prompt = "Keep a running log of the current fight from the turn order, dice rolls and health read from the virtual tabletop and the recent table talk.\n" +
                     "Use a \"## Round N\" heading per round (a new round starts when the turn order wraps back to the top) and one short bullet per action: " +
                     "who acted, what they did, hits, misses and damage, who went down, and conditions applied or ended.\n" +
                     "Keep your previous log as it is and only append new events; never repeat an event. " +
                     "When the turn order shows no combat any more, add \"Combat ended.\"; when a new turn order appears later, start a new \"# Encounter\" section.\n" +
                     "Only use facts from the inputs. Reply with the log only."
        };
        var reminders = new OutputTemplate
        {
            Name = "Table: DM reminders",
            UseScreenshot = false, UseTranscript = true, UseReferences = true, IncludePrevious = false,
            MaxTranscriptChars = 12000, AutoUpdate = true, IntervalSeconds = 45,
            InputTemplateIds = [turnOrder.Id, movement.Id, health.Id, scene.Id],
            Prompt = "I am the GM running this game right now. You get: the turn order, token positions and movement, health and conditions, " +
                     "and the scene read from my virtual tabletop, the recent transcript of the table talk, and my notes / adventure (reference files).\n" +
                     "Give short real-time reminders as Markdown bullets, most urgent first, at most 8 bullets:\n" +
                     "- Whose turn it is now and who is next.\n" +
                     "- For monsters acting soon: one tactical suggestion based on the token positions, their health and the terrain.\n" +
                     "- Creatures that are low on HP, down, or whose conditions need tracking.\n" +
                     "- Rules reminders that apply right now (conditions, opportunity attacks, concentration, etc.).\n" +
                     "- Story beats, clues or NPC moments from my notes for the current scene that have NOT been presented yet in the transcript, " +
                     "phrased like \"Don't forget to present …\".\n" +
                     "Each bullet one short line. Only use facts from the inputs; skip a point if there is nothing for it. No introduction or closing text."
        };
        return [turnOrder, tokens, movement, health, rolls, scene, combatLog, reminders];
    }

    /// <summary>Ready-made tabletop RPG templates the user can add one at a time (or all at once). All start as manual updates.</summary>
    public static IReadOnlyList<TemplateBlueprint> TtrpgLibrary { get; } =
    [
        new("Recap: \"Previously on…\"", "A short read-aloud recap to open the next session.", () => new()
        {
            Name = "Recap: previously on…",
            Prompt = "Write a read-aloud recap of this session for the Game Master to open the next session with. Start with \"Previously on…\" and write 1–3 short, vivid paragraphs in second person plural (\"you\"), past tense, covering only what the player characters did and learned, ending on the cliffhanger or the situation where play stopped. Do not reveal GM secrets from the reference files. After the recap add a heading \"Key reminders\" with up to 5 bullets the players will want to remember (names, promises, deadlines)."
        }),
        new("Quest log and plot hooks", "Active quests, objectives, rewards, and hooks the party noticed.", () => new()
        {
            Name = "Quest log and plot hooks",
            Prompt = "Maintain the party's quest log. Use the headings Active quests, Completed or failed, and Unfollowed plot hooks. For each quest give: name, who gave it, the goal, current progress and next step, any reward promised, and any deadline. Under Unfollowed plot hooks list rumours, leads and offers the players heard but have not acted on yet. Use the reference files to name quests consistently with the adventure. Keep entries from your previous output, update their status, and add new ones. Do not invent quests that were not mentioned."
        }),
        new("Combat tracker", "Round, initiative, damage, conditions and resources in the current fight.", () => new()
        {
            Name = "Combat tracker",
            MaxTranscriptChars = 20000,
            Prompt = "Track the current (or most recent) combat from the transcript. Show: the round number, the initiative order with whose turn it is, then one line per combatant with damage taken or HP remaining when stated, conditions and their durations (e.g. prone, poisoned until end of next turn), concentration, and notable resources used (spell slots, rage, ki, legendary actions, potions). List defeated or fled combatants separately. Mark anything you are unsure about with \"(?)\". If no combat is happening, reply with a one-line note and the outcome of the last fight. Keep it compact; no narration."
        }),
        new("Rules questions and rulings", "Rules questions raised at the table, the call made, and the rule as written.", () => new()
        {
            Name = "Rules questions and rulings",
            Prompt = "Keep a log of rules questions and rulings from this session. For each: the situation in one line, the ruling the GM made at the table, and what the rules say (search the reference files and cite file and page when you find it). Flag rulings that differ from the rules as written with \"Differs from RAW\" so the GM can decide whether to keep them as house rules. Keep previous entries and add new ones. Skip questions that were never resolved unless they are still open; list those under \"Open questions\"."
        }),
        new("Lore and canon log", "World facts established in play, so later sessions stay consistent.", () => new()
        {
            Name = "Lore and canon log",
            Prompt = "Maintain a canon log of facts about the world that were established during play, especially details the GM improvised: history, names, gods, customs, prices, distances, relationships, secrets revealed to the players. Group by topic with short bullets and note who said it when relevant. Compare against the reference files and add a \"Possible contradictions\" section when something said at the table conflicts with the adventure or setting material (cite file and page). Keep previous entries; do not invent facts."
        }),
        new("Locations and travel", "Places visited or mentioned, routes, and where the party is now.", () => new()
        {
            Name = "Locations and travel",
            Prompt = "Maintain a gazetteer of locations from this session. Start with \"Party is now at:\" and the current location. Then for each place visited or mentioned: name (spelled as in the reference files when found), region, a one-line description, notable features, who or what is there, and whether the party has visited it. End with a \"Routes\" section listing known paths, travel times and hazards between places. Keep previous entries and update them."
        }),
        new("In-game calendar and timeline", "Days passed, time of day, rests, deadlines and countdowns.", () => new()
        {
            Name = "In-game calendar and timeline",
            Prompt = "Track in-world time for this campaign. Give: the current in-game date or day number and time of day, then a timeline of events with in-game times (travel, rests, downtime, scenes), and a \"Deadlines and countdowns\" section with anything time-sensitive (rituals, festivals, ultimatums, spell or effect durations, poison or disease progress) and how much time is left. State assumptions when the transcript is vague (e.g. \"assumed one day of travel\"). Keep previous entries and extend the timeline."
        }),
        new("Mysteries and clues", "Clues found, what they point to, and leads the players have missed.", () => new()
        {
            Name = "Mysteries and clues",
            Prompt = "Track the mysteries in this campaign. For each open mystery or question: what the players are trying to find out, the clues they have found so far (and where), what those clues actually point to according to the reference files (GM eyes only), and important clues they have not found or not connected yet. Suggest one or two new ways to deliver a missing clue if the players seem stuck. Mark solved mysteries as solved. Keep previous entries and update them."
        }),
        new("Spotlight and player engagement", "Who got the spotlight, character moments, and ideas to involve quieter players.", () => new()
        {
            Name = "Spotlight and player engagement",
            Prompt = "For each player character (and the player's name when known), summarise: their key moments this session, personal goals or backstory threads that came up, and roughly how much spotlight they had (high, medium, low). Then suggest 1–2 concrete ways to give the lower-spotlight characters a moment next session, tied to their goals or abilities and to the adventure in the reference files. Be kind and practical; this is for the GM only."
        }),
        new("Memorable quotes and moments", "Funny or epic lines with who said them, plus highlight moments.", () => new()
        {
            Name = "Memorable quotes and moments",
            Prompt = "Collect the memorable moments of this session: great or funny quotes (quote them exactly as transcribed, with the speaker), dramatic dice rolls, clever plans, epic fails and emotional beats. Use two headings, Quotes and Highlights, with short bullets in the order they happened. Keep previous entries and add new ones. Do not invent quotes."
        }),
        new("In-character journal", "A journal entry written by a party member, in prose.", () => new()
        {
            Name = "In-character journal",
            UseReferences = false,
            Prompt = "Write this session as an in-character journal entry by a member of the party (the most talkative player character, unless one is named as the chronicler). Write in first person, past tense, in that character's voice, 300–600 words. Include only what the characters experienced and know; no game mechanics, dice or out-of-character table talk. Update and extend your previous entry instead of starting over."
        }),
        new("Player handout (spoiler-free)", "A player-facing summary that is safe to share with the group.", () => new()
        {
            Name = "Player handout (spoiler-free)",
            UseReferences = false,
            Prompt = "Write a player-facing campaign wiki entry for this session that the GM can share with the players. Include: a short summary, NPCs met (one line each, only what the characters know), places visited, loot gained, and open leads. Leave out anything only the GM knows, any out-of-character table talk and anything the characters did not witness. Use Markdown headings and bullets."
        }),
        new("Next session prep", "Likely next scenes, NPCs and stat blocks to prepare, and consequences.", () => new()
        {
            Name = "Next session prep",
            Prompt = "Help the GM prepare the next session based on where this one ended. Give: a strong start for next session, the 3–5 scenes most likely to come up next, NPCs and monsters to have ready (with the file and page of their stat blocks or descriptions from the reference files), secrets and clues the players could discover, how the world reacts to what the players did (consequences, factions moving), and loose ends to follow up. Use short headings and bullets."
        }),
        new("XP, milestones and rewards", "Encounters overcome, objectives reached, XP and treasure to hand out.", () => new()
        {
            Name = "XP, milestones and rewards",
            Prompt = "Track advancement and rewards for this session: encounters overcome (with the monsters involved), objectives and milestones reached, XP awarded when mentioned (or a suggested amount from the reference files' rules, marked as a suggestion), treasure and how it was split, and other rewards such as favours, titles or boons. End with whether the party looks ready for a level-up under milestone advancement. Keep previous entries and add new ones."
        }),
        new("Character changes and conditions", "Level-ups, new abilities, lasting conditions, curses and attunement.", () => new()
        {
            Name = "Character changes and conditions",
            Prompt = "For each player character, track lasting changes from this session: level-ups, new spells, feats or abilities, ability score or HP changes, lasting conditions (exhaustion, curses, diseases, madness, injuries), attuned or equipped magic items, and resources still spent at the end of the session if no rest happened. Look up rules for conditions and effects in the reference files and note how they end. Keep previous entries and update them."
        }),
        new("Improv helper: NPCs on the fly", "Ready-to-use NPCs and names that fit the current scene.", () => new()
        {
            Name = "Improv helper: NPCs on the fly",
            IncludePrevious = false, MaxTranscriptChars = 8000,
            Prompt = "From the most recent part of the transcript, work out the current scene and setting. Give the GM three ready-to-use NPCs who could plausibly appear here, each with: a name that fits the setting, a one-line look, a voice or mannerism, what they want, and one secret or useful piece of information. Then list 8 spare names (mixed people, taverns and shops) that fit the setting. If the reference files describe this location, prefer NPCs from there and cite file and page. Keep it short."
        })
    ];
}

/// <summary>An entry of the TTRPG template library: <see cref="Create"/> builds a fresh template (new ID) each time.</summary>
public sealed record TemplateBlueprint(string Name, string Description, Func<OutputTemplate> Create)
{
    public override string ToString() => Name;
}

/// <summary>
/// Versioned copies of a template's output: every changed update is saved as a timestamped file, either next to the
/// template's output file (in "&lt;name&gt; versions") or in the library's template-versions folder.
/// </summary>
public static partial class TemplateVersions
{
    private const string Stamp = "yyyy-MM-dd HH-mm-ss";

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}$")]
    private static partial System.Text.RegularExpressions.Regex StampName();

    public static string Folder(OutputTemplate template, string libraryRoot)
    {
        if (template.WriteToFile && template.OutputPath.Trim().Trim('"') is { Length: > 0 } path)
        {
            var full = Path.GetFullPath(path);
            return Path.Combine(Path.GetDirectoryName(full) ?? libraryRoot, Path.GetFileNameWithoutExtension(full) + " versions");
        }
        // The folder ends with the template's short ID, so renaming the template keeps using its existing folder.
        var root = Path.Combine(libraryRoot, "template-versions");
        var suffix = $" ({template.Id.ToString("N")[..8]})";
        if (Directory.Exists(root) && Directory.EnumerateDirectories(root, "*" + suffix).FirstOrDefault() is { } existing) return existing;
        return Path.Combine(root, SafeName(template.Name) + suffix);
    }

    private static string Extension(OutputTemplate template) =>
        template.WriteToFile && Path.GetExtension(template.OutputPath.Trim().Trim('"')) is { Length: > 1 } ext ? ext : ".md";

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (safe.Length > 60) safe = safe[..60].Trim();
        return safe.Length == 0 ? "Template" : safe;
    }

    /// <summary>Versioned copies in <paramref name="folder"/>, oldest first.</summary>
    public static IReadOnlyList<string> List(string folder, string extension) => !Directory.Exists(folder) ? []
        : Directory.EnumerateFiles(folder, "*" + extension)
            .Where(f => Path.GetExtension(f).Equals(extension, StringComparison.OrdinalIgnoreCase) && StampName().IsMatch(Path.GetFileNameWithoutExtension(f)))
            .Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Saves <paramref name="output"/> as a new version unless it matches the newest one, then deletes the oldest versions
    /// beyond <see cref="OutputTemplate.MaxVersions"/>. Returns the new file, or null when nothing changed.
    /// </summary>
    public static string? Save(OutputTemplate template, string libraryRoot, string output, DateTime now)
    {
        var folder = Folder(template, libraryRoot);
        var extension = Extension(template);
        Directory.CreateDirectory(folder);
        var existing = List(folder, extension);
        string? written = null;
        if (existing.Count == 0 || File.ReadAllText(existing[^1]) != output)
        {
            if (existing.Count > 0 && DateTime.TryParseExact(Path.GetFileNameWithoutExtension(existing[^1]), Stamp,
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var newest) && newest >= now)
                now = newest;
            string path;
            while (File.Exists(path = Path.Combine(folder, now.ToString(Stamp, System.Globalization.CultureInfo.InvariantCulture) + extension))) now = now.AddSeconds(1);
            File.WriteAllText(path, output);
            written = path;
            existing = [.. existing, path];
        }
        if (template.MaxVersions > 0)
            foreach (var old in existing.Take(Math.Max(0, existing.Count - template.MaxVersions))) File.Delete(old);
        return written;
    }
}

/// <summary>One row of the "outputs of other templates" checklist for the selected template.</summary>
public sealed class TemplateInputOption(OutputTemplate template, bool selected, bool enabled, string hint, Action<TemplateInputOption> toggled) : ObservableObject
{
    private bool isSelected = selected, isEnabled = enabled;
    private string hint = hint;

    public OutputTemplate Template { get; } = template;
    public bool IsSelected { get => isSelected; set { if (Set(ref isSelected, value)) toggled(this); } }
    public bool IsEnabled { get => isEnabled; set => Set(ref isEnabled, value); }
    public string Hint { get => hint; set => Set(ref hint, value); }
}

/// <summary>Chaining helpers: which templates feed which, cycle checks, run order and change detection.</summary>
public static class TemplateGraph
{
    public static IReadOnlyList<OutputTemplate> Inputs(OutputTemplate template, IEnumerable<OutputTemplate> all) =>
        template.InputTemplateIds.Distinct().Select(id => all.FirstOrDefault(t => t.Id == id)).OfType<OutputTemplate>()
            .Where(t => t != template).ToArray();

    /// <summary>True when <paramref name="from"/> uses <paramref name="target"/> as an input, directly or through other templates.</summary>
    public static bool DependsOn(OutputTemplate from, OutputTemplate target, IReadOnlyCollection<OutputTemplate> all)
    {
        var seen = new HashSet<Guid>();
        var stack = new Stack<OutputTemplate>([from]);
        while (stack.TryPop(out var current))
        {
            if (!seen.Add(current.Id)) continue;
            foreach (var input in Inputs(current, all))
            {
                if (input == target) return true;
                stack.Push(input);
            }
        }
        return false;
    }

    /// <summary>Templates ordered so each comes after its inputs (upstream first).</summary>
    public static IReadOnlyList<OutputTemplate> RunOrder(IReadOnlyCollection<OutputTemplate> all)
    {
        var order = new List<OutputTemplate>();
        var seen = new HashSet<Guid>();
        void Visit(OutputTemplate t)
        {
            if (!seen.Add(t.Id)) return;
            foreach (var input in Inputs(t, all)) Visit(input);
            order.Add(t);
        }
        foreach (var t in all) Visit(t);
        return order;
    }

    /// <summary>
    /// Hash of everything that feeds a template: session + transcript (when used) and each input template's output.
    /// <paramref name="extra"/> is the extension point for further inputs (e.g. a screenshot hash).
    /// </summary>
    public static string Fingerprint(OutputTemplate template, Guid? session, string? transcript, IEnumerable<(Guid Id, string Output)> inputs, string? extra = null)
    {
        var text = new StringBuilder();
        if (template.UseTranscript) text.Append("T:").Append(session?.ToString("N")).Append('\n').Append(transcript).Append('\0');
        foreach (var (id, output) in inputs) text.Append("I:").Append(id.ToString("N")).Append('\n').Append(output).Append('\0');
        if (!string.IsNullOrEmpty(extra)) text.Append("X:").Append(extra);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}

public sealed class TemplateSettings
{
    public List<LlmConnection> Connections { get; set; } = [];
    public List<OutputTemplate> Templates { get; set; } = [];
    public string ContextFolder { get; set; } = "";
    public string PinnedFiles { get; set; } = "";
    /// <summary>"Screen N (…)" or a window title (matched exactly, then by "contains").</summary>
    public string CaptureTarget { get; set; } = "";
    public int CaptureMaxWidth { get; set; } = ScreenCapture.DefaultMaxWidth;
}
