using System.Security.Cryptography;

namespace PackCheck.Core;

public static class Hashing
{
    public static async Task<string> Sha256FileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static bool EqualsHex(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(a.Trim()), Convert.FromHexString(b.Trim()));
}
