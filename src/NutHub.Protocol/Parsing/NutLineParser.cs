using System.Text;

namespace NutHub.Protocol.Parsing;

/// <summary>What <see cref="NutLineParser.Feed"/> reports after each byte.</summary>
internal enum ParseResult
{
    /// <summary>The request is not complete yet.</summary>
    NeedMore,

    /// <summary>A request ended; take its words with <see cref="NutLineParser.TakeWords"/>.</summary>
    LineComplete,

    /// <summary>The request exceeds the maximum length; the connection should be closed.</summary>
    LineTooLong,
}

/// <summary>
/// Splits the byte stream of a NUT connection into requests and words, byte by byte, exactly like the state machine
/// of NUT's <c>common/parseconf.c</c> (<c>pconf_char</c>) that upsd uses, so that every client that works with upsd
/// is understood the same way:
/// <list type="bullet">
/// <item>Words are separated by unescaped whitespace; a line feed ends the request (a carriage return is just
/// whitespace, so CR LF works).</item>
/// <item>A double quote starts a quoted word only at the start of a word; the closing quote ends it
/// (<c>"one"two</c> is two words). An empty quoted word (<c>""</c>) is a word.</item>
/// <item>A backslash takes the next character literally, inside or outside quotes; a backslash before a line feed,
/// or a line feed inside quotes, continues the request on the next line.</item>
/// <item>An unescaped <c>#</c> outside quotes starts a comment up to the end of the line; an unescaped <c>=</c>
/// outside quotes is a word of its own.</item>
/// <item>Control characters inside words are discarded (CVE-2012-2944 in upsd); words are truncated at 512 bytes
/// and words beyond the 32nd are dropped (the parseconf defaults).</item>
/// </list>
/// One deliberate difference: upsd also discards bytes above 0x7F, while NutHub keeps them and decodes words as
/// UTF-8, so that values such as a UPS id may contain accented letters.
/// </summary>
internal sealed class NutLineParser
{
    /// <summary>PCONF_DEFAULT_ARG_LIMIT.</summary>
    public const int MaxWords = 32;

    /// <summary>PCONF_DEFAULT_WORDLEN_LIMIT.</summary>
    public const int MaxWordBytes = 512;

    private readonly int _maxLineBytes;
    private readonly List<string> _words = new(8);
    private readonly byte[] _word = new byte[MaxWordBytes];
    private int _wordLength;
    private State _state = State.FindWordStart;
    private int _lineBytes;

    /// <param name="maxLineBytes">
    /// The longest request accepted, not counting carriage returns and the final line feed.
    /// </param>
    public NutLineParser(int maxLineBytes = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineBytes, 1);
        _maxLineBytes = maxLineBytes;
    }

    private enum State
    {
        FindWordStart,
        FindEndOfLine,
        QuoteCollect,
        QuoteLiteral,
        Collect,
        CollectLiteral,
    }

    /// <summary>Consumes one byte of the connection.</summary>
    public ParseResult Feed(byte b)
    {
        if (Step(b))
        {
            _lineBytes = 0;
            _state = State.FindWordStart;
            return ParseResult.LineComplete;
        }

        if (b != (byte)'\r' && ++_lineBytes > _maxLineBytes)
        {
            return ParseResult.LineTooLong;
        }

        return ParseResult.NeedMore;
    }

    /// <summary>The words of the request that just completed; clears them for the next request.</summary>
    public string[] TakeWords()
    {
        string[] words = _words.ToArray();
        _words.Clear();
        return words;
    }

    /// <summary>
    /// Parses one request given as text (a line feed is appended). Returns false when the text does not form a
    /// complete request, e.g. an unterminated quote.
    /// </summary>
    public static bool TryParse(string line, out string[] words)
    {
        var parser = new NutLineParser(int.MaxValue);
        foreach (byte b in Encoding.UTF8.GetBytes(line + "\n"))
        {
            if (parser.Feed(b) == ParseResult.LineComplete)
            {
                words = parser.TakeWords();
                return true;
            }
        }

        words = [];
        return false;
    }

    /// <summary>One transition of the state machine; true when the request is complete.</summary>
    private bool Step(byte ch)
    {
        switch (_state)
        {
            case State.FindWordStart:
                return FindWordStart(ch);

            case State.FindEndOfLine:
                return ch == (byte)'\n';

            case State.QuoteCollect:
                if (ch == (byte)'"')
                {
                    EndOfWord();
                    _state = State.FindWordStart;
                }
                else if (ch == (byte)'\\')
                {
                    _state = State.QuoteLiteral;
                }
                else
                {
                    AddChar(ch); // a raw line feed is discarded here: the quote continues on the next line
                }

                return false;

            case State.QuoteLiteral:
                if (ch != (byte)'\n')
                {
                    AddChar(ch);
                }

                _state = State.QuoteCollect;
                return false;

            case State.Collect:
                return Collect(ch);

            case State.CollectLiteral:
                if (ch != (byte)'\n')
                {
                    AddChar(ch);
                }

                _state = State.Collect;
                return false;

            default:
                return false;
        }
    }

    private bool FindWordStart(byte ch)
    {
        if (ch == (byte)'\n')
        {
            return true;
        }

        if (ch == (byte)'#')
        {
            _state = State.FindEndOfLine;
            return false;
        }

        if (IsSpace(ch))
        {
            return false;
        }

        if (ch == (byte)'\\')
        {
            _state = State.CollectLiteral;
            return false;
        }

        if (ch == (byte)'"')
        {
            _state = State.QuoteCollect;
            return false;
        }

        AddChar(ch);
        if (ch == (byte)'=')
        {
            EndOfWord();
            _state = State.FindWordStart;
            return false;
        }

        _state = State.Collect;
        return false;
    }

    private bool Collect(byte ch)
    {
        if (ch == (byte)'#')
        {
            EndOfWord();
            _state = State.FindEndOfLine;
            return false;
        }

        if (ch == (byte)'\n')
        {
            EndOfWord();
            return true;
        }

        if (IsSpace(ch))
        {
            EndOfWord();
            _state = State.FindWordStart;
            return false;
        }

        if (ch == (byte)'=')
        {
            EndOfWord();
            AddChar(ch);
            EndOfWord();
            _state = State.FindWordStart;
            return false;
        }

        if (ch == (byte)'\\')
        {
            _state = State.CollectLiteral;
            return false;
        }

        AddChar(ch);
        return false;
    }

    /// <summary>C isspace() in the "C" locale; the line feed is handled before this is asked.</summary>
    private static bool IsSpace(byte ch) => ch is (byte)' ' or (byte)'\t' or (byte)'\r' or 0x0B or 0x0C;

    private void AddChar(byte ch)
    {
        if (ch < 0x20 || _wordLength >= MaxWordBytes)
        {
            return;
        }

        _word[_wordLength++] = ch;
    }

    private void EndOfWord()
    {
        if (_words.Count < MaxWords)
        {
            _words.Add(Encoding.UTF8.GetString(_word, 0, _wordLength));
        }

        _wordLength = 0;
    }
}
