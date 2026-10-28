using PackCheck.Providers;

namespace PackCheck.Tests;

public class PlainTextTests
{
    [Fact]
    public void Markdown_IsReadable()
    {
        var text = PlainText.From("# Title\n\n**Bold** and [a link](https://x.y)\n![img](https://x.y/a.png)\n\n\n\nEnd");
        Assert.Equal("Title\n\nBold and a link\n\nEnd", text);
    }

    [Fact]
    public void Html_IsReadable()
    {
        Assert.Equal("One\nTwo &", PlainText.From("<p>One</p><p><b>Two</b> &amp;</p>"));
    }
}
