using System.Globalization;

namespace PackCheck.Core;

/// <summary>Numeric Minecraft release version such as 1.21.4 (snapshots are not comparable and rejected).</summary>
public readonly record struct MinecraftVersion(int Major, int Minor, int Patch) : IComparable<MinecraftVersion>
{
    public static bool TryParse(string? text, out MinecraftVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split('.');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        var nums = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i]))
            {
                return false;
            }
        }

        version = new MinecraftVersion(nums[0], nums[1], nums[2]);
        return true;
    }

    public int CompareTo(MinecraftVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }

        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(MinecraftVersion a, MinecraftVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(MinecraftVersion a, MinecraftVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(MinecraftVersion a, MinecraftVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(MinecraftVersion a, MinecraftVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => Patch == 0 && Major == 1 && Minor < 20 ? $"{Major}.{Minor}" : $"{Major}.{Minor}.{Patch}";

    /// <summary>Minimum Java feature version required to run a given Minecraft server version.</summary>
    public int RequiredJava() => this switch
    {
        _ when this >= new MinecraftVersion(26, 0, 0) => 25,
        _ when this >= new MinecraftVersion(1, 20, 5) => 21,
        _ when this >= new MinecraftVersion(1, 18, 0) => 17,
        _ when this >= new MinecraftVersion(1, 17, 0) => 16,
        _ => 8,
    };
}
