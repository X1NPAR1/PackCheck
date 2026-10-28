using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace PackCheck.Servers;

/// <summary>Public IP lookup, CGNAT detection helpers and DuckDNS updates.</summary>
public static class PublicAddress
{
    private static readonly Uri[] Sources = [new("https://api.ipify.org"), new("https://checkip.amazonaws.com"), new("https://icanhazip.com")];

    public static async Task<IPAddress?> GetAsync(HttpClient http, CancellationToken ct)
    {
        foreach (var source in Sources)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                var text = (await http.GetStringAsync(source, cts.Token).ConfigureAwait(false)).Trim();
                if (IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }
        }

        return null;
    }

    /// <summary>The LAN address Windows would use to reach the internet (no packet is sent).</summary>
    public static IPAddress? LocalAddress()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 53);
            return (s.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>100.64.0.0/10 (carrier-grade NAT) or private ranges on the WAN side mean port forwarding cannot work.</summary>
    public static bool IsCarrierGradeOrPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>Updates a DuckDNS A record to the current public address. Returns true when DuckDNS answers "OK".</summary>
    public static async Task<bool> UpdateDuckDnsAsync(HttpClient http, string subdomain, string token, CancellationToken ct)
    {
        var domain = subdomain.Replace(".duckdns.org", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        var url = new Uri($"https://www.duckdns.org/update?domains={Uri.EscapeDataString(domain)}&token={Uri.EscapeDataString(token.Trim())}&ip=");
        var answer = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        return answer.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Minimal UPnP Internet Gateway Device client: discover, map, unmap, read the router's external IP.</summary>
public sealed class Upnp(HttpClient http)
{
    private const string Search = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";

    private static readonly string[] Services =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    ];

    private (Uri Control, string Service)? _gateway;

    public async Task<bool> DiscoverAsync(CancellationToken ct)
    {
        if (_gateway is not null)
        {
            return true;
        }

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var data = Encoding.ASCII.GetBytes(Search);
        await udp.SendAsync(data, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), ct).ConfigureAwait(false);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(deadline - DateTime.UtcNow);
            UdpReceiveResult reply;
            try
            {
                reply = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var location = Encoding.ASCII.GetString(reply.Buffer).Split("\r\n")
                .FirstOrDefault(l => l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))?[9..].Trim();
            if (location is null || !Uri.TryCreate(location, UriKind.Absolute, out var loc))
            {
                continue;
            }

            if (await ReadDescriptionAsync(loc, ct).ConfigureAwait(false) is { } gw)
            {
                _gateway = gw;
                return true;
            }
        }

        return false;
    }

    private async Task<(Uri, string)?> ReadDescriptionAsync(Uri location, CancellationToken ct)
    {
        try
        {
            var xml = XDocument.Parse(await http.GetStringAsync(location, ct).ConfigureAwait(false));
            foreach (var service in xml.Descendants().Where(e => e.Name.LocalName == "service"))
            {
                var type = service.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value;
                var control = service.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value;
                if (type is not null && control is not null && Services.Contains(type))
                {
                    return (new Uri(location, control), type);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or TaskCanceledException)
        {
        }

        return null;
    }

    public static string BuildSoap(string service, string action, IEnumerable<(string Name, string Value)> args)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
        sb.Append("<u:").Append(action).Append(" xmlns:u=\"").Append(service).Append("\">");
        foreach (var (n, v) in args)
        {
            sb.Append('<').Append(n).Append('>').Append(System.Security.SecurityElement.Escape(v)).Append("</").Append(n).Append('>');
        }

        sb.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");
        return sb.ToString();
    }

    private async Task<XDocument?> CallAsync(string action, IEnumerable<(string, string)> args, CancellationToken ct)
    {
        if (_gateway is not { } gw)
        {
            return null;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, gw.Control) { Content = new StringContent(BuildSoap(gw.Service, action, args), Encoding.UTF8, "text/xml") };
        req.Headers.Add("SOAPAction", $"\"{gw.Service}#{action}\"");
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return res.IsSuccessStatusCode ? XDocument.Parse(body) : null;
    }

    public async Task<IPAddress?> ExternalAddressAsync(CancellationToken ct)
    {
        var doc = await CallAsync("GetExternalIPAddress", [], ct).ConfigureAwait(false);
        var text = doc?.Descendants().FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value;
        return IPAddress.TryParse(text, out var ip) ? ip : null;
    }

    public async Task<bool> MapAsync(int port, string protocol, IPAddress local, string description, CancellationToken ct) =>
        await CallAsync("AddPortMapping",
        [
            ("NewRemoteHost", string.Empty), ("NewExternalPort", port.ToString(CultureInfo.InvariantCulture)), ("NewProtocol", protocol),
            ("NewInternalPort", port.ToString(CultureInfo.InvariantCulture)), ("NewInternalClient", local.ToString()), ("NewEnabled", "1"),
            ("NewPortMappingDescription", description), ("NewLeaseDuration", "0"),
        ], ct).ConfigureAwait(false) is not null;

    public async Task<bool> UnmapAsync(int port, string protocol, CancellationToken ct) =>
        await CallAsync("DeletePortMapping",
        [
            ("NewRemoteHost", string.Empty), ("NewExternalPort", port.ToString(CultureInfo.InvariantCulture)), ("NewProtocol", protocol),
        ], ct).ConfigureAwait(false) is not null;
}

/// <summary>Windows Defender Firewall rules for a server port. Changes need administrator rights.</summary>
public static class Firewall
{
    public static string RuleName(int port) => $"PackCheck Minecraft {port}";

    /// <summary>Reading rules does not need elevation.</summary>
    public static bool RuleExists(int port)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "advfirewall", "firewall", "show", "rule", $"name={RuleName(port)}" })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    /// <summary>netsh argument lists for adding (TCP + UDP) or deleting the rules of a port.</summary>
    public static IReadOnlyList<string[]> Commands(int port, bool add)
    {
        var name = $"name={RuleName(port)}";
        if (!add)
        {
            return [["advfirewall", "firewall", "delete", "rule", name]];
        }

        return
        [
            ["advfirewall", "firewall", "add", "rule", name, "dir=in", "action=allow", "protocol=TCP", $"localport={port}", "profile=any"],
            ["advfirewall", "firewall", "add", "rule", name, "dir=in", "action=allow", "protocol=UDP", $"localport={port}", "profile=any"],
        ];
    }

    /// <summary>Runs inside the elevated helper instance ("PackCheck.exe --firewall add|remove port").</summary>
    public static int RunElevatedCommands(int port, bool add)
    {
        if (!add || RuleExists(port))
        {
            foreach (var cmd in Commands(port, add: false))
            {
                Run(cmd);
            }
        }

        if (!add)
        {
            return 0;
        }

        var code = 0;
        foreach (var cmd in Commands(port, add: true))
        {
            code |= Run(cmd);
        }

        return code;
    }

    private static int Run(string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>Asks for administrator rights (UAC) and runs this app as a short-lived firewall helper.</summary>
    public static async Task<bool> ChangeElevatedAsync(int port, bool add)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"--firewall {(add ? "add" : "remove")} {port.ToString(CultureInfo.InvariantCulture)}",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync().ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user declined the UAC prompt.
            return false;
        }
    }
}
