using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PackCheck.Servers;

public sealed record PingResult(int Online, int Max, IReadOnlyList<string> Sample, string Version, int LatencyMs);

/// <summary>
/// Minecraft "Server List Ping" (handshake + status request, protocol used by the multiplayer screen).
/// Works for every Java server platform and needs no plugin.
/// </summary>
public static class ServerListPing
{
    public static async Task<PingResult?> QueryAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();

            var handshake = new List<byte>();
            WriteVarInt(handshake, 0x00);
            WriteVarInt(handshake, -1);
            var hostBytes = Encoding.UTF8.GetBytes(host);
            WriteVarInt(handshake, hostBytes.Length);
            handshake.AddRange(hostBytes);
            handshake.Add((byte)(port >> 8));
            handshake.Add((byte)port);
            WriteVarInt(handshake, 1);
            await SendPacketAsync(stream, handshake, cts.Token).ConfigureAwait(false);
            await SendPacketAsync(stream, [0x00], cts.Token).ConfigureAwait(false);

            _ = await ReadVarIntAsync(stream, cts.Token).ConfigureAwait(false);
            _ = await ReadVarIntAsync(stream, cts.Token).ConfigureAwait(false);
            var jsonLength = await ReadVarIntAsync(stream, cts.Token).ConfigureAwait(false);
            if (jsonLength is <= 0 or > 1_000_000)
            {
                return null;
            }

            var json = new byte[jsonLength];
            await stream.ReadExactlyAsync(json, cts.Token).ConfigureAwait(false);

            // Ping/pong measures round-trip latency.
            var sw = Stopwatch.StartNew();
            var ping = new List<byte> { 0x01 };
            var payload = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(payload, Environment.TickCount64);
            ping.AddRange(payload);
            await SendPacketAsync(stream, ping, cts.Token).ConfigureAwait(false);
            _ = await ReadVarIntAsync(stream, cts.Token).ConfigureAwait(false);
            var pong = new byte[9];
            await stream.ReadExactlyAsync(pong, cts.Token).ConfigureAwait(false);
            var latency = (int)sw.ElapsedMilliseconds;

