using System.Net;
using PackCheck.Providers;
using Xunit;

namespace PackCheck.Tests;

public class CurseForgeKeyTests
{
    private const string Key = "$2a$10$abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0";

    [Theory]
    [InlineData(" " + Key + "\r\n")]
    [InlineData("\"" + Key + "\"")]
    [InlineData("x-api-key: " + Key)]
    [InlineData("​" + Key)]
    public void Pasted_keys_are_cleaned(string pasted) => Assert.Equal(Key, CurseForgeKey.Normalize(pasted));

    [Fact]
    public void Format_check()
    {
        Assert.Equal(60, Key.Length);
        Assert.True(CurseForgeKey.LooksValid(Key));
        Assert.False(CurseForgeKey.LooksValid("abc"));
        Assert.Null(CurseForgeKey.Normalize("   "));
    }

    [Fact]
    public void Header_keeps_dollar_signs()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.curseforge.com/v1/games/432");
        CurseForgeKey.Apply(req, " " + Key + " ");
        Assert.Equal(Key, Assert.Single(req.Headers.GetValues("x-api-key")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "API key rejected (HTTP 403)")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public void Failure_text(HttpStatusCode code, string text) => Assert.Equal(text, CurseForgeKey.Failure(code));
}
