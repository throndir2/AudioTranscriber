using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Threading;
using AudioTranscriber.Application;
using AudioTranscriber.Discord;
using AudioTranscriber.Providers;

namespace AudioTranscriber.App;

/// <summary>Discord tab: the user's own bot (token encrypted for this OS user in discord.json), joining a server voice
/// channel to record it with every line named after the Discord user speaking, and the network help voice may need.</summary>
public sealed class DiscordViewModel : ObservableObject
{
    private readonly IAppController controller;
    private readonly Dispatcher dispatcher;
    private readonly Func<string, Task> recordFrom;
    private readonly Action devicesChanged;
    private readonly Action<string, bool> log;
    private readonly string settingsPath;
    private readonly DiscordBot bot = new();
    private readonly DispatcherTimer poll;
    private Settings settings;
    private string tokenInput = "", networkStatus = "", firewallStatus = "Not checked yet.";
    private DiscordVoiceChannel? selectedChannel;
    private bool working, mapped;
    private int queued;

    public bool IsWindows { get; } = OperatingSystem.IsWindows();


    public DiscordViewModel(IAppController controller, Dispatcher dispatcher, string dataRoot, Func<string, Task> recordFrom,
        Action devicesChanged, Action<string, bool> log)
    {
        this.controller = controller;
        this.dispatcher = dispatcher;
        this.recordFrom = recordFrom;
        this.devicesChanged = devicesChanged;
        this.log = log;
        settingsPath = Path.Combine(dataRoot, "discord.json");
        settings = Load(settingsPath);
        bot.VoicePort = settings.VoicePort;
        bot.Changed += () =>
        {
            if (Interlocked.Exchange(ref queued, 1) == 0)
                dispatcher.Post(() => { Interlocked.Exchange(ref queued, 0); Refresh(); }, DispatcherPriority.Background);
        };
        OpenPortalCommand = new RelayCommand(() => Open(DiscordSetup.PortalUrl));
        OpenBotPageCommand = new RelayCommand(() => Open(DiscordSetup.BotPage(ApplicationId)), () => ApplicationId != 0);
        InviteCommand = new RelayCommand(() => Open(DiscordSetup.InviteUrl(ApplicationId)), () => ApplicationId != 0);
        CopyInviteCommand = new RelayCommand(() => Copy(DiscordSetup.InviteUrl(ApplicationId)), () => ApplicationId != 0);
        ConnectCommand = new AsyncCommand(SaveAndConnectAsync, () => !working && (DiscordSetup.LooksLikeToken(TokenInput) || HasToken));
        DisconnectCommand = new AsyncCommand(() => Work(async () => { await bot.StopAsync(); Save(settings with { AutoConnect = false }); }),
            () => !working && bot.Status.State != DiscordBotState.Off && !RecordingDiscord);
        ForgetCommand = new AsyncCommand(ForgetAsync, () => !working && HasToken && !RecordingDiscord);
        RefreshChannelsCommand = new RelayCommand(Refresh);
        JoinCommand = new AsyncCommand(() => JoinAsync(record: false), CanJoin);
        JoinAndRecordCommand = new AsyncCommand(() => JoinAsync(record: true), () => CanJoin() && !controller.IsRecording);
        LeaveCommand = new AsyncCommand(() => Work(async () => { await bot.LeaveAsync(); devicesChanged(); }),
            () => !working && bot.Joined is not null && !RecordingDiscord);
        FirewallCommand = new AsyncCommand(AllowFirewallAsync, () => !working);
        UpnpCommand = new AsyncCommand(MapUpnpAsync, () => !working);
        CopyPortForwardCommand = new RelayCommand(() => Copy(PortForwardSteps));
        poll = NewTimer(TimeSpan.FromSeconds(2), (_, _) => Refresh());
    }

