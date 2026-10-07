# Teams and WebVTT

Open **VTT / Teams** on an existing session to import local timed WebVTT cues or
retrieve configured Microsoft Teams transcripts.

## WebVTT import

A local WebVTT import brings in timed cues and source speaker labels. The labels are
source attribution metadata, not invented or verified participant identities.

Imported cue times keep the source transcript clock. They are not automatically aligned
to an unrelated recording's audio, so do not treat them as verified synchronization
unless the source really matches.

## Teams transcript retrieval

The Teams dialog uses delegated Microsoft Graph transcript retrieval. It requires:

- a work/school tenant GUID;
- a public-client application GUID configured for device-code flow;
- `OnlineMeetingTranscript.Read.All`;
- explicit user, online meeting, and transcript identifiers.

A join link is not the meeting ID. The device verification URL, code, and expiration
appear in a dedicated window. Tokens are not displayed and are not mixed with NVIDIA
credentials.

An unattributed-content option is available only when tenant policy permits it. Do not
reconstruct identities to bypass tenant attribution restrictions.

Graph integration is configuration-required and is retrieval of an existing transcript,
not live Teams audio. Live Teams audio needs a separate Azure media-bot deployment and
policies.

Discord server voice channels are recorded live on the **Discord** tab; see
[Discord](Discord).
