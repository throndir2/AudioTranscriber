using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AudioTranscriber.Storage;

public static class TranscriptExporter
{
    public static async Task ExportAsync(LibraryStore store, Guid sessionId, string destination,
        CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(destination).ToLowerInvariant();
        if (extension is not (".txt" or ".json" or ".srt" or ".vtt"))
            throw new ArgumentException("Choose a .txt, .json, .srt, or .vtt export.");
        var session = store.GetSession(sessionId);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (extension == ".json")
                {
                    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                    writer.WriteStartObject();
                    writer.WriteNumber("schemaVersion", 1);
                    writer.WriteString("exportedUtc", DateTimeOffset.UtcNow);
                    writer.WritePropertyName("session");
                    JsonSerializer.Serialize(writer, session);
                    writer.WritePropertyName("tracks");
                    JsonSerializer.Serialize(writer, store.GetTracks(sessionId));
                    writer.WritePropertyName("speakers");
                    JsonSerializer.Serialize(writer, store.GetSpeakers(sessionId));
                    writer.WritePropertyName("transcript");
                    writer.WriteStartArray();
                    foreach (var row in store.EnumerateTranscript(sessionId))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        JsonSerializer.Serialize(writer, row);
                        if (writer.BytesPending > 65_536) await writer.FlushAsync(cancellationToken);
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    await writer.FlushAsync(cancellationToken);
                }
                else
                {
                    await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 65_536, leaveOpen: true);
                    if (extension == ".vtt") await writer.WriteLineAsync("WEBVTT\n");
                    if (extension == ".txt")
                        await writer.WriteLineAsync($"{session.Name}\nSource language: {session.Language}\n");
                    var index = 0;
                    if (extension == ".txt")
                    {
                        foreach (var line in TranscriptLine.Group(store.EnumerateTranscript(sessionId)))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await writer.WriteLineAsync(TextLine(line));
                        }
                    }
                    else foreach (var row in store.EnumerateTranscript(sessionId))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (extension == ".srt") await writer.WriteLineAsync((++index).ToString(CultureInfo.InvariantCulture));
                        var separator = extension == ".srt" ? ',' : '.';
                        await writer.WriteLineAsync($"{Time(row.StartTicks, separator)} --> {Time(row.EndTicks, separator)}");
                        await writer.WriteLineAsync(extension == ".vtt"
                            ? $"<v {WebUtility.HtmlEncode(row.SpeakerName)}>{WebUtility.HtmlEncode(row.Text)}</v>"
                            : $"{row.SpeakerName}: {row.Text}");
                        await writer.WriteLineAsync();
                    }
                    await writer.FlushAsync(cancellationToken);
                }
                stream.Flush(true);
            }
            File.Move(temporary, destination, true);
            if (extension != ".json")
            {
                var provenance = new
                {
                    schemaVersion = 1, exportedUtc = DateTimeOffset.UtcNow,
                    session, tracks = store.GetTracks(sessionId), speakers = store.GetSpeakers(sessionId),
                    note = "Cue times retain source timing granularity; they are not guaranteed word boundaries. Raw text and edits are separately available in JSON exports."
                };
                await File.WriteAllTextAsync(destination + ".provenance.json",
                    JsonSerializer.Serialize(provenance, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static string TextLine(TranscriptLine line) =>
        $"[{Time(line.StartTicks, '.')} - {Time(line.EndTicks, '.')}] {line.First.SpeakerName}: {line.Text}";

    private static string Time(long ticks, char separator)
    {
        var time = TimeSpan.FromTicks(ticks);
        return string.Create(CultureInfo.InvariantCulture,
            $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}{separator}{time.Milliseconds:000}");
    }
}
