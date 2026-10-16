using Flextime;

namespace Test.Flextime;

public class TerminalTextTests
{
    [Fact]
    public void KeepsPlainText()
    {
        const string text = "Martins MacBook Pro (åäö) – 7.13";

        Assert.Same(text, TerminalText.Clean(text));
    }

    [Fact]
    public void KeepsNewlineAndTab()
    {
        Assert.Equal("a\tb\nc", TerminalText.Clean("a\tb\nc"));
    }

    [Fact]
    public void NullIsEmpty()
    {
        Assert.Equal(string.Empty, TerminalText.Clean(null));
    }

    [Theory]
    [InlineData("\u001b[31mred\u001b[0m", "[31mred[0m")]
    [InlineData("\u001b]0;title\u0007name", "]0;titlename")]
    [InlineData("over\rwrite", "overwrite")]
    [InlineData("back\bspace", "backspace")]
    [InlineData("nul\u0000l", "null")]
    [InlineData("del\u007fete", "delete")]
    [InlineData("c1\u009b31m", "c131m")]
    [InlineData("osc\u009d0;x\u009c", "osc0;x")]
    public void DropsControlCharacters(string text, string expected)
    {
        Assert.Equal(expected, TerminalText.Clean(text));
    }

    [Theory]
    [InlineData("‮exe.txt", "exe.txt")]
    [InlineData("a‪b‫c‬d‭e", "abcde")]
    [InlineData("a⁦b⁧c⁨d⁩e", "abcde")]
    public void DropsBidiOverrides(string text, string expected)
    {
        Assert.Equal(expected, TerminalText.Clean(text));
    }
}
