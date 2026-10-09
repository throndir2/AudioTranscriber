namespace AudioTranscriber.App.Templates;

/// <summary>
/// Token arithmetic for filling a model's context window. Counts are estimates (no tokenizer): about three ASCII
/// characters per token and one token per other character (accents, CJK), which errs on the safe side.
/// </summary>
public static class ContextBudget
{
    public const int OllamaMinimum = 8192;
    public const int ImageTokens = 1500;
    private const double AsciiPerToken = 3.0;

    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var ascii = 0;
        foreach (var c in text) if (c < 128) ascii++;
        return (int)Math.Ceiling(ascii / AsciiPerToken) + (text.Length - ascii);
    }

    /// <summary>Room left for the answer (including any thinking).</summary>
    public static int OutputReserve(int contextTokens) => Math.Clamp(contextTokens / 4, 1024, 16384);

    /// <summary>Room left for reference files the model reads with tools.</summary>
    public static int ToolReserve(int contextTokens) => Math.Min(contextTokens / 4, 32768);

    /// <summary>How many characters from the end of <paramref name="text"/> fit in <paramref name="tokens"/>.</summary>
    public static int TailChars(string text, int tokens)
    {
        if (tokens <= 0) return 0;
        double used = 0;
        var start = text.Length;
        while (start > 0)
        {
            var cost = text[start - 1] < 128 ? 1 / AsciiPerToken : 1;
            if (used + cost > tokens) break;
            used += cost;
            start--;
        }
        return text.Length - start;
    }

    /// <summary>
    /// The num_ctx to ask Ollama for: a power of two that holds <paramref name="needed"/> tokens, never smaller than
    /// <paramref name="current"/> (changing it reloads the model, so it only grows), and at most <paramref name="cap"/> when known.
    /// </summary>
    public static int OllamaContext(int current, int needed, int cap)
    {
        long size = Math.Max(current, OllamaMinimum);
        while (size < needed && size < int.MaxValue / 2) size *= 2;
        return cap > 0 ? (int)Math.Min(size, cap) : (int)size;
    }
}
