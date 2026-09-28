using System.Collections.Concurrent;
using AudioTranscriber.Application;
using AudioTranscriber.Storage;

namespace AudioTranscriber.App.Mcp;

/// <summary>Headless MCP hooks: drives the real <see cref="AppController"/> without any window.</summary>
public sealed class HeadlessMcpTools
{
    private readonly AppController controller;
    private readonly ConcurrentQueue<NotificationEntry> notifications = new();
    private long sequence;
    private double peakOutput, peakMicrophone;

    public HeadlessMcpTools(AppController controller)
    {
        this.controller = controller;
        controller.Notification += notification =>
        {
            notifications.Enqueue(new(Interlocked.Increment(ref sequence), DateTimeOffset.Now, notification.Message, notification.IsError));
            while (notifications.Count > 500) notifications.TryDequeue(out _);
        };
        controller.LevelsChanged += meter =>
        {
            peakOutput = Math.Max(peakOutput, meter.Output);
            peakMicrophone = Math.Max(peakMicrophone, meter.Microphone);
        };
    }

    public const string Instructions =
        "Headless AudioTranscriber: the real recording/transcription engine without a window. " +
        "Typical flow: status -> list_devices -> install_parakeet_model and install_diarization_models -> " +
        "import_audio or start_recording + play_audio + stop_recording -> wait_for_jobs -> get_transcript. " +
        "Use notifications to see background errors (for example diarization worker failures). Nothing is uploaded unless cloud_consent is true.";

