# Transcripts and Search

Select a session and open **Transcript**. The grid shows the whole transcript. During
recording, new lines appear at the bottom and the grid follows them while you are
already scrolled to the end.

## Search and jump

Search uses effective text, including saved corrections, and can be combined with a
speaker filter. **Find / jump** accepts seconds, `mm:ss`, or `hh:mm:ss`; hours can
exceed 23.

## Corrections

Click a line to edit it below the grid. The left box is your correction; **Save text**
stores it. The right box preserves raw recognition. **Restore raw** removes your
correction. Raw recognition and corrections stay separate.

Consecutive rows from the same known speaker and track may be displayed as one wrapped
line when close together. This is display-only; JSON, SRT, and WebVTT keep the original
rows.

## Playback

Double-click a line, choose **▶ Play**, or right-click **Play line**. Playback uses that
line's own track and stored start timestamp. **Track playback** lets you choose an
original track and timestamp separately.

Playback is one track at a time. Coarse cues do not become word-accurate seeking, and a
VTT-only track without linked audio cannot be played.

## Exports

**Export** writes the whole selected transcript, independent of the current search, as
TXT, JSON, SRT, or WebVTT. JSON keeps raw text, corrections, timing, and source fields.
Text and subtitle exports include a provenance sidecar.

## Live transcript file

On **Record / import**, check **Write the transcript to a live text file** and choose a
path. The default is `Documents\AudioTranscriber\live-transcript.txt`; an existing file
is overwritten. The app rewrites the file when recognized text changes and at least
every about 3 seconds.

The live file uses the same merged TXT-style lines as export: `[hh:mm:ss.fff -
hh:mm:ss.fff] Speaker: text`. Corrections and speaker renames appear on the next
refresh. The file is not locked exclusively, so VS Code or another reader can keep it
open. Treat it as read-only because edits are overwritten.
