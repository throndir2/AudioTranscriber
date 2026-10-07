# Managing Sessions

The session list is on the left. Right-click a session, or select it and press
**Delete**, for session actions. Shift-click selects a range and Ctrl-click adds or
removes one.

## Continue recording

Recent builds added continuing recording into an existing session. Use the app's session
actions to add more captured audio to a session instead of starting a separate one.

## Merge sessions

Select several sessions and right-click **Merge N selected sessions…**. This is for
split recordings that belong together. After merging, transcript rows from the same
known speaker can also display as one wrapped line when they are close together.

## Delete sessions

Right-click one or more sessions, press **Delete**, or use the trash button above the
list. **Delete sessions** can select sessions by age: older than a week, 30/90 days, 6
months, or a year.

Deleting removes that session's transcript, speakers, jobs, and retained audio from this
PC and cannot be undone. Exported files and the live transcript file are left alone.
Stop a recording before deleting its session.

## Re-transcribe with the current phrase pause

**Re-transcribe…** on the Transcript tab re-cuts a recorded or imported session using
the current **Pause that ends a phrase** setting. It replaces chunks, drops old
recognition jobs and recognized lines, and queues recognition plus speaker analysis
again.

Original audio is untouched. Manual speaker labels are kept as time-ranged hints for the
new lines they cover by at least half. Text corrections are discarded because
recognition is redone.

## Jobs

**Jobs** shows aggregate counts and the newest 100 job details or errors. Pause, resume,
and cancel affect transcription for the selected session, not recording. Resume is
explicit after fixing a key, consent, quota, model, or FFmpeg issue; the app does not
silently choose another provider.
