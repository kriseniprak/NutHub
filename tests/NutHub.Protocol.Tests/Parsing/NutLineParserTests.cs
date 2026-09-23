using System.Text;
using NutHub.Protocol.Parsing;

namespace NutHub.Protocol.Tests.Parsing;

public sealed class NutLineParserTests
{
    [Theory]
    [InlineData("GET VAR ups1 ups.status", new[] { "GET", "VAR", "ups1", "ups.status" })]
    [InlineData("  LIST   UPS  ", new[] { "LIST", "UPS" })]
    [InlineData("LIST\tVAR\tups1", new[] { "LIST", "VAR", "ups1" })]
    [InlineData("VER\r", new[] { "VER" })]
    [InlineData("SET VAR ups1 ups.id \"My UPS\"", new[] { "SET", "VAR", "ups1", "ups.id", "My UPS" })]
    [InlineData("\"one\"two", new[] { "one", "two" })]
    [InlineData("\"\"", new[] { "" })]
    [InlineData("a \"\" b", new[] { "a", "", "b" })]
    [InlineData("\"say \\\"hi\\\"\"", new[] { "say \"hi\"" })]
    [InlineData("\"back\\\\slash\"", new[] { "back\\slash" })]
    [InlineData("one\\ two", new[] { "one two" })]
    [InlineData("\\n", new[] { "n" })]
    [InlineData("ab\"cd", new[] { "ab\"cd" })]
    [InlineData("it's fine", new[] { "it's", "fine" })]
    [InlineData("\"Outlet #3\"", new[] { "Outlet #3" })]
    [InlineData("\"Outlet \\#3\"", new[] { "Outlet #3" })]
    [InlineData("word # comment here", new[] { "word" })]
    [InlineData("word#comment", new[] { "word" })]
    [InlineData("# only a comment", new string[0])]
    [InlineData("a=b", new[] { "a", "=", "b" })]
    [InlineData("= x", new[] { "=", "x" })]
    [InlineData("\"a=b\"", new[] { "a=b" })]
    [InlineData("a\\=b", new[] { "a=b" })]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    public void Splits_words_like_parseconf(string line, string[] expected)
    {
        Assert.True(NutLineParser.TryParse(line, out string[] words));
        Assert.Equal(expected, words);
    }

    [Fact]
    public void Line_feed_ends_the_request_and_the_next_one_starts_clean()
    {
        var parser = new NutLineParser();
        var requests = new List<string[]>();
        foreach (byte b in Encoding.ASCII.GetBytes("VER\r\nGET VAR a b\nLIST UPS\n"))
        {
            if (parser.Feed(b) == ParseResult.LineComplete)
            {
                requests.Add(parser.TakeWords());
            }
        }

        Assert.Equal(3, requests.Count);
        Assert.Equal(["VER"], requests[0]);
        Assert.Equal(["GET", "VAR", "a", "b"], requests[1]);
        Assert.Equal(["LIST", "UPS"], requests[2]);
    }

    [Fact]
    public void Quote_continues_across_a_line_feed()
    {
        // A raw line feed inside quotes is dropped (it is a control character) and the request goes on.
        Assert.False(NutLineParser.TryParse("SET VAR u v \"abc", out _));

        var parser = new NutLineParser();
        string[]? words = null;
        foreach (byte b in Encoding.ASCII.GetBytes("SET VAR u v \"ab\ncd\"\n"))
        {
            if (parser.Feed(b) == ParseResult.LineComplete)
            {
                words = parser.TakeWords();
            }
        }

        Assert.NotNull(words);
        Assert.Equal(["SET", "VAR", "u", "v", "abcd"], words);
    }

    [Fact]
    public void Backslash_before_line_feed_continues_the_request()
    {
        var parser = new NutLineParser();
        string[]? words = null;
        foreach (byte b in Encoding.ASCII.GetBytes("GET VAR ups1 \\\nups.status\n"))
        {
            if (parser.Feed(b) == ParseResult.LineComplete)
            {
                Assert.Null(words);
                words = parser.TakeWords();
            }
        }

        // "\<LF>" is dropped and the request goes on with the next physical line, as in parseconf.
        Assert.NotNull(words);
        Assert.Equal(["GET", "VAR", "ups1", "ups.status"], words);
    }

    [Fact]
    public void Control_characters_are_discarded_inside_words()
    {
        Assert.True(NutLineParser.TryParse("GE\u0001T VAR \"a\u0007b\"", out string[] words));
        Assert.Equal(["GET", "VAR", "ab"], words);
    }

    [Fact]
    public void A_control_character_alone_makes_an_empty_word_like_upsd()
    {
        Assert.True(NutLineParser.TryParse("\u0001 VER", out string[] words));
        Assert.Equal(["", "VER"], words);
    }

    [Fact]
    public void Utf8_is_kept()
    {
        Assert.True(NutLineParser.TryParse("SET VAR ups1 ups.id \"Salle serveur n°2 é\"", out string[] words));
        Assert.Equal("Salle serveur n°2 é", words[4]);
    }

    [Fact]
    public void Words_are_truncated_at_512_bytes()
    {
        string longWord = new('x', 600);
        Assert.True(NutLineParser.TryParse("A " + longWord + " B", out string[] words));
        Assert.Equal(3, words.Length);
        Assert.Equal(NutLineParser.MaxWordBytes, words[1].Length);
        Assert.Equal("B", words[2]);
    }

    [Fact]
    public void Words_beyond_the_32nd_are_dropped()
    {
        string line = string.Join(' ', Enumerable.Range(1, 40).Select(i => "w" + i));
        Assert.True(NutLineParser.TryParse(line, out string[] words));
        Assert.Equal(NutLineParser.MaxWords, words.Length);
        Assert.Equal("w32", words[^1]);
    }

    [Fact]
    public void Request_longer_than_the_limit_is_reported()
    {
        var parser = new NutLineParser(maxLineBytes: 10);
        ParseResult last = ParseResult.NeedMore;
        int fed = 0;
        foreach (byte b in Encoding.ASCII.GetBytes("0123456789ABC"))
        {
            fed++;
            last = parser.Feed(b);
            if (last != ParseResult.NeedMore)
            {
                break;
            }
        }

        Assert.Equal(ParseResult.LineTooLong, last);
        Assert.Equal(11, fed);
    }

    [Fact]
    public void Request_of_exactly_the_limit_is_accepted_with_crlf()
    {
        var parser = new NutLineParser(maxLineBytes: 10);
        ParseResult last = ParseResult.NeedMore;
        foreach (byte b in Encoding.ASCII.GetBytes("0123456789\r\n"))
        {
            last = parser.Feed(b);
            Assert.NotEqual(ParseResult.LineTooLong, last);
        }

        Assert.Equal(ParseResult.LineComplete, last);
        Assert.Equal(["0123456789"], parser.TakeWords());
    }

    [Fact]
    public void The_limit_counts_each_request_separately()
    {
        var parser = new NutLineParser(maxLineBytes: 10);
        int complete = 0;
        foreach (byte b in Encoding.ASCII.GetBytes("012345678\n012345678\n012345678\n"))
        {
            ParseResult r = parser.Feed(b);
            Assert.NotEqual(ParseResult.LineTooLong, r);
            if (r == ParseResult.LineComplete)
            {
                complete++;
                parser.TakeWords();
            }
        }

        Assert.Equal(3, complete);
    }
}
