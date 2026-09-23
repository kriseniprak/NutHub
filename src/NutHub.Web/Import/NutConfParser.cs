using System.Text;

namespace NutHub.Web.Import;

/// <summary>One line of a NUT configuration file, split into words, with its line number.</summary>
internal sealed record NutConfLine(int Number, IReadOnlyList<string> Words);

/// <summary>A "[name]" section of ups.conf or upsd.users with its lines.</summary>
internal sealed record NutConfSection(string Name, int Line, IReadOnlyList<NutConfLine> Lines);

/// <summary>
/// Reads the syntax shared by NUT's ups.conf and upsd.users: words separated by blanks, "=" always a word of its own,
/// double quotes and backslash escapes, "#" comments, "[section]" headers. A port of the state machine of NUT
/// common/parseconf.c (GPL-2.0-or-later).
/// </summary>
internal static class NutConfParser
{
    /// <summary>Maximum size of an imported file; real ones are a few kilobytes.</summary>
    public const int MaxLength = 512 * 1024;

    private enum State
    {
        FindWordStart,
        FindEndOfLine,
        QuoteCollect,
        QuoteLiteral,
        Collect,
        CollectLiteral,
    }

    public static IReadOnlyList<NutConfLine> ReadLines(string text)
    {
        var lines = new List<NutConfLine>();
        var words = new List<string>();
        var word = new StringBuilder();
        State state = State.FindWordStart;
        int lineNumber = 1;
        int startLine = 1;

        void EndWord()
        {
            words.Add(word.ToString());
            word.Clear();
        }

        void EndLine()
        {
            if (words.Count > 0)
            {
                lines.Add(new NutConfLine(startLine, words.ToArray()));
                words.Clear();
            }

            startLine = lineNumber + 1;
        }

        foreach (char raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
        {
            char ch = raw;
            switch (state)
            {
                case State.FindWordStart:
                    if (ch == '\n')
                    {
                        EndLine();
                    }
                    else if (ch == '#')
                    {
                        state = State.FindEndOfLine;
                    }
                    else if (char.IsWhiteSpace(ch))
                    {
                    }
                    else if (ch == '\\')
                    {
                        state = State.CollectLiteral;
                    }
                    else if (ch == '"')
                    {
                        state = State.QuoteCollect;
                    }
                    else if (ch == '=')
                    {
                        word.Append(ch);
                        EndWord();
                    }
                    else
                    {
                        word.Append(ch);
                        state = State.Collect;
                    }

                    break;
                case State.FindEndOfLine:
                    if (ch == '\n')
                    {
                        EndLine();
                        state = State.FindWordStart;
                    }

                    break;
                case State.QuoteCollect:
                    if (ch == '"')
                    {
                        EndWord();
                        state = State.FindWordStart;
                    }
                    else if (ch == '\\')
                    {
                        state = State.QuoteLiteral;
                    }
                    else
                    {
                        word.Append(ch);
                    }

                    break;
                case State.QuoteLiteral:
                    if (ch != '\n')
                    {
                        word.Append(ch);
                    }

                    state = State.QuoteCollect;
                    break;
                case State.Collect:
                    if (ch == '#')
                    {
                        EndWord();
                        state = State.FindEndOfLine;
                    }
                    else if (ch == '\n')
                    {
                        EndWord();
                        EndLine();
                        state = State.FindWordStart;
                    }
                    else if (char.IsWhiteSpace(ch))
                    {
                        EndWord();
                        state = State.FindWordStart;
                    }
                    else if (ch == '=')
                    {
                        EndWord();
                        word.Append('=');
                        EndWord();
                        state = State.FindWordStart;
                    }
                    else if (ch == '\\')
                    {
                        state = State.CollectLiteral;
                    }
                    else
                    {
                        word.Append(ch);
                    }

                    break;
                case State.CollectLiteral:
                    if (ch != '\n')
                    {
                        word.Append(ch);
                    }

                    state = State.Collect;
                    break;
            }

            if (raw == '\n')
            {
                lineNumber++;
            }
        }

        if (state is State.Collect or State.CollectLiteral or State.QuoteCollect or State.QuoteLiteral)
        {
            EndWord();
        }

        EndLine();
        return lines;
    }

    /// <summary>Groups lines into sections; lines before the first section are returned as the global part.</summary>
    public static (IReadOnlyList<NutConfLine> Global, IReadOnlyList<NutConfSection> Sections) ReadSections(string text)
    {
        var global = new List<NutConfLine>();
        var sections = new List<NutConfSection>();
        string? name = null;
        int line = 0;
        var current = new List<NutConfLine>();
        foreach (NutConfLine l in ReadLines(text))
        {
            string first = l.Words[0];
            if (l.Words.Count == 1 && first.Length > 2 && first[0] == '[' && first[^1] == ']')
            {
                if (name is not null)
                {
                    sections.Add(new NutConfSection(name, line, current));
                }

                name = first[1..^1].Trim();
                line = l.Number;
                current = [];
            }
            else if (name is null)
            {
                global.Add(l);
            }
            else
            {
                current.Add(l);
            }
        }

        if (name is not null)
        {
            sections.Add(new NutConfSection(name, line, current));
        }

        return (global, sections);
    }

    /// <summary>
    /// The key and values of a line: "key = v1 v2" gives (key, [v1, v2]); a lone word is a flag with no value;
    /// "key value" (as in "upsmon primary") gives (key, [value]).
    /// </summary>
    public static (string Key, IReadOnlyList<string> Values) KeyValues(NutConfLine line)
    {
        IReadOnlyList<string> w = line.Words;
        if (w.Count >= 2 && w[1] == "=")
        {
            return (w[0], w.Skip(2).ToArray());
        }

        return (w[0], w.Skip(1).ToArray());
    }
}
