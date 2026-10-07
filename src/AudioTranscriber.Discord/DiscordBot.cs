using System.Collections.Concurrent;
using AudioTranscriber.Audio;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;

namespace AudioTranscriber.Discord;

public enum DiscordBotState { Off, Connecting, Online, Failed }

public sealed record DiscordBotStatus(DiscordBotState State, string? BotName = null, ulong BotId = 0, int Servers = 0, string? Problem = null);

public sealed record DiscordVoiceChannel(ulong GuildId, ulong ChannelId, string Server, string Channel, int People)
{
    public string Display => $"{Server} › {Channel}" + (People > 0 ? $"  ({People} in it)" : "");
}

/// <summary>The user's own Discord bot, hosted in this app: one gateway connection (server list and voice states only, no
/// privileged intents) and at most one voice channel joined to record. The bot only listens; it never speaks or reads chat.</summary>
public sealed class DiscordBot : IAsyncDisposable
{
    private const GatewayIntents Intents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates;
    private readonly Lock gate = new();
    private readonly ConcurrentDictionary<ulong, string> names = new();
    private GatewayClient? client;
    private DiscordBotStatus status = new(DiscordBotState.Off);
    private VoiceClient? voice;
    private DiscordVoiceRecorder? recorder;
    private DiscordVoiceChannel? joined;
    private string? sourceId, voiceProblem;
    private FixedPortUdpConnectionProvider? udp;

    public DiscordBotStatus Status { get { lock (gate) return status; } }
    public DiscordVoiceChannel? Joined => joined;
    public DiscordVoiceRecorder? Recorder => recorder;
    /// <summary>The capture device ID of the joined channel (offered beside the Windows outputs), or null.</summary>
    public string? SourceId => sourceId;
    /// <summary>Local UDP port for voice; firewall rule, UPnP and manual forwarding all use it.</summary>
    public int VoicePort { get; set; } = DiscordNetwork.DefaultPort;
    public int? BoundVoicePort => udp?.BoundPort is > 0 and var port ? port : null;
    /// <summary>Raised off the UI thread when status, servers, channels or the joined channel change.</summary>
    public event Action? Changed;

    public static string VoiceLibraryName => OperatingSystem.IsWindows() ? "libdave.dll" : "libdave.so";
    public static bool VoiceLibraryPresent => File.Exists(Path.Combine(AppContext.BaseDirectory, VoiceLibraryName));

    public async Task StartAsync(string token, CancellationToken cancellation = default)
    {
        await StopAsync().ConfigureAwait(false);
        GatewayClient started = new(new BotToken(token.Trim()), new GatewayClientConfiguration { Intents = Intents });
        started.Ready += ready =>
        {
            Publish(new(DiscordBotState.Online, ready.User.GlobalName ?? ready.User.Username, ready.User.Id, started.Cache.Guilds.Count));
            return default;
        };
        started.GuildCreate += _ => { Recount(started); return default; };
        started.GuildDelete += _ => { Recount(started); return default; };
        started.VoiceStateUpdate += state =>
        {
            if (state.User is { } user) names[user.Id] = NameOf(user);
            if (state.UserId == Status.BotId && joined is { } current && state.GuildId == current.GuildId && state.ChannelId != current.ChannelId)
                voiceProblem = state.ChannelId is null ? "Someone disconnected the bot from the voice channel." : "Someone moved the bot to another channel.";
            Changed?.Invoke();
            return default;
        };
        started.Disconnect += args =>
        {
            if (!args.Reconnect) Publish(new(DiscordBotState.Failed, Status.BotName, Status.BotId, 0, DescribeClose((int?)args.CloseStatus)));
            return default;
        };
        lock (gate) client = started;
        Publish(new(DiscordBotState.Connecting));
        try { await started.StartAsync(cancellationToken: cancellation).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            lock (gate) if (client == started) client = null;
            started.Dispose();
            Publish(new(DiscordBotState.Failed, Problem: Describe(error)));
        }
    }

    public async Task StopAsync()
    {
        await LeaveAsync().ConfigureAwait(false);
        GatewayClient? stopping;
        lock (gate) { stopping = client; client = null; }
        if (stopping is null) return;
        try { await stopping.CloseAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or System.Net.WebSockets.WebSocketException) { }
        stopping.Dispose();
        Publish(new(DiscordBotState.Off));
    }

    /// <summary>Voice and stage channels in every server the bot is in, with how many people are in each now.</summary>
    public IReadOnlyList<DiscordVoiceChannel> VoiceChannels()
    {
        if (Client is not { } current) return [];
        var bot = Status.BotId;
        return current.Cache.Guilds.Values.OrderBy(guild => guild.Name, StringComparer.CurrentCultureIgnoreCase).SelectMany(guild =>
            guild.Channels.Values.OfType<TextGuildChannel>().Where(channel => channel is IVoiceGuildChannel)
                .OrderBy(channel => channel.Position ?? 0)
                .Select(channel => new DiscordVoiceChannel(guild.Id, channel.Id, guild.Name, channel.Name,
                    guild.VoiceStates.Values.Count(state => state.ChannelId == channel.Id && state.UserId != bot))))
            .ToArray();
    }