            return Parse(Encoding.UTF8.GetString(json), latency);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or JsonException or EndOfStreamException)
        {
            return null;
        }
    }

    public static PingResult? Parse(string json, int latency)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("players", out var players))
        {
            return null;
        }

        var sample = players.TryGetProperty("sample", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Select(p => p.GetProperty("name").GetString() ?? "?").ToList()
            : [];
        var version = root.TryGetProperty("version", out var v) && v.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        return new PingResult(players.GetProperty("online").GetInt32(), players.GetProperty("max").GetInt32(), sample, version, latency);
    }

    public static void WriteVarInt(List<byte> buffer, int value)
    {
        var v = (uint)value;
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0)
            {
                b |= 0x80;
            }

            buffer.Add(b);
        }
        while (v != 0);
    }

    private static async Task SendPacketAsync(NetworkStream stream, List<byte> body, CancellationToken ct)
    {
        var packet = new List<byte>();
        WriteVarInt(packet, body.Count);
        packet.AddRange(body);
        await stream.WriteAsync(packet.ToArray(), ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadVarIntAsync(NetworkStream stream, CancellationToken ct)
    {
        var result = 0;
        var buf = new byte[1];
        for (var shift = 0; shift < 35; shift += 7)
        {
            await stream.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
            result |= (buf[0] & 0x7F) << shift;
            if ((buf[0] & 0x80) == 0)
            {
                return result;
            }
        }

        throw new IOException("VarInt too long");
    }
}

public enum LogLevel
{
    Info,
    Warn,
    Error,
    Command,
    Plain,
}

public sealed record LogLine(string? Time, string? Source, LogLevel Level, string Message, string Raw);

/// <summary>Splits "[21:30:16] [Server thread/INFO]: message" (and Forge/Paper variants) into parts.</summary>
public static partial class LogParser
{
    [GeneratedRegex(@"^\[(?<time>\d{2}:\d{2}:\d{2})(?:\.\d+)?\]\s*\[(?<src>[^\]/]*)/(?<lvl>[A-Z]+)\](?:\s*\[[^\]]*\])?:?\s?(?<msg>.*)$")]
    private static partial Regex Standard();

    [GeneratedRegex(@"^\[(?<time>\d{2}:\d{2}:\d{2}) (?<lvl>[A-Z]+)\]:?\s?(?<msg>.*)$")]
    private static partial Regex Short();

    public static LogLine Parse(string raw)
    {
        var m = Standard().Match(raw);
        if (!m.Success)
        {
            m = Short().Match(raw);
        }

        if (!m.Success)
        {
            var guess = raw.Contains("Exception", StringComparison.Ordinal) || raw.TrimStart().StartsWith("at ", StringComparison.Ordinal) ? LogLevel.Error
                : raw.StartsWith("WARNING", StringComparison.Ordinal) ? LogLevel.Warn
                : LogLevel.Plain;
            return new LogLine(null, null, guess, raw, raw);
        }

        var level = m.Groups["lvl"].Value switch
        {
            "WARN" or "WARNING" => LogLevel.Warn,
            "ERROR" or "SEVERE" or "FATAL" => LogLevel.Error,
            _ => LogLevel.Info,
        };
        return new LogLine(m.Groups["time"].Value, m.Groups["src"].Success ? m.Groups["src"].Value : null, level, m.Groups["msg"].Value, raw);
    }
}

/// <summary>Reads TPS from the output of the platform's own tps command.</summary>
public static partial class TpsParser
{
    // Paper/Spigot: "TPS from last 1m, 5m, 15m: 20.0, 20.0, 20.0" (may contain color codes / '*').
    [GeneratedRegex(@"TPS from last 1m, 5m, 15m:\s*\*?(?<v>[0-9]+(?:[.,][0-9]+)?)")]
    private static partial Regex Paper();

    // Forge/NeoForge: "Overall: Mean tick time: 2.345 ms. Mean TPS: 20.000"
    [GeneratedRegex(@"Overall\s*:.*Mean TPS:\s*(?<v>[0-9]+(?:[.,][0-9]+)?)")]
    private static partial Regex Forge();

    // spark: "TPS from last 5s, 10s, 1m, 5m, 15m:" followed by "20.0, 20.0, ..."
    [GeneratedRegex(@"^\s*\*?(?<v>[0-9]+(?:\.[0-9]+)?)\*?,\s*\*?[0-9]+(?:\.[0-9]+)?\*?,")]
    private static partial Regex Spark();

    [GeneratedRegex(@"§.")]
    private static partial Regex ColorCodes();

    public static double? TryParse(string line)
    {
        var clean = ColorCodes().Replace(line, string.Empty);
        foreach (var r in new[] { Paper(), Forge(), Spark() })
        {
            var m = r.Match(clean);
            if (m.Success && double.TryParse(m.Groups["v"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var tps))
            {
                return Math.Min(tps, 20);
            }
        }

        return null;
    }

    /// <summary>The command that prints TPS on this platform, or null when the platform has none.</summary>
    public static string? CommandFor(ServerPlatform p, bool hasSpark) => p switch
    {
        ServerPlatform.Paper or ServerPlatform.Purpur or ServerPlatform.Folia or ServerPlatform.Leaf or ServerPlatform.Leaves
            or ServerPlatform.Spigot or ServerPlatform.CraftBukkit or ServerPlatform.Mohist or ServerPlatform.Youer or ServerPlatform.Arclight => "tps",
        ServerPlatform.Forge => "forge tps",
        ServerPlatform.NeoForge => "neoforge tps",
        _ when hasSpark => "spark tps",
        _ => null,
    };
}

/// <summary>Health thresholds shared by the gauges. 0 = excellent … 4 = critical.</summary>
public static class Health
{
    public static int Tps(double v) => v >= 19.5 ? 0 : v >= 18 ? 1 : v >= 15 ? 2 : v >= 10 ? 3 : 4;

    public static int Percent(double cpu, double excellent, double good, double medium, double bad) =>
        cpu <= excellent ? 0 : cpu <= good ? 1 : cpu <= medium ? 2 : cpu <= bad ? 3 : 4;

    public static int Cpu(double v) => Percent(v, 40, 60, 75, 90);

    public static int Ram(double v) => Percent(v, 50, 65, 80, 90);

    public static int Ping(double ms) => Percent(ms, 30, 60, 100, 200);
}
