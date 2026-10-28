using System.Net;
using PackCheck.Servers;
using Xunit.Abstractions;

namespace PackCheck.Tests;

public class NetworkingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("85.105.10.20", false)]
    public void DetectsCgnatAndPrivateWan(string ip, bool expected) =>
        Assert.Equal(expected, PublicAddress.IsCarrierGradeOrPrivate(IPAddress.Parse(ip)));

    [Fact]
    public void SoapEnvelope_EscapesValues()
    {
        var xml = Upnp.BuildSoap("urn:schemas-upnp-org:service:WANIPConnection:1", "AddPortMapping", [("NewPortMappingDescription", "a<b&c")]);
        Assert.Contains("<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">", xml, StringComparison.Ordinal);
        Assert.Contains("a&lt;b&amp;c", xml, StringComparison.Ordinal);
        System.Xml.Linq.XDocument.Parse(xml);
    }

    [Fact]
    public void FirewallCommands_AreArgumentLists_ForTcpAndUdp()
    {
        var add = Firewall.Commands(25565, add: true);
        Assert.Equal(2, add.Count);
        Assert.Contains(add, c => c.Contains("protocol=TCP") && c.Contains("localport=25565"));
        Assert.Contains(add, c => c.Contains("protocol=UDP"));
        Assert.All(add, c => Assert.Contains("name=PackCheck Minecraft 25565", c));
        Assert.Single(Firewall.Commands(25565, add: false));
    }

    [Fact]
    public void ReadingFirewallRules_NeedsNoElevation() => Assert.False(Firewall.RuleExists(65001));

    /// <summary>Read-only: asks the router for its WAN address. Never maps anything.</summary>
    [Fact]
    [Trait("Category", "Network")]
    public async Task UpnpDiscovery_ReadOnly()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var upnp = new Upnp(http);
        var found = await upnp.DiscoverAsync(CancellationToken.None);
        var wan = found ? await upnp.ExternalAddressAsync(CancellationToken.None) : null;
        var pub = await PublicAddress.GetAsync(http, CancellationToken.None);
        output.WriteLine($"UPnP gateway: {found}, router WAN: {wan?.ToString() ?? "-"}, public IP known: {pub is not null}, local: {PublicAddress.LocalAddress()}");
        Assert.NotNull(PublicAddress.LocalAddress());
    }
}
