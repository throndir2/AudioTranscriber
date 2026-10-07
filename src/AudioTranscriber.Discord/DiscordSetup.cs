using System.Globalization;

namespace AudioTranscriber.Discord;

/// <summary>Links and checks for setting up the user's own Discord application. Discord has no API that creates an
/// application, so the user creates it in the Developer Portal; the app then builds the invite link from its ID.</summary>
public static class DiscordSetup
{
    public const string PortalUrl = "https://discord.com/developers/applications";
    public const string DocsUrl = "https://discord.com/developers/docs/quick-start/getting-started";

    /// <summary>View Channel (1 &lt;&lt; 10) and Connect (1 &lt;&lt; 20): enough to see voice channels and join them to listen.
    /// The bot never speaks, reads messages or needs privileged intents.</summary>
    public const ulong Permissions = (1UL << 10) | (1UL << 20);

    public static string BotPage(ulong applicationId) =>
        $"{PortalUrl}/{applicationId.ToString(CultureInfo.InvariantCulture)}/bot";

    /// <summary>Adds the bot to a server the user manages (guild install).</summary>
    public static string InviteUrl(ulong applicationId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(applicationId);
        return "https://discord.com/oauth2/authorize?client_id=" + applicationId.ToString(CultureInfo.InvariantCulture) +
            "&scope=bot&permissions=" + Permissions.ToString(CultureInfo.InvariantCulture) + "&integration_type=0";
    }

    /// <summary>The application ID is the first part of a bot token (base64 of the bot's user ID), or 0 when it isn't one.</summary>
    public static ulong ApplicationIdFromToken(ReadOnlySpan<char> token)
    {
        var dot = token.IndexOf('.');
        if (dot <= 0) return 0;
        var part = token[..dot].ToString().Replace('-', '+').Replace('_', '/');
        part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
        try
        {
            var text = System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(part));
            return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
        }
        catch (FormatException) { return 0; }
    }

    public static bool LooksLikeToken(string? token) =>
        token is { Length: > 50 } && token.Count(c => c == '.') == 2 && ApplicationIdFromToken(token.Trim()) != 0;

    public const string Steps =
        "1. Open the Discord Developer Portal and choose New Application. Name it, for example \"Transcriber\", and accept the terms.\n" +
        "2. Open Bot. Choose Reset Token, copy the token and paste it below, then choose Save and connect. The token is encrypted for " +
        "your user account and never leaves this PC except to sign in to Discord.\n" +
        "3. Still on Bot, you may turn off Public Bot so only you can add it to servers. No privileged intents are needed.\n" +
        "4. Choose Add the bot to a server: Discord opens the invite page with the right permissions (View Channels and Connect). " +
        "Pick a server you manage and authorize it.\n" +
        "5. Back here, pick the server and voice channel and choose Join and record. Everyone in the channel sees the bot join, " +
        "so people know they are being recorded; ask for their consent first.";
}
