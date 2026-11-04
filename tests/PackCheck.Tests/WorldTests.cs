using System.IO.Compression;
using System.Text;
using PackCheck.Core;
using PackCheck.Servers;
using Xunit;

namespace PackCheck.Tests;

public class WorldTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "pc-world-" + Guid.NewGuid().ToString("N"));

    private static ServerDefinition Server(string dir) => new()
    {
        Id = "s",
        Name = "Srv",
        Directory = dir,
        Platform = ServerPlatform.Paper,
        MinecraftVersion = "1.21.4",
        JarFile = "paper.jar",
        JavaPath = "java",
    };

    private static void MakeWorld(string dir, string name, string marker)
    {
        Directory.CreateDirectory(Path.Combine(dir, name, "region"));
        File.WriteAllText(Path.Combine(dir, name, "level.dat"), "x");
        File.WriteAllText(Path.Combine(dir, name, "marker.txt"), marker);
        File.WriteAllText(Path.Combine(dir, name, "session.lock"), "");
    }

    [Fact]
    public void Library_SavesAndInstallsWorlds()
    {
        var a = Temp();
        var b = Temp();
        MakeWorld(a, "world", "from-a");
        MakeWorld(a, "world_nether", "nether-a");
        MakeWorld(b, "world", "from-b");
        var lib = new WorldLibrary(Temp());

        var saved = lib.Save(Server(a), "Spawn");
        var aside = lib.Install(saved, Server(b));

        Assert.Equal("Spawn", Assert.Single(lib.List()).Name);
        Assert.Equal("from-a", File.ReadAllText(Path.Combine(b, "world", "marker.txt")));
        Assert.True(File.Exists(Path.Combine(b, "world_nether", "marker.txt")));
        Assert.False(File.Exists(Path.Combine(b, "world", "session.lock")));
        Assert.Equal("from-b", File.ReadAllText(Path.Combine(aside!, "world", "marker.txt")));
    }

    [Fact]
    public void Library_ImportsAFlatWorldZipAsWorld()
    {
        var zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("level.dat").Open()))
            {
                w.Write("x");
            }
        }

        var lib = new WorldLibrary(Temp());
        var srv = Temp();
        Directory.CreateDirectory(srv);

        lib.Install(lib.Import(zip, "Imported"), Server(srv));

        Assert.True(File.Exists(Path.Combine(srv, "world", "level.dat")));
    }

    [Fact]
    public void Import_RejectsZipsWithoutAWorld()
    {
        var zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            z.CreateEntry("readme.txt");
        }

        Assert.Throws<InvalidDataException>(() => new WorldLibrary(Temp()).Import(zip, "x"));
    }

    [Fact]
    public void Reset_MovesWorldsAndSetsSeed()
    {
        var dir = Temp();
        MakeWorld(dir, "world", "old");
        File.WriteAllText(Path.Combine(dir, "server.properties"), "level-seed=\nmotd=hi\n");

        var aside = WorldLibrary.ResetWithSeed(Server(dir), "12345");

        Assert.False(Directory.Exists(Path.Combine(dir, "world")));
        Assert.NotNull(aside);
        var props = File.ReadAllText(Path.Combine(dir, "server.properties"));
        Assert.Contains("level-seed=12345", props, StringComparison.Ordinal);
        Assert.Contains("motd=hi", props, StringComparison.Ordinal);
    }

    // ---- NBT ----
    private sealed class NbtWriter
    {
        private readonly MemoryStream _ms = new();

        private void U8(int v) => _ms.WriteByte((byte)v);

        private void I16(int v) { U8(v >> 8); U8(v); }

        private void I32(int v) { I16(v >> 16); I16(v & 0xFFFF); }

        public void Str(string s) { var b = Encoding.UTF8.GetBytes(s); I16(b.Length); _ms.Write(b); }

        public NbtWriter Begin(byte type, string name) { U8(type); Str(name); return this; }

        public NbtWriter Int(string name, int v) { Begin(3, name); I32(v); return this; }

        public NbtWriter Float(string name, float v) { Begin(5, name); I32(BitConverter.SingleToInt32Bits(v)); return this; }

        public NbtWriter Item(int slot, string id, int count, bool oldFormat)
        {
            Begin(1, "Slot"); U8(slot);
            Begin(8, "id"); Str(id);
            if (oldFormat) { Begin(1, "Count"); U8(count); } else { Int("count", count); }
            U8(0);
            return this;
        }

        public NbtWriter ListOfCompounds(string name, int n) { Begin(9, name); U8(10); I32(n); return this; }

        public NbtWriter End() { U8(0); return this; }

        public byte[] Gzip()
        {
            var o = new MemoryStream();
            using (var g = new GZipStream(o, CompressionLevel.Fastest, leaveOpen: true))
            {
                g.Write(_ms.ToArray());
            }

            return o.ToArray();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlayerData_ReadsInventoryAndEnderChest(bool oldFormat)
    {
        var w = new NbtWriter().Begin(10, string.Empty).Float("Health", 18.5f).Int("XpLevel", 7)
            .ListOfCompounds("Inventory", 2).Item(0, "minecraft:diamond_sword", 1, oldFormat).Item(103, "minecraft:iron_helmet", 1, oldFormat)
            .ListOfCompounds("EnderItems", 1).Item(0, "minecraft:diamond", 12, oldFormat)
            .End();

        var inv = PlayerData.FromNbt(Nbt.Read(new MemoryStream(w.Gzip())));

        Assert.Equal(2, inv.Items.Count);
        Assert.Equal("Diamond Sword", inv.Items[0].DisplayName);
        Assert.Equal("hotbar", inv.Items[0].Place);
        Assert.Equal("armor", inv.Items[1].Place);
        Assert.Equal(12, Assert.Single(inv.EnderChest).Count);
        Assert.Equal(18.5, inv.Health!.Value, 3);
        Assert.Equal(7, inv.XpLevel);
    }

    [Fact]
    public void Nbt_RejectsGarbage() =>
        Assert.Throws<InvalidDataException>(() => Nbt.Read(new MemoryStream([1, 2, 3])));

    [Theory]
    [InlineData(ServerPlatform.Paper, "paper")]
    [InlineData(ServerPlatform.Fabric, "fabric")]
    [InlineData(ServerPlatform.NeoForge, "neoforge")]
    [InlineData(ServerPlatform.Vanilla, null)]
    public void BlueMap_LoaderPerPlatform(ServerPlatform p, string? loader) => Assert.Equal(loader, BlueMapSetup.Loader(p));

    [Fact]
    public void BlueMap_AcceptsDownloadAndReadsPort()
    {
        var dir = Temp();
        var s = Server(dir);
        var cfg = BlueMapSetup.ConfigDir(s);
        Directory.CreateDirectory(cfg);
        File.WriteAllText(Path.Combine(cfg, "core.conf"), "# comment\naccept-download: false\n");
        File.WriteAllText(Path.Combine(cfg, "webserver.conf"), "port: 8123\n");

        Assert.True(BlueMapSetup.AcceptDownload(s));
        Assert.Contains("accept-download: true", File.ReadAllText(Path.Combine(cfg, "core.conf")), StringComparison.Ordinal);
        Assert.Equal(8123, BlueMapSetup.Port(s));
    }
}