    public IReadOnlyList<McpTool> Tools =>
    [
        McpTool.Create("status", "Engine status: data root, recording state, installed models, prerequisites, worker presence and recent errors.",
            _ => McpToolResult.Json(Status())),
        McpTool.Create("list_devices", "List available Windows output (loopback) and microphone endpoints. The first entry is the Windows default.",
            _ => McpToolResult.Json(new { outputs = controller.GetOutputDevices(), microphones = controller.GetMicrophoneDevices() })),
        McpTool.Create("list_providers", "List transcription providers (local-parakeet and local-whisper run on this PC; others are NVIDIA cloud).",
            _ => McpToolResult.Json(controller.Providers)),
        McpTool.Create("install_parakeet_model", "Download (465 MiB, SHA-256 verified) the default local Parakeet TDT v3 model.",
            async (_, token) =>
            {
                var progress = new LastValue();
                await controller.InstallParakeetModelAsync(progress, token);
                return McpToolResult.Json(new { ready = controller.ParakeetModelReady, last = progress.Value });
            }),
        McpTool.Create("set_gpu_parakeet", "Answer the one-time GPU question: enabled=true downloads the ~4.4 GB NVIDIA CUDA runtime " +
            "(accepting NVIDIA's CUDA/cuDNN license terms) and runs Parakeet on the NVIDIA GPU; false keeps the CPU. See status.localGpu.",
            args =>
            {
                controller.SetGpuParakeet(args.Bool("enabled", false));
                return McpToolResult.Json(new { enabled = controller.GpuParakeetEnabled, status = controller.LocalGpuStatus });
            }, ("enabled", "boolean", "true to use the GPU (large download, NVIDIA license terms), false for CPU only", true)),
        McpTool.Create("install_whisper_model", "Download (SHA-256 verified) and select a local Whisper model. Use tiny or base for fast tests.",
            async (args, token) =>
            {
                var progress = new LastValue();
                await controller.InstallWhisperModelAsync(args.String("model_id") ?? "base", progress, token);
                return McpToolResult.Json(new { selected = controller.WhisperModelPath, last = progress.Value });
            }, ("model_id", "string", "tiny | base | small | large-v3 | large-v3-turbo (default base)", false)),
        McpTool.Create("set_whisper_model", "Select an existing local whisper.cpp model file.",
            args =>
            {
                controller.SetLocalWhisperModel(args.RequireString("path"));
                return McpToolResult.Json(new { selected = controller.WhisperModelPath });
            }, ("path", "string", "Absolute path to a ggml-*.bin model", true)),
        McpTool.Create("install_diarization_models", "Download and hash-verify the 33.49 MB local speaker models.",
            async (_, token) =>
            {
                var progress = new LastValue();
                await controller.InstallDiarizationModelsAsync(progress, token);
                return McpToolResult.Json(new { ready = controller.DiarizationModelsReady, last = progress.Value });
            }),
        McpTool.Create("start_recording", "Start recording the selected Windows output (loopback) and optionally a microphone.",
            async (args, token) =>
            {
                var output = args.String("output_device_id") ?? controller.GetOutputDevices().FirstOrDefault()?.Id
                    ?? throw new InvalidOperationException("No active output endpoint.");
                var microphone = args.String("microphone_device_id");
                if (microphone == "default") microphone = controller.GetMicrophoneDevices().FirstOrDefault()?.Id
                    ?? throw new InvalidOperationException("No microphone endpoint.");
                peakOutput = peakMicrophone = 0;
                var continued = args.String("session_id");
                var session = continued is { Length: > 0 }
                    ? await controller.ContinueRecordingAsync(Guid.Parse(continued), output, microphone, args.Bool("reduce_echo", true), token)
                    : await controller.StartRecordingAsync(args.String("name") ?? $"MCP recording {DateTime.Now:HH:mm:ss}",
                        output, microphone, args.String("provider_id") ?? "local-parakeet", args.String("language") ?? "en",
                        args.Bool("cloud_consent", false), args.Bool("reduce_echo", true), token);
                return McpToolResult.Json(Describe(session));
            },
            ("session_id", "string", "Continue this existing session (new audio follows its current end; name/provider/language/consent are the session's own). Omit to start a new session.", false),
            ("name", "string", "Session name", false),
            ("output_device_id", "string", "Output endpoint id from list_devices (default: Windows default output)", false),
            ("microphone_device_id", "string", "Microphone id, or 'default'; omit to skip the microphone track", false),
            ("provider_id", "string", "Provider id (default local-parakeet; local-whisper for other languages)", false),
            ("language", "string", "Source language (default en)", false),
            ("cloud_consent", "boolean", "Allow NVIDIA upload for this session (default false). With local-whisper it only enables the hosted Parakeet fallback for low-confidence chunks when local Parakeet is not installed.", false),
            ("reduce_echo", "boolean", "Remove speaker audio from the microphone track before transcription (default true)", false)),
        McpTool.Create("stop_recording", "Stop the active recording and seal original audio. Transcription continues in the background.",
            async (_, token) =>
            {
                var id = controller.RecordingSessionId;
                await controller.StopRecordingAsync(token);
                return McpToolResult.Json(new
                {
                    session = id is { } value ? Describe(controller.Store.GetSession(value)) : null,
                    peakOutputLevel = Math.Round(peakOutput, 4), peakMicrophoneLevel = Math.Round(peakMicrophone, 4)
                });
            }),
        McpTool.Create("levels", "Current recording state and peak capture levels seen since recording started (non-zero output means audio was captured).",
            _ => McpToolResult.Json(new { controller.IsRecording, controller.RecordingSessionId, peakOutput, peakMicrophone })),
        McpTool.Create("play_audio", "Play a local audio file to a Windows output endpoint (so loopback recording captures it). Waits until playback ends.",
            async (args, token) => McpToolResult.Json(await AudioFilePlayer.PlayAsync(args.RequireString("path"),
                args.String("output_device_id"), args.Int("volume_percent", 100) / 100d, token)),
            ("path", "string", "Audio file (wav/mp3)", true),
            ("output_device_id", "string", "Output endpoint id (default: Windows default output)", false),
            ("volume_percent", "integer", "Playback volume 0-100 (default 100)", false)),
        McpTool.Create("import_audio", "Import a local audio/video file as a new session; returns after the original is retained and normalized.",
            async (args, token) =>
            {
                var path = Path.GetFullPath(args.RequireString("path"));
                var stream = args.Int("stream_index", -1);
                if (stream < 0)
                {
                    var probe = await controller.ProbeMediaAsync(path, token);
                    stream = probe.Streams.FirstOrDefault()?.Index ?? throw new InvalidOperationException("No audio stream in file.");
                }
                var session = await controller.ImportAudioAsync(args.String("name") ?? Path.GetFileNameWithoutExtension(path), path, stream,
                    args.String("provider_id") ?? "local-parakeet", args.String("language") ?? "en", args.Bool("cloud_consent", false), token);
                return McpToolResult.Json(Describe(session));
            },
            ("path", "string", "Local audio or video file", true),
            ("name", "string", "Session name (default: file name)", false),
            ("stream_index", "integer", "Audio stream index (default: first audio stream)", false),
            ("provider_id", "string", "Provider id (default local-parakeet; local-whisper for other languages)", false),
            ("language", "string", "Source language (default en)", false),
            ("cloud_consent", "boolean", "Allow NVIDIA upload for this session (default false). With local-whisper it only enables the hosted Parakeet fallback for low-confidence chunks when local Parakeet is not installed.", false)),
        McpTool.Create("list_sessions", "List saved sessions, newest first.",
            args => McpToolResult.Json(controller.Store.GetSessions(args.Int("limit", 20)).Select(Describe)),
            ("limit", "integer", "Maximum sessions (default 20)", false)),
        McpTool.Create("session_details", "Session state, tracks, job progress, job errors and speakers.",
            args => McpToolResult.Json(Details(args.RequireGuid("session_id"))),
            ("session_id", "string", "Session GUID", true)),
        McpTool.Create("wait_for_jobs", "Wait until the session has no queued or running jobs (or timeout). Returns progress and job errors.",
            async (args, token) =>
            {
                var id = args.RequireGuid("session_id");
                var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(args.Int("timeout_seconds", 300), 1, 3600));
                var started = DateTime.UtcNow;
                QueueProgress progress;
                while (true)
                {
                    progress = controller.Store.GetProgress(id);
                    var state = controller.Store.GetSession(id).State;
                    var idle = progress.Pending == 0 && progress.Running == 0 && state is not ("Importing" or "Stopping" or "Starting");
                    if (idle || DateTime.UtcNow >= deadline) break;
                    await Task.Delay(1000, token);
                }
                return McpToolResult.Json(new
                {
                    completed = progress.Pending == 0 && progress.Running == 0,
                    waitedSeconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1),
                    details = Details(id)
                });
            },
            ("session_id", "string", "Session GUID", true),
            ("timeout_seconds", "integer", "Maximum wait (default 300)", false)),
        McpTool.Create("get_transcript", "Transcript rows (time, track, speaker, text) for a session.",
            args =>
            {
                var id = args.RequireGuid("session_id");
                var tracks = controller.Store.GetTracks(id).ToDictionary(track => track.Id);
                var rows = controller.Store.GetTranscriptPage(id, args.String("search"), args.String("speaker_id"), limit: args.Int("limit", 200));
                return McpToolResult.Json(rows.Select(row => new
                {
                    id = row.Id,
                    time = row.Timestamp,
                    start = Math.Round(TimeSpan.FromTicks(row.StartTicks).TotalSeconds, 2),
                    end = Math.Round(TimeSpan.FromTicks(row.EndTicks).TotalSeconds, 2),
                    track = tracks.TryGetValue(row.TrackId, out var track) ? track.Kind : row.TrackId.ToString(),
                    speaker = row.SpeakerName, row.Uncertain, manual = row.ManualSpeaker, text = row.Text
                }));
            },
            ("session_id", "string", "Session GUID", true),
            ("search", "string", "Optional full-text search", false),
            ("speaker_id", "string", "Optional speaker id filter ('' for unassigned)", false),
            ("limit", "integer", "Maximum rows (default 200)", false)),
        McpTool.Create("analyze_speakers", "Queue local speaker diarization for a session (install_diarization_models first). Then wait_for_jobs.",
            async (args, token) =>
            {
                var id = args.RequireGuid("session_id");
                await controller.DiarizeSessionAsync(id, token);
                return McpToolResult.Json(Details(id));
            }, ("session_id", "string", "Session GUID", true)),
        McpTool.Create("control_jobs", "Pause, resume or cancel durable transcription jobs for a session.",
            args =>
            {
                var id = args.RequireGuid("session_id");
                switch (args.RequireString("action"))
                {
                    case "pause": controller.PauseTranscription(id); break;
                    case "resume": controller.ResumeTranscription(id); break;
                    case "cancel": controller.CancelTranscription(id); break;
                    default: throw new ArgumentException("action must be pause, resume or cancel.");
                }
                return McpToolResult.Json(Details(id));
            },
            ("session_id", "string", "Session GUID", true),
            ("action", "string", "pause | resume | cancel", true)),
        McpTool.Create("delete_sessions", "Permanently delete sessions with their transcript, speakers, jobs and retained audio. The recording session is refused.",
            async (args, token) =>
            {
                var ids = args.RequireString("session_ids").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Guid.Parse).ToArray();
                return McpToolResult.Json(await controller.DeleteSessionsAsync(ids, token));
            },
            ("session_ids", "string", "Comma-separated session GUIDs", true)),
        McpTool.Create("merge_sessions", "Stitch sessions into the earliest one: later recordings' audio, transcript and speakers follow it on one timeline. The other sessions are removed.",
            async (args, token) =>
            {
                var ids = args.RequireString("session_ids").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Guid.Parse).ToArray();
                return McpToolResult.Json(Describe(await controller.MergeSessionsAsync(ids, token)));
            },
            ("session_ids", "string", "Comma-separated session GUIDs (two or more)", true)),
        McpTool.Create("fill_speakers", "Match every unlabeled line to the speakers of the lines labeled manually (assign_speaker), by voice. Manual labels never change; unclear lines keep their label.",
            async (args, token) =>
            {
                var summary = await controller.FillSpeakersFromLabelsAsync(args.RequireGuid("session_id"), null, token);
                return McpToolResult.Json(new
                {
                    summary.Labeled, summary.Filled, summary.Changed, summary.Unmatched, summary.Skipped, summary.AgreementPercent,
                    perSpeaker = summary.PerSpeaker.Select(item => new { item.Name, item.Lines })
                });
            },
            ("session_id", "string", "Session GUID", true)),
        McpTool.Create("rename_speaker", "Rename a speaker throughout a session. Using another speaker's name merges the two (voice profiles combine).",
            async (args, token) =>
            {
                var id = args.RequireGuid("session_id");
                var kept = await controller.RenameSpeakerAsync(id, args.RequireString("speaker_id"), args.RequireString("name"), token);
                return McpToolResult.Json(new { kept, speakers = controller.Store.GetSpeakers(id) });
            },
            ("session_id", "string", "Session GUID", true),
            ("speaker_id", "string", "Speaker id from session_details", true),
            ("name", "string", "New display name", true)),
        McpTool.Create("assign_speaker", "Manually label transcript rows with a speaker (existing or new name; omit name for Unknown). " +
                "With speaker models installed the rows' voice is learned in the background and unknown windows are re-checked; then wait_for_jobs.",
            args =>
            {
                var id = args.RequireGuid("session_id");
                var rows = (args.RequireString("segment_ids")).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var name = args.String("name");
                var speaker = string.IsNullOrWhiteSpace(name) ? null : controller.GetOrCreateSpeaker(id, name);
                controller.AssignSpeaker(id, rows, speaker?.Id);
                return McpToolResult.Json(new { speaker, rows = controller.Store.GetSegments(id, rows).Select(row => new { row.Id, row.SpeakerName, row.ManualSpeaker }) });
            },
            ("session_id", "string", "Session GUID", true),
            ("segment_ids", "string", "Comma-separated row ids from get_transcript", true),
            ("name", "string", "Speaker name (created if new); omit for Unknown", false)),
        McpTool.Create("export_transcript", "Export a session transcript to .txt, .json, .srt or .vtt.",
            async (args, token) =>
            {
                var path = Path.GetFullPath(args.RequireString("path"));
                await TranscriptExporter.ExportAsync(controller.Store, args.RequireGuid("session_id"), path, token);
                return McpToolResult.Json(new { path, bytes = new FileInfo(path).Length });
            },
            ("session_id", "string", "Session GUID", true),
            ("path", "string", "Destination file (.txt/.json/.srt/.vtt)", true)),
        McpTool.Create("notifications", "Engine notifications (status and background errors) after a sequence number.",
            args =>
            {
                var after = args.Int("after", 0);
                var errorsOnly = args.Bool("errors_only", false);
                return McpToolResult.Json(notifications.Where(item => item.Sequence > after && (!errorsOnly || item.IsError))
                    .TakeLast(Math.Clamp(args.Int("limit", 50), 1, 500)));
            },
            ("after", "integer", "Only entries with a larger sequence number", false),
            ("errors_only", "boolean", "Only errors", false),
            ("limit", "integer", "Maximum entries (default 50)", false)),
    ];

    private object Status()
    {
        var prerequisites = Prerequisites.Check();
        return new
        {
            dataRoot = controller.Store.RootDirectory,
            appDirectory = AppContext.BaseDirectory,
            workerExecutable = File.Exists(Path.Combine(AppContext.BaseDirectory, "AudioTranscriber.Worker.dll"))
                ? Path.Combine(AppContext.BaseDirectory, "AudioTranscriber.Worker.exe") : "development worker (dotnet host)",
            controller.IsRecording, controller.RecordingSessionId,
            controller.DiarizationModelsReady, controller.ParakeetModelReady, controller.WhisperModelPath, controller.HasNvidiaKey, localGpu = controller.LocalGpuStatus, gpuOffer = controller.GpuOffer,
            prerequisites.FFmpeg, prerequisites.FFprobe, prerequisites.VcRuntimeReady,
            recentErrors = notifications.Where(item => item.IsError).TakeLast(5)
        };
    }

    private object Details(Guid id)
    {
        var jobs = controller.Store.GetJobs(id, 1000);
        return new
        {
            session = Describe(controller.Store.GetSession(id)),
            tracks = controller.Store.GetTracks(id).Select(track => new { track.Id, track.Kind, track.Name }),
            progress = controller.Store.GetProgress(id),
            jobs = jobs.GroupBy(job => (job.ProviderId, job.State)).Select(group => new { provider = group.Key.ProviderId, state = group.Key.State, count = group.Count() }),
            jobErrors = jobs.Where(job => job.Error is not null).Select(job => $"{job.ProviderId} [{job.State}]: {job.Error}").Distinct().Take(10),
            speakers = controller.Store.GetSpeakers(id).Select(speaker => new { speaker.Id, speaker.Name })
        };
    }

    private static object Describe(StoredSession session) => new
    {
        session.Id, session.Name, session.State, session.ProcessingState, session.ProviderId, session.Language,
        durationSeconds = Math.Round(TimeSpan.FromTicks(session.DurationTicks).TotalSeconds, 2), session.Error, session.CloudConsent
    };

    private sealed record NotificationEntry(long Sequence, DateTimeOffset Time, string Message, bool IsError);

    private sealed class LastValue : IProgress<string>
    {
        public string? Value { get; private set; }
        public void Report(string value) => Value = value;
    }
}
