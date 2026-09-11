using System.Globalization;
using System.Text;

namespace AudioTranscriber.Benchmarks;

public sealed record EditCounts(int Substitutions, int Deletions, int Insertions, int ReferenceUnits)
{
    public int Errors => Substitutions + Deletions + Insertions;
    public double? Rate => ReferenceUnits == 0 ? null : (double)Errors / ReferenceUnits;
}

public sealed record TranscriptMetrics(string NormalizedReference, string NormalizedHypothesis,
    EditCounts Words, EditCounts Characters);

public static class ErrorMetrics
{
    public const string Normalization =
        "Unicode NFKC; invariant lowercase; retain Unicode letters/digits; all other runes become spaces; " +
        "collapse whitespace and trim. WER splits on spaces. CER removes spaces and compares Unicode scalar values. " +
        "No number expansion, spelling correction, stemming, reference hints, or entity annotations. " +
        "Levenshtein unit costs; ties prefer substitution, deletion, insertion; rates may exceed 1.";

    public static string Normalize(string text)
    {
        var output = new StringBuilder();
        var pendingSpace = false;
        foreach (var rune in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                if (pendingSpace && output.Length > 0) output.Append(' ');
                output.Append(rune.ToString());
                pendingSpace = false;
            }
            else pendingSpace = true;
        }
        return output.ToString();
    }

    public static TranscriptMetrics Compare(string reference, string hypothesis)
    {
        var r = Normalize(reference);
        var h = Normalize(hypothesis);
        return new(r, h,
            Distance(r.Split(' ', StringSplitOptions.RemoveEmptyEntries), h.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            Distance(r.EnumerateRunes().Where(x => x.Value != ' ').ToArray(),
                h.EnumerateRunes().Where(x => x.Value != ' ').ToArray()));
    }

    public static EditCounts Distance<T>(IReadOnlyList<T> reference, IReadOnlyList<T> hypothesis)
    {
        var previous = new EditCounts[hypothesis.Count + 1];
        for (var j = 0; j <= hypothesis.Count; j++) previous[j] = new(0, 0, j, reference.Count);
        for (var i = 1; i <= reference.Count; i++)
        {
            var current = new EditCounts[hypothesis.Count + 1];
            current[0] = new(0, i, 0, reference.Count);
            for (var j = 1; j <= hypothesis.Count; j++)
            {
                var diagonal = previous[j - 1];
                if (EqualityComparer<T>.Default.Equals(reference[i - 1], hypothesis[j - 1]))
                    current[j] = diagonal;
                else
                {
                    var substitute = diagonal with { Substitutions = diagonal.Substitutions + 1 };
                    var delete = previous[j] with { Deletions = previous[j].Deletions + 1 };
                    var insert = current[j - 1] with { Insertions = current[j - 1].Insertions + 1 };
                    current[j] = substitute.Errors <= delete.Errors && substitute.Errors <= insert.Errors
                        ? substitute : delete.Errors <= insert.Errors ? delete : insert;
                }
            }
            previous = current;
        }
        return previous[^1];
    }
}
