# Discord

The **Discord** tab records a voice channel in a Discord server through **your own
Discord bot**. Discord sends each person's voice as a separate stream, so every
transcript line is named after the Discord user who said it (server nickname, else
display name, else username).

Those names behave like names you set by hand: speaker analysis never overrides them.
With the speaker models installed and **Remember voices** on, each person's voice print
is learned from their own lines and saved to the voice library under their Discord name,
so later recordings from any source recognize them.

## Set up the bot

Discord has no API that creates an application, so the tab walks you through it:

1. **Open the Discord Developer Portal** (<https://discord.com/developers/applications>)
   and choose **New Application**.
2. On **Bot**, choose **Reset Token**, copy the token, paste it into the tab and choose
   **Save and connect**. The token is encrypted for your Windows account (DPAPI) in
   `discord.json` in the library folder and is only sent to Discord. You may turn off
   **Public Bot**; no privileged gateway intents are needed.
3. **Add the bot to a server** opens Discord's invite page with only **View Channels**
   and **Connect**. You need **Manage Server** there, or send the copied link to someone
   who has it.

The bot connects automatically when the app starts if it was connected when the app
closed.

## Record a channel

Pick the server and voice channel, then **Join and record**. This starts a new session
from the Record / import settings (provider, language, live file) with the Discord
channel as its output track and no microphone track (Discord already carries everyone,
including you). Stop with **Stop recording**; **Leave** is available once the recording
stops.

**Join only** joins without recording; the channel then appears as an output choice on
Record / import, so **Continue recording** works too.

Everyone in the channel sees the bot join. Tell people they are being recorded and get
their consent. Bots cannot join DM or group-DM calls; record Windows output for those.

## Network: firewall, UPnP and port forwarding

Voice audio arrives over UDP on one fixed local port (**50505** by default, set on the
tab). Most networks need nothing. The voice status says **audio arriving** once packets
come in; if Discord reports people talking but **no audio arrives**, UDP is blocked:

- **Allow in Windows Firewall** adds an inbound UDP rule for this app on that port after
  one UAC prompt.
- **UPnP** (on by default) asks the router to forward the port each time the bot joins;
  the mapping is removed when the app closes. **Try UPnP now** shows the router's answer.
- **Forward the port by hand**: the tab shows the steps with this PC's LAN address and
  the router's admin page. Carrier-grade NAT can't be forwarded.

## Privacy

Voice stays end-to-end encrypted by Discord (DAVE) until this PC decrypts it with
Discord's official `libdave.dll`. The audio is then archived and transcribed like any
other track, following the session's provider and consent. Voice prints are embeddings,
never audio.

More detail: [docs/usage.md](https://github.com/throndir2/AudioTranscriber/blob/main/docs/usage.md#discord-server-voice-channels).