    private static DispatcherTimer NewTimer(TimeSpan interval, EventHandler tick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += tick;
        return timer;
    }
    public string Steps => DiscordSetup.Steps;
    public ObservableCollection<DiscordVoiceChannel> Channels { get; } = [];
    public DiscordVoiceChannel? SelectedChannel { get => selectedChannel; set => Set(ref selectedChannel, value); }
    public string TokenInput { get => tokenInput; set { Set(ref tokenInput, value); CommandManager.InvalidateRequerySuggested(); } }
    public bool HasToken => settings.ProtectedToken is not null;
    public ulong ApplicationId => settings.ApplicationId;
    public string InviteUrl => ApplicationId == 0 ? "Save the bot token first; the invite link is built from it." : DiscordSetup.InviteUrl(ApplicationId);
    public string BotStatus => bot.Status switch
    {
        { State: DiscordBotState.Online } s => $"Connected as {s.BotName} · in {s.Servers} server{(s.Servers == 1 ? "" : "s")}" +
            (s.Servers == 0 ? ". Use Add the bot to a server." : "."),
        { State: DiscordBotState.Connecting } => "Connecting to Discord…",
        { State: DiscordBotState.Failed } s => s.Problem ?? "Discord connection failed.",
        _ => HasToken ? "Not connected. Choose Connect." : "No bot yet. Follow the steps above."
    };
    public string VoiceStatus => bot.VoiceSummary;
    public bool RecordingDiscord => bot.Recorder?.Recording == true;
    public int VoicePort
    {
        get => settings.VoicePort;
        set
        {
            var port = Math.Clamp(value, 1024, 65535);
            Save(settings with { VoicePort = port });
            bot.VoicePort = port;
            Changed();
            Changed(nameof(PortForwardSteps));
        }
    }
    public bool UseUpnp { get => settings.UseUpnp; set { Save(settings with { UseUpnp = value }); Changed(); } }
    public string NetworkStatus { get => networkStatus; private set => Set(ref networkStatus, value); }
    public string FirewallStatus { get => firewallStatus; private set => Set(ref firewallStatus, value); }
    public string PortForwardSteps => DiscordNetwork.PortForwardSteps(VoicePort);

    public ICommand OpenPortalCommand { get; }
    public ICommand OpenBotPageCommand { get; }
    public ICommand InviteCommand { get; }
    public ICommand CopyInviteCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ForgetCommand { get; }
    public ICommand RefreshChannelsCommand { get; }
    public ICommand JoinCommand { get; }
    public ICommand JoinAndRecordCommand { get; }
    public ICommand LeaveCommand { get; }
    public ICommand FirewallCommand { get; }
    public ICommand UpnpCommand { get; }
    public ICommand CopyPortForwardCommand { get; }

    /// <summary>Connects at startup when a bot was saved and left connected.</summary>
    public void AutoConnect()
    {
        poll.Start();
        _ = CheckFirewallAsync();
        if (settings.AutoConnect && Token() is { } token) _ = Work(() => ConnectAsync(token));
    }

