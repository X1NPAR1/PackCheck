using System.Net;
using System.Net.Http.Headers;

namespace PackCheck.Providers;

public enum CurseForgeKeyStatus
{
    Valid,
    Rejected,
    Missing,
    Unreachable,
}

/// <summary>Cleans and checks a CurseForge API key (console.curseforge.com → API keys).</summary>
public static class CurseForgeKey
{
    /// <summary>Removes what often comes along when a key is pasted: spaces, line breaks, quotes, invisible characters, a "x-api-key:" prefix.</summary>
    public static string? Normalize(string? key)
    {
        if (key is null)
        {
            return null;
        }

        var k = new string([.. key.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c is not '\u200B' and not '\u200C' and not '\u200D' and not '\uFEFF')]);
        if (k.StartsWith("x-api-key:", StringComparison.OrdinalIgnoreCase))
        {
            k = k["x-api-key:".Length..];
        }

        k = k.Trim('"', '\'', '`');
        return k.Length == 0 ? null : k;
    }

    /// <summary>CurseForge keys look like "$2a$10$" followed by 53 characters (60 in total).</summary>
    public static bool LooksValid(string? key) => key is { Length: 60 } && key.StartsWith("$2a$", StringComparison.Ordinal);

    /// <summary>Adds the key header without .NET's header validation ("$" and "/" are fine in the key).</summary>
    public static void Apply(HttpRequestMessage request, string? key)
    {
        request.Headers.TryAddWithoutValidation("x-api-key", Normalize(key));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Text for a failed request; 401/403 always mean the key was refused.</summary>
    public static string Failure(HttpStatusCode code) =>
        code is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? $"API key rejected (HTTP {(int)code})" : $"HTTP {(int)code}";

    /// <summary>One cheap request (the Minecraft game entry) to see whether CurseForge accepts the key.</summary>
    public static async Task<(CurseForgeKeyStatus Status, string? Detail)> CheckAsync(HttpClient http, string? key, CancellationToken ct)
    {
        if (Normalize(key) is null)
        {
            return (CurseForgeKeyStatus.Missing, null);
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.curseforge.com/v1/games/432"));
            Apply(req, key);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var res = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            return res.IsSuccessStatusCode ? (CurseForgeKeyStatus.Valid, null)
                : res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? (CurseForgeKeyStatus.Rejected, $"HTTP {(int)res.StatusCode}")
                : (CurseForgeKeyStatus.Unreachable, $"HTTP {(int)res.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (CurseForgeKeyStatus.Unreachable, ex.Message);
        }
    }
}
