using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace AudioTranscriber.Integrations;

public sealed record VttCue(long StartTicks, long EndTicks, string Text, string? SpeakerLabel);

public static partial class WebVttReader
{
    public static async IAsyncEnumerable<VttCue> ReadAsync(
        TextReader reader, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var lines = new BoundedLines(reader);
        var first = (await lines.ReadAsync(cancellationToken))?.TrimStart('\uFEFF');
        if (first is null || (first != "WEBVTT" && !first.StartsWith("WEBVTT ", StringComparison.Ordinal) &&
                             !first.StartsWith("WEBVTT\t", StringComparison.Ordinal)))
            throw new InvalidDataException("The transcript is not a WebVTT file.");

        string? line;
        var header = true;
        while ((line = await lines.ReadAsync(cancellationToken)) is not null)
        {
            if (header)
            {
                if (string.IsNullOrWhiteSpace(line)) header = false;
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("NOTE", StringComparison.Ordinal) || line is "STYLE" or "REGION")
            {
                while ((line = await lines.ReadAsync(cancellationToken)) is not null &&
                       !string.IsNullOrWhiteSpace(line)) { }
                continue;
            }
            if (!line.Contains("-->", StringComparison.Ordinal))
                line = await lines.ReadAsync(cancellationToken);
            var timing = line is null ? Match.Empty : Timing().Match(line);
            if (!timing.Success)
                throw new InvalidDataException("A WebVTT cue has invalid timing.");
            var start = ParseTime(timing.Groups["start"].Value);
            var end = ParseTime(timing.Groups["end"].Value);
            if (end < start) throw new InvalidDataException("A WebVTT cue ends before it starts.");

            var text = new StringBuilder();
            while ((line = await lines.ReadAsync(cancellationToken)) is not null &&
                   !string.IsNullOrWhiteSpace(line))
            {
                if (text.Length + line.Length > 65_536)
                    throw new InvalidDataException("A WebVTT cue exceeds the 64 KiB text limit.");
                if (text.Length > 0) text.Append('\n');
                text.Append(line);
            }
            var body = text.ToString();
            var voices = Voice().Matches(body);
            if (voices.Count == 0)
            {
                var plain = Plain(body);
                if (plain.Length > 0) yield return new(start, end, plain, null);
            }
            else
            {
                var cursor = 0;
                foreach (Match voice in voices)
                {
                    var unlabelled = Plain(body[cursor..voice.Index]);
                    if (unlabelled.Length > 0) yield return new(start, end, unlabelled, null);
                    var plain = Plain(voice.Groups["text"].Value);
                    if (plain.Length > 0)
                        yield return new(start, end, plain, WebUtility.HtmlDecode(voice.Groups["name"].Value.Trim()));
                    cursor = voice.Index + voice.Length;
                }
                var tail = Plain(body[cursor..]);
                if (tail.Length > 0) yield return new(start, end, tail, null);
            }
        }
    }

    private sealed class BoundedLines(TextReader reader)
    {
        private readonly char[] buffer = new char[4096];
        private int position;
        private int count;

        public async ValueTask<string?> ReadAsync(CancellationToken cancellationToken)
        {
            var text = new StringBuilder();
            while (true)
            {
                if (position == count)
                {
                    count = await reader.ReadAsync(buffer, cancellationToken);
                    position = 0;
                    if (count == 0) return text.Length == 0 ? null : text.ToString().TrimEnd('\r');
                }
                var character = buffer[position++];
                if (character == '\n') return text.ToString().TrimEnd('\r');
                if (text.Length >= 65_536) throw new InvalidDataException("A WebVTT line exceeds the 64 KiB limit.");
                text.Append(character);
            }
        }
    }

    private static string Plain(string value) => WebUtility.HtmlDecode(Tags().Replace(value, "")).Trim();

    private static long ParseTime(string value)
    {
        var parts = value.Split(':');
        var hours = parts.Length == 3 ? long.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        var minutes = int.Parse(parts[^2], CultureInfo.InvariantCulture);
        var seconds = decimal.Parse(parts[^1], CultureInfo.InvariantCulture);
        return checked(hours * TimeSpan.TicksPerHour + minutes * TimeSpan.TicksPerMinute +
                       (long)(seconds * TimeSpan.TicksPerSecond));
    }

    [GeneratedRegex(@"^(?<start>(?:\d+:)?[0-5]\d:[0-5]\d\.\d{3})\s+-->\s+(?<end>(?:\d+:)?[0-5]\d:[0-5]\d\.\d{3})(?:\s+.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Timing();

    [GeneratedRegex(@"<v(?:\.[^\s>]+)?\s+(?<name>[^>]+)>(?<text>.*?)(?:</v>|(?=<v(?:[.\s]))|$)", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Voice();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();
}