    /// <summary>Joins a voice channel and offers it as a recording source. The bot shows in the channel, so everyone there can see it.</summary>
    public async Task<DiscordVoiceRecorder> JoinAsync(DiscordVoiceChannel channel, CancellationToken token = default)
    {
        if (Client is not { } current || Status.State != DiscordBotState.Online) throw new InvalidOperationException("Connect the Discord bot first.");
        if (!VoiceLibraryPresent) throw new InvalidOperationException($"{VoiceLibraryName} (Discord's voice encryption) is missing beside the app; reinstall the app.");
        if (recorder?.Recording == true) throw new InvalidOperationException("Stop the recording before switching channels.");
        await LeaveAsync().ConfigureAwait(false);
        foreach (var state in current.Cache.Guilds.GetValueOrDefault(channel.GuildId)?.VoiceStates.Values ?? [])
            if (state.User is { } user) names[user.Id] = NameOf(user);
        udp = new FixedPortUdpConnectionProvider(VoicePort);
        VoiceClient? connection = null;
        DiscordVoiceRecorder? created = null;
        try
        {
            connection = await current.JoinVoiceChannelAsync(channel.GuildId, channel.ChannelId,
                new VoiceClientConfiguration { UdpConnectionProvider = udp }, TimeSpan.FromSeconds(10), cancellationToken: token).ConfigureAwait(false);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cache = connection;
            created = new DiscordVoiceRecorder($"Discord: {channel.Server} › {channel.Channel}",
                ssrc => cache.Cache.SsrcUsers.TryGetValue(ssrc, out var user) ? user : null, user => SpeakerName(channel.GuildId, user));
            var heard = created;
            connection.Ready += () => { ready.TrySetResult(); return default; };
            connection.VoiceReceive += args => { heard.Receive(args.Ssrc, args.SequenceNumber, args.Frame); return default; };
            connection.Speaking += args => { if (args.UserId != Status.BotId) heard.NoteSpeaking(); return default; };
            connection.Disconnect += args =>
            {
                if (!args.Reconnect) { voiceProblem = "Discord closed the voice connection."; Changed?.Invoke(); }
                return default;
            };
            await connection.StartAsync(token).ConfigureAwait(false);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            created?.Dispose();
            connection?.Dispose();
            try { await current.UpdateVoiceStateAsync(new(channel.GuildId, null)).ConfigureAwait(false); }
            catch (Exception leave) when (leave is not OutOfMemoryException) { }
            voiceProblem = error is TimeoutException ? "Discord voice didn't connect in time." : "Couldn't join the voice channel: " + error.Message;
            Changed?.Invoke();
            throw new InvalidOperationException(voiceProblem, error);
        }
        voice = connection;
        recorder = created;
        joined = channel;
        voiceProblem = null;
        sourceId = ExternalAudioSources.Register($"discord:{channel.GuildId}:{channel.ChannelId}", created);
        Changed?.Invoke();
        return created;
    }

    public async Task LeaveAsync()
    {
        var leaving = joined;
        if (sourceId is { } id) ExternalAudioSources.Unregister(id);
        sourceId = null;
        recorder?.Dispose();
        recorder = null;
        if (voice is { } connection)
        {
            try { await connection.CloseAsync().ConfigureAwait(false); }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or System.Net.WebSockets.WebSocketException) { }
            connection.Dispose();
        }
        voice = null;
        joined = null;
        if (leaving is not null && Client is { } current)
        {
            try { await current.UpdateVoiceStateAsync(new(leaving.GuildId, null)).ConfigureAwait(false); }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or System.Net.WebSockets.WebSocketException) { }
        }
        if (leaving is not null) Changed?.Invoke();
    }

    /// <summary>One line about the voice connection: where, whether audio arrives, and the last problem.</summary>
    public string VoiceSummary
    {
        get
        {
            if (joined is not { } channel) return voiceProblem ?? "Not in a voice channel.";
            var heard = recorder;
            var port = BoundVoicePort is { } bound ? $" · UDP port {bound}" : "";
            var audio = heard is null ? "" : heard.AudioBlocked
                ? $" · Discord says people are talking but no audio arrives: UDP{port.Replace(" · UDP", "")} is blocked. Allow it in Windows Firewall, try UPnP, or forward the port (Network, below)."
                : heard.PacketsReceived == 0 ? " · waiting for someone to talk" : $" · audio arriving ({heard.PacketsReceived:N0} packets)";
            return $"In {channel.Channel} ({channel.Server}){port}{(heard?.Recording == true ? " · RECORDING" : "")}{audio}" +
                (voiceProblem is { } problem ? " · " + problem : "") + (heard?.Problem is { } other ? " · " + other : "");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private GatewayClient? Client { get { lock (gate) return client; } }

    private string SpeakerName(ulong guildId, ulong userId)
    {
        if (Client?.Cache.Guilds.GetValueOrDefault(guildId) is { } guild &&
            (guild.Users.GetValueOrDefault(userId) ?? guild.VoiceStates.GetValueOrDefault(userId)?.User) is { } user)
            return names[userId] = NameOf(user);
        return names.GetValueOrDefault(userId) ?? $"Discord user {userId}";
    }

    private static string NameOf(GuildUser user) => FirstNonEmpty(user.Nickname, user.GlobalName, user.Username) ?? $"Discord user {user.Id}";
    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private void Recount(GatewayClient source)
    {
        lock (gate)
        {
            if (client != source || status.State != DiscordBotState.Online) return;
            status = status with { Servers = source.Cache.Guilds.Count };
        }
        Changed?.Invoke();
    }

    private void Publish(DiscordBotStatus next)
    {
        lock (gate) status = next;
        Changed?.Invoke();
    }

    private static string Describe(Exception error) => error.Message.Contains("4004", StringComparison.Ordinal)
        ? "Discord rejected the bot token. Reset it on the Bot page of the Developer Portal and paste the new one."
        : $"Couldn't connect to Discord: {error.Message}";

    private static string DescribeClose(int? code) => code switch
    {
        4004 => "Discord rejected the bot token. Reset it on the Bot page of the Developer Portal and paste the new one.",
        4014 => "Discord refused an intent the bot asked for.",
        null => "Discord closed the connection.",
        _ => $"Discord closed the connection ({code})."
    };
}
