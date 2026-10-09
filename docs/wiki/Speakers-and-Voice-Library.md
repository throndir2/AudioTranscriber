# Speakers and Voice Library

Speaker labels are editable display labels, not verified identities. The speaker model
matches voices, not names.

## Set who is speaking

You can label one or more transcript lines by right-clicking and choosing **Set speaker
for …**, by typing a name in **Selected line**, or by renaming on the **Speaker names**
tab.

A name that already exists, case-insensitively, reuses that speaker. Choosing the name
of another speaker merges the two after confirmation. Lines you label show **Set by
you** and later analysis does not change them.

## Fill speakers

The quickest way to label a whole recording:

1. Right-click a few lines for each person and **Set speaker**.
2. Press **Fill speakers from my labels** on the **Transcript** tab.

Every other line in the recording gets the labeled person whose voice it sounds closest
to. Lines you labeled are never changed; unclear lines keep their label (label a few of
those and run it again). It runs locally.

When you label clear speech, the app also embeds that voice into the speaker's profile
and re-checks unknown speech windows. **Analyze speakers** re-checks every window with
the current profiles.

## Name the microphone

The microphone track is usually one person: you. Type a name in **Label microphone lines
as** (Record / import) and every microphone line of new recordings gets that speaker. For
an existing session, right-click it (or use **Speaker names**) and choose **Name all
microphone lines…**; lines transcribed later get the same speaker.

Short lines can be labeled but may be too short to learn. Overlap, crowding, similar
voices, character voices, or too little clean speech can stay unknown.

## Voice library

When you give a speaker a name, up to three voice samples from that session are saved to
the voice library. They are 256-number embeddings, not audio. A voice keeps the newest
15 samples, at most 3 per session speaker.

New sessions compare automatic speakers such as **Speaker 1** to the library once
that speaker has about 20 seconds of clear speech. A close, clear match renames the
speaker; if that name is already present in the session, the speakers merge.

Automatic matches never add samples, so a wrong match cannot reinforce itself. Rename a
remembered speaker to move samples to the new name, or rename back to **Speaker N** to
keep that session's speaker unnamed.

Use **Speaker names → Match known voices** for the open session (also while recording), **Remember this
session's named voices** to add one session, and **Privacy / models → Voice library** to
rename or forget remembered voices. Forgetting deletes the library samples; past
sessions keep their labels.

For implementation limits and thresholds, see
[docs/diarization.md](https://github.com/throndir2/AudioTranscriber/blob/main/docs/diarization.md).