    public async Task ShutdownAsync()
    {
        poll.Stop();
        try
        {
            if (mapped) await DiscordNetwork.RemovePortMappingAsync(VoicePort);
            await bot.DisposeAsync();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    private bool CanJoin() => !working && bot.Status.State == DiscordBotState.Online && SelectedChannel is not null && !RecordingDiscord;

    private async Task SaveAndConnectAsync()
    {
        var typed = TokenInput.Trim();
        if (typed.Length > 0)
        {
            if (!DiscordSetup.LooksLikeToken(typed))
            {
                log("That doesn't look like a bot token. Copy it from the Bot page (Reset Token) in the Developer Portal.", true);
                return;
            }
            Save(settings with
            {
                ProtectedToken = Convert.ToBase64String(UserSecretProtection.Protect(Encoding.UTF8.GetBytes(typed))),
                ApplicationId = DiscordSetup.ApplicationIdFromToken(typed)
            });
            TokenInput = "";
            Changed(nameof(HasToken)); Changed(nameof(ApplicationId)); Changed(nameof(InviteUrl));
        }
        if (Token() is not { } token) return;
        Save(settings with { AutoConnect = true });
        await Work(() => ConnectAsync(token));
    }

    private async Task ConnectAsync(string token)
    {
        await bot.StartAsync(token);
        for (var waited = 0; waited < 50 && bot.Status.State == DiscordBotState.Connecting; waited++) await Task.Delay(200);
        Refresh();
        if (bot.Status.State == DiscordBotState.Online) log("Discord: " + BotStatus, false);
        else if (bot.Status.State == DiscordBotState.Failed) log("Discord: " + BotStatus, true);
    }

    private Task ForgetAsync() => Work(async () =>
    {
        await bot.StopAsync();
        Save(new Settings { VoicePort = settings.VoicePort, UseUpnp = settings.UseUpnp });
        Changed(nameof(HasToken)); Changed(nameof(ApplicationId)); Changed(nameof(InviteUrl));
        log("Discord bot token forgotten on this PC. Delete or reset the application in the Developer Portal if you no longer need it.", false);
    });

    private Task JoinAsync(bool record) => Work(async () =>
    {
        if (SelectedChannel is not { } channel) return;
        if (settings.UseUpnp) await MapAsync();
        var recorder = await bot.JoinAsync(channel);
        recorder.Spoke += speech => controller.LabelTrackSpeech(speech.SessionId, speech.TrackId, speech.Name, speech.StartTicks, speech.EndTicks);
        Save(settings with { GuildId = channel.GuildId, ChannelId = channel.ChannelId });
        devicesChanged();
        log($"Discord: joined {channel.Channel} in {channel.Server}. It is now an output choice on Record / import.", false);
        if (record && bot.SourceId is { } id)
        {
            working = false;
            await recordFrom(id);
        }
    });

    private async Task AllowFirewallAsync()
    {
        await Work(async () =>
        {
            var added = await DiscordNetwork.AddFirewallRuleAsync(VoicePort);
            FirewallStatus = added ? $"Windows Firewall allows UDP {VoicePort} for this app." : "The firewall rule wasn't added (approval declined or failed).";
        });
    }

    private async Task CheckFirewallAsync()
    {
        var exists = await DiscordNetwork.FirewallRuleExistsAsync(VoicePort);
        FirewallStatus = exists switch
        {
            true => $"Windows Firewall allows UDP {VoicePort} for this app.",
            false => $"No firewall rule yet. Usually not needed; add it if audio doesn't arrive or Windows asks about this app.",
            _ => "Couldn't read Windows Firewall rules."
        };
    }

    private Task MapUpnpAsync() => Work(MapAsync);

    private async Task MapAsync()
    {
        var result = await DiscordNetwork.MapPortAsync(VoicePort);
        mapped |= result.Mapped;
        NetworkStatus = result.Message;
    }

    private async Task Work(Func<Task> action)
    {
        if (working) return;
        working = true;
        CommandManager.InvalidateRequerySuggested();
        try { await action(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or TimeoutException or CryptographicException or
            System.Net.Http.HttpRequestException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            log("Discord: " + error.Message, true);
        }
        finally
        {
            working = false;
            Refresh();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void Refresh()
    {
        var channels = bot.VoiceChannels();
        var keep = SelectedChannel ?? channels.FirstOrDefault(item => item.GuildId == settings.GuildId && item.ChannelId == settings.ChannelId);
        if (!Channels.SequenceEqual(channels))
        {
            Channels.Clear();
            foreach (var channel in channels) Channels.Add(channel);
        }
        SelectedChannel = keep is null ? Channels.FirstOrDefault()
            : Channels.FirstOrDefault(item => item.GuildId == keep.GuildId && item.ChannelId == keep.ChannelId) ?? Channels.FirstOrDefault();
        Changed(nameof(BotStatus));
        Changed(nameof(VoiceStatus));
        Changed(nameof(RecordingDiscord));
    }

    private string? Token()
    {
        if (settings.ProtectedToken is not { } stored) return null;
        try { return Encoding.UTF8.GetString(UserSecretProtection.Unprotect(Convert.FromBase64String(stored))); }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            log("The saved Discord token can't be read on this user account; paste it again.", true);
            return null;
        }
    }

    private void Save(Settings next)
    {
        settings = next;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log("Couldn't save Discord settings: " + error.Message, true); }
    }

    private static Settings Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    private void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { Copy(url); }
    }

    private void Copy(string text)
    {
        try { _ = DesktopDialogs.SetClipboardTextAsync(text); log("Copied to the clipboard.", false); }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException) { log("The clipboard is unavailable; copy manually: " + text, true); }
    }

    private sealed record Settings
    {
        public string? ProtectedToken { get; init; }
        public ulong ApplicationId { get; init; }
        public bool AutoConnect { get; init; }
        public int VoicePort { get; init; } = DiscordNetwork.DefaultPort;
        public bool UseUpnp { get; init; } = true;
        public ulong GuildId { get; init; }
        public ulong ChannelId { get; init; }
    }
}
