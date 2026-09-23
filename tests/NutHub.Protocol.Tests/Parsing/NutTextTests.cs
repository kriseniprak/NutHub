using NutHub.Protocol.Parsing;

namespace NutHub.Protocol.Tests.Parsing;

public sealed class NutTextTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("say \"hi\"", "say \\\"hi\\\"")]
    [InlineData("C:\\path", "C:\\\\path")]
    [InlineData("Outlet #3", "Outlet \\#3")]
    [InlineData("two\nlines", "two lines")]
    [InlineData("", "")]
    public void Escapes_like_pconf_encode(string value, string expected)
    {
        Assert.Equal(expected, NutText.Escape(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("simple")]
    [InlineData("with space")]
    [InlineData("quote \" inside")]
    [InlineData("back\\slash")]
    [InlineData("\\\"#\\#\"\\")]
    [InlineData("trailing backslash\\")]
    [InlineData("Rack \"A\" #1 \\ main")]
    [InlineData("a=b # c")]
    [InlineData("accents é ü")]
    public void Quoted_values_parse_back_to_the_original(string value)
    {
        string line = "VAR ups1 x " + NutText.Quote(value);
        Assert.True(NutLineParser.TryParse(line, out string[] words));
        Assert.Equal(["VAR", "ups1", "x", value], words);
    }

    [Fact]
    public void Random_values_round_trip()
    {
        var random = new Random(3493);
        const string alphabet = "ab \"\\#=\t'xyz0123.-_";
        for (int i = 0; i < 500; i++)
        {
            string value = new(Enumerable.Range(0, random.Next(0, 40)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            string expected = value.Replace('\t', ' ');
            Assert.True(NutLineParser.TryParse("X " + NutText.Quote(value), out string[] words));
            Assert.Equal(["X", expected], words);
        }
    }

    [Theory]
    [InlineData("ver", "VER")]
    [InlineData("Get", "GET")]
    [InlineData("LIST", "LIST")]
    [InlineData("\u017Fet", "\u017FET")] // only ASCII letters fold, like strcasecmp
    public void Upper_cases_ascii_only(string word, string expected)
    {
        Assert.Equal(expected, NutText.AsciiUpper(word));
    }

    [Fact]
    public void Orders_names_like_strcasecmp()
    {
        string[] names = ["input.L2.voltage", "input.frequency", "ups.status", "ups_x", "ups.Model", "battery.charge"];
        string[] sorted = names.Order(NutNameComparer.Instance).ToArray();
        Assert.Equal(["battery.charge", "input.frequency", "input.L2.voltage", "ups.Model", "ups.status", "ups_x"], sorted);
    }

    [Theory]
    [InlineData("server.info", "SERVER.", true)]
    [InlineData("Server.Version", "server.", true)]
    [InlineData("serve", "server.", false)]
    public void Prefix_ignores_ascii_case(string text, string prefix, bool expected)
    {
        Assert.Equal(expected, NutText.AsciiStartsWithIgnoreCase(text, prefix));
    }
}
