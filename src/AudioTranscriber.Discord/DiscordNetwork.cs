using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetCord.Gateway.Voice.UdpSockets;

namespace AudioTranscriber.Discord;

/// <summary>Discord voice audio arrives over UDP. The bot sends first (so most home routers let the replies back in), but a
/// strict firewall or NAT can drop them. Voice therefore uses one fixed local UDP port, which the app can open in Windows
/// Firewall, map on the router with UPnP, or the user can forward by hand.</summary>
public static class DiscordNetwork
{
    public const int DefaultPort = 50_505;
    public const string RuleName = "AudioTranscriber Discord voice (UDP)";
    public const string UpnpDescription = "AudioTranscriber Discord voice";

    /// <summary>This PC's LAN address used for the internet (no packet is sent).</summary>
    public static IPAddress? LocalAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 53);
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException) { return null; }
    }

    /// <summary>The default gateway (usually the router's admin page) for the LAN address.</summary>
    public static IPAddress? Gateway(IPAddress? local)
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            var properties = adapter.GetIPProperties();
            if (local is not null && !properties.UnicastAddresses.Any(address => address.Address.Equals(local))) continue;
            var gateway = properties.GatewayAddresses.Select(item => item.Address)
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any));
            if (gateway is not null) return gateway;
        }
        return null;
    }

    public static string PortForwardSteps(int port)
    {
        var local = LocalAddress();
        var gateway = Gateway(local);
        return
            $"Only needed when the bot joins but no one is heard (the status says audio isn't arriving) and UPnP didn't work.\n" +
            $"1. Open your router's admin page{(gateway is null ? "" : $": http://{gateway}")} and sign in (the login is often on a sticker on the router).\n" +
            $"2. Find Port Forwarding (also called Virtual Server, NAT or Applications & Gaming).\n" +
            $"3. Add a rule: protocol UDP, external port {port}, internal port {port}, internal IP {local?.ToString() ?? "this PC's IP address (ipconfig shows it)"}.\n" +
            $"4. Save, then in this app choose Leave and join again.\n" +
            "Give this PC a reserved (static) IP in the router's DHCP settings so the rule keeps pointing at it. Behind carrier-grade NAT " +
            "(some mobile and fiber providers) forwarding can't work; use another network or ask your provider for a public IP.";
    }

    /// <summary>Whether Windows Firewall already has this app's inbound UDP rule for <paramref name="port"/> (null: unknown).</summary>
    public static async Task<bool?> FirewallRuleExistsAsync(int port, CancellationToken token = default)
    {
        try
        {
            var start = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{RuleName}\" verbose")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) return false;
            var program = Environment.ProcessPath ?? "";
            return output.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                output.Contains(program, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    /// <summary>Adds (or replaces) an inbound allow rule for this app on UDP <paramref name="port"/>, all network profiles.
    /// Windows asks for administrator approval once (UAC). Returns false when the user declined.</summary>
    public static async Task<bool> AddFirewallRuleAsync(int port, CancellationToken token = default)
    {
        var program = Environment.ProcessPath ?? throw new InvalidOperationException("The app's path is unknown.");
        var arguments = $"/c netsh advfirewall firewall delete rule name=\"{RuleName}\" >nul 2>&1 & " +
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=UDP localport={port} " +
            $"program=\"{program}\" profile=any description=\"Lets Discord voice audio reach AudioTranscriber's bot.\"";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", arguments)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            })!;
            await process.WaitForExitAsync(token);
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223) { return false; }
        return await FirewallRuleExistsAsync(port, token) == true;
    }

    /// <summary>Asks the router (UPnP IGD, through Windows' NATUPnP) to forward UDP <paramref name="port"/> to this PC.
    /// Returns a user-facing result; never throws for an unsupported router.</summary>
    public static Task<UpnpResult> MapPortAsync(int port, CancellationToken token = default) =>
        RunCom(() =>
        {
            var local = LocalAddress() ?? throw new InvalidOperationException("This PC has no network address.");
            dynamic? mappings = Mappings();
            if (mappings is null)
                return new UpnpResult(false, "The router didn't answer UPnP (it may be off on the router, or Windows network discovery is off). " +
                    "Forward the port by hand if audio doesn't arrive.");
            try { mappings.Remove(port, "UDP"); } catch (Exception) { }
            dynamic mapping = mappings.Add(port, "UDP", port, local.ToString(), true, UpnpDescription);
            string? external = null;
            try { external = mapping.ExternalIPAddress as string; } catch (Exception) { }
            return new UpnpResult(true, $"The router forwards UDP {port} to this PC ({local})" +
                (string.IsNullOrEmpty(external) ? "." : $"; its internet address is {external}."));
        }, token);

    public static Task RemovePortMappingAsync(int port) =>
        RunCom(() =>
        {
            dynamic? mappings = Mappings();
            try { mappings?.Remove(port, "UDP"); } catch (Exception) { }
            return new UpnpResult(true, "");
        }, CancellationToken.None);

    private static dynamic? Mappings()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.NATUPnP") ?? throw new InvalidOperationException("Windows UPnP isn't available.");
        dynamic nat = Activator.CreateInstance(type)!;
        return nat.StaticPortMappingCollection;
    }

    // NATUPnP is a COM object that discovers the router synchronously (it can take several seconds), so it runs off the UI.
    private static Task<UpnpResult> RunCom(Func<UpnpResult> work, CancellationToken token)
    {
        var result = new TaskCompletionSource<UpnpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.TrySetResult(work()); }
            catch (Exception error) { result.TrySetResult(new(false, "UPnP failed: " + error.Message)); }
        }) { IsBackground = true, Name = "UPnP" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
    }
}

public sealed record UpnpResult(bool Mapped, string Message);

/// <summary>Voice over one fixed local UDP port, so the firewall rule, UPnP mapping and a manual forward all point at it.
/// When the port is busy (another copy, another app) the connection falls back to any free port.</summary>
public sealed class FixedPortUdpConnectionProvider(int port) : IUdpConnectionProvider
{
    public int Port { get; } = port;
    /// <summary>The local port the last connection actually used.</summary>
    public int BoundPort { get; private set; }

    public IUdpConnection CreateConnection(string ip, ushort port)
    {
        var address = IPAddress.TryParse(ip, out var parsed) ? parsed : Dns.GetHostAddresses(ip).First(item => item.AddressFamily == AddressFamily.InterNetwork);
        return new Connection(this, new IPEndPoint(address, port));
    }

    private sealed class Connection(FixedPortUdpConnectionProvider owner, IPEndPoint remote) : IUdpConnection
    {
        private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        public ValueTask OpenAsync(CancellationToken cancellationToken = default)
        {
            try { socket.Bind(new IPEndPoint(IPAddress.Any, owner.Port)); }
            catch (SocketException) { socket.Bind(new IPEndPoint(IPAddress.Any, 0)); }
            // Don't fail receives when an ICMP "port unreachable" comes back for an earlier send (SIO_UDP_CONNRESET off).
            try { socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null); } catch (SocketException) { }
            owner.BoundPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
            socket.Connect(remote);
            return default;
        }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default) =>
            await socket.SendAsync(datagram, SocketFlags.None, cancellationToken).ConfigureAwait(false);

        public void Send(ReadOnlySpan<byte> datagram) => socket.Send(datagram);

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);

        public void Abort() => socket.Close();
        public void Dispose() => socket.Dispose();
    }
}
