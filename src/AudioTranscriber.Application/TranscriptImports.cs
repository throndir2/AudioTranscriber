using System.Security.Cryptography;
using System.Text;
using AudioTranscriber.Integrations;
using AudioTranscriber.Storage;

namespace AudioTranscriber.Application;

public static class TranscriptImports
{
    public static async Task<int> ImportWebVttAsync(
        LibraryStore store, Guid sessionId, string sourcePath, CancellationToken cancellationToken = default,
        bool allowVoiceLabels = true)
    {
        var session = store.GetSession(sessionId);
        var source = Path.GetFullPath(sourcePath);
        string hash;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                         65_536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
        var directory = Path.Combine(session.Directory, "transcripts");
        Directory.CreateDirectory(directory);
        var retainedPath = Path.Combine(directory, hash + ".vtt");
        if (!File.Exists(retainedPath))
        {
            var partial = retainedPath + ".partial";
            try
            {
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                                 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                                 65_536, FileOptions.Asynchronous))
                {
                    await input.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    output.Flush(true);
                }
                await using (var copy = File.OpenRead(partial))
                {
                    if (!Convert.ToHexString(await SHA256.HashDataAsync(copy, cancellationToken))
                            .Equals(hash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The transcript source changed while it was being imported.");
                }
                File.Move(partial, retainedPath, false);
            }
            finally
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
        }

        await using (var retained = File.OpenRead(retainedPath))
        {
            if (!Convert.ToHexString(await SHA256.HashDataAsync(retained, cancellationToken))
                    .Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The retained transcript no longer matches its recorded content hash.");
        }
        var tracks = store.GetTracks(sessionId);
        var track = tracks.FirstOrDefault(t => t.Kind != "Transcript") ??
                    tracks.FirstOrDefault(t => t.OriginalPath == retainedPath);
        if (track is null)
        {
            track = new(Guid.NewGuid(), sessionId, "Transcript", "Imported WebVTT", retainedPath, 0,
                "{\"timing\":\"source-cues\",\"alignment\":\"not-verified\"}");
            store.AddTrack(track);
        }
        using var reader = new StreamReader(retainedPath, Encoding.UTF8, true, 65_536);
        var index = 0;
        await foreach (var cue in WebVttReader.ReadAsync(reader, cancellationToken))
        {
            string? speakerId = null;
            if (allowVoiceLabels && !string.IsNullOrWhiteSpace(cue.SpeakerLabel))
            {
                var labelHash = SHA256.HashData(Encoding.UTF8.GetBytes(hash + "\n" + cue.SpeakerLabel));
                speakerId = "vtt-" + Convert.ToHexString(labelHash).ToLowerInvariant()[..24];
                store.UpsertSpeaker(new(speakerId, sessionId, cue.SpeakerLabel, null, "WebVTT source label, not a verified participant identifier"));
            }
            store.ImportCue(sessionId, track.Id, $"{sessionId:N}-vtt-{hash}-{index:D8}",
                new(cue.StartTicks, cue.EndTicks, cue.Text, speakerId, "Segment",
                    $"WebVTT SHA256 {hash}; retained {retainedPath}; cue {index}; voice attribution permitted {allowVoiceLabels}; session alignment not verified",
                    speakerId is null));
            index++;
        }
        if (index == 0) throw new InvalidDataException("The WebVTT transcript contains no speech cues.");
        return index;
    }
}
