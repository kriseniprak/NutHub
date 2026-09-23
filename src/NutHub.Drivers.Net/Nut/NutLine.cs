using System.Text;

namespace NutHub.Drivers.Net.Nut;

/// <summary>
/// Splitting and quoting of NUT protocol lines. upsd formats its answers and parses its requests with parseconf,
/// so this follows the same rules (state machine of NUT common/parseconf.c): words are separated by white space,
/// double quotes group a word that may contain spaces, a backslash takes the next character literally (inside
/// and outside quotes), '#' outside quotes starts a comment, and a '=' outside quotes is a word of its own.
/// </summary>
internal static class NutLine
{
    /// <summary>Characters that pconf_encode() escapes with a backslash (PCONF_ESCAPE in parseconf.c).</summary>
    private const string EscapedCharacters = "#\\\"";

    private enum State
    {
        FindWordStart,
        Collect,
        CollectLiteral,
        QuoteCollect,
        QuoteLiteral,
        Comment,
    }

    /// <summary>Splits one line (without its terminator) into words.</summary>
    public static List<string> Split(string line)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        State state = State.FindWordStart;

        void EndWord()
        {
            words.Add(word.ToString());
            word.Clear();
        }

        foreach (char c in line)
        {
            switch (state)
            {
                case State.FindWordStart:
                    if (c == '#')
                    {
                        state = State.Comment;
                    }
                    else if (char.IsWhiteSpace(c))
                    {
                        // Still between words.
                    }
                    else if (c == '\\')
                    {
                        state = State.CollectLiteral;
                    }
                    else if (c == '"')
                    {
                        state = State.QuoteCollect;
                    }
                    else if (c == '=')
                    {
                        word.Append(c);
                        EndWord();
                    }
                    else
                    {
                        word.Append(c);
                        state = State.Collect;
                    }

                    break;

                case State.Collect:
                    if (c == '#')
                    {
                        EndWord();
                        state = State.Comment;
                    }
                    else if (char.IsWhiteSpace(c))
                    {
                        EndWord();
                        state = State.FindWordStart;
                    }
                    else if (c == '=')
                    {
                        EndWord();
                        word.Append(c);
                        EndWord();
                        state = State.FindWordStart;
                    }
                    else if (c == '\\')
                    {
                        state = State.CollectLiteral;
                    }
                    else
                    {
                        word.Append(c);
                    }

                    break;

                case State.CollectLiteral:
                    word.Append(c);
                    state = State.Collect;
                    break;

                case State.QuoteCollect:
                    if (c == '"')
                    {
                        EndWord();
                        state = State.FindWordStart;
                    }
                    else if (c == '\\')
                    {
                        state = State.QuoteLiteral;
                    }
                    else
                    {
                        word.Append(c);
                    }

                    break;

                case State.QuoteLiteral:
                    word.Append(c);
                    state = State.QuoteCollect;
                    break;

                case State.Comment:
                    break;
            }
        }

        // An unterminated word (plain or quoted) ends with the line, as in parseconf.
        if (state is State.Collect or State.CollectLiteral or State.QuoteCollect or State.QuoteLiteral)
        {
            EndWord();
        }

        return words;
    }

    /// <summary>
    /// Formats one argument of a request: as is when it is a plain word, otherwise between double quotes with
    /// '"', '\' and '#' escaped (what pconf_encode does), so values with spaces reach upsd intact.
    /// </summary>
    /// <param name="value">The argument.</param>
    /// <param name="always">Quote even a plain word, as upsrw does for the value of SET VAR.</param>
    public static string Quote(string value, bool always = false)
    {
        bool plain = !always && value.Length > 0;
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c) || c == '=' || EscapedCharacters.Contains(c))
            {
                plain = false;
                break;
            }
        }

        if (plain)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 4);
        sb.Append('"');
        foreach (char c in value)
        {
            if (EscapedCharacters.Contains(c))
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Builds a request line from a command and its arguments; arguments are quoted when needed.</summary>
    public static string Build(string command, params string[] arguments)
    {
        var sb = new StringBuilder(command);
        foreach (string argument in arguments)
        {
            sb.Append(' ').Append(Quote(argument));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Whether a value can travel in a request: a line break or another control character would end the line
    /// early or be rejected by upsd, and would let a value smuggle a second command.
    /// </summary>
    public static bool IsTransmittable(string value)
    {
        foreach (char c in value)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }
}
