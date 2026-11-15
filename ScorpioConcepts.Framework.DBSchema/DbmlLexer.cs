using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ScorpioConcepts.Framework.DBSchema
{
    internal enum DbmlTokenKind
    {
        /// <summary>A name such as Table or varchar</summary>
        Identifier,
        /// <summary>A "double quoted" name</summary>
        QuotedName,
        /// <summary>A 'single quoted' or '''triple quoted''' string</summary>
        String,
        /// <summary>A `backtick` expression</summary>
        Expression,
        Number,
        /// <summary>A #colour</summary>
        Color,
        /// <summary>One of the relationship operators, made of &lt; &gt; - and ?</summary>
        Operator,
        /// <summary>A single punctuation character: { } [ ] ( ) , : . ~ * ;</summary>
        Punctuation,
        NewLine,
        EndOfFile,
    }

    internal sealed class DbmlToken
    {
        public DbmlTokenKind Kind;
        /// <summary>The value: a name or string with its quotes removed and escapes applied, or the raw text otherwise</summary>
        public string Text = "";
        public int Line;
        public int Column;

        public bool Is(string punctuation)
        {
            return (Kind == DbmlTokenKind.Punctuation) && (Text == punctuation);
        }

        /// <summary>Is this the given keyword (DBML keywords are not case sensitive)</summary>
        public bool IsKeyword(string keyword)
        {
            return (Kind == DbmlTokenKind.Identifier) && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);
        }

        public bool IsName
        {
            get { return (Kind == DbmlTokenKind.Identifier) || (Kind == DbmlTokenKind.QuotedName); }
        }

        public string Describe()
        {
            switch (Kind)
            {
                case DbmlTokenKind.NewLine: return "end of line";
                case DbmlTokenKind.EndOfFile: return "end of file";
                case DbmlTokenKind.String: return "string '" + Text + "'";
                case DbmlTokenKind.QuotedName: return "\"" + Text + "\"";
                case DbmlTokenKind.Expression: return "`" + Text + "`";
                default: return "'" + Text + "'";
            }
        }
    }

    /// <summary>
    /// Splits DBML text into tokens, following the rules of the dbml-parse lexer: comments are dropped,
    /// line breaks are kept because DBML statements end at the end of a line.
    /// </summary>
    internal sealed class DbmlLexer
    {
        private readonly string _text;
        private int _pos;
        private int _line = 1;
        private int _column = 1;
        private readonly List<DbmlToken> _tokens = new List<DbmlToken>();

        private DbmlLexer(string text)
        {
            _text = text;
        }

        public static List<DbmlToken> Tokenize(string text)
        {
            DbmlLexer lexer = new DbmlLexer(text);
            lexer.Run();
            return lexer._tokens;
        }

        internal static FormatException Error(int line, int column, string message)
        {
            return new FormatException("DBML line " + line.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", column " + column.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": " + message);
        }

        private char Peek(int ahead = 0)
        {
            int i = _pos + ahead;
            return (i < _text.Length) ? _text[i] : '\0';
        }

        private bool AtEnd
        {
            get { return _pos >= _text.Length; }
        }

        private char Advance()
        {
            char c = _text[_pos++];
            if (c == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }
            return c;
        }

        private bool Match(string sequence)
        {
            if (string.CompareOrdinal(_text, _pos, sequence, 0, sequence.Length) != 0)
            {
                return false;
            }
            for (int i = 0; i < sequence.Length; i++)
            {
                Advance();
            }
            return true;
        }

        private void Add(DbmlTokenKind kind, string text, int line, int column)
        {
            _tokens.Add(new DbmlToken { Kind = kind, Text = text, Line = line, Column = column });
        }

        private void Run()
        {
            SkipFrontMatter();

            while (!AtEnd)
            {
                int line = _line;
                int column = _column;
                char c = Advance();

                switch (c)
                {
                    case ' ':
                    case '\t':
                    case '\r':
                        break;
                    case '\n':
                        Add(DbmlTokenKind.NewLine, "\n", line, column);
                        break;
                    case '{':
                    case '}':
                    case '[':
                    case ']':
                    case '(':
                    case ')':
                    case ',':
                    case ':':
                    case '.':
                    case '~':
                    case '*':
                    case ';':
                        Add(DbmlTokenKind.Punctuation, c.ToString(), line, column);
                        break;
                    case '\'':
                        if (Match("''"))
                        {
                            Add(DbmlTokenKind.String, NormalizeBlockString(ReadUntil("'''", true, true, line, column)), line, column);
                        }
                        else
                        {
                            Add(DbmlTokenKind.String, ReadUntil("'", false, true, line, column), line, column);
                        }
                        break;
                    case '"':
                        Add(DbmlTokenKind.QuotedName, ReadUntil("\"", false, true, line, column), line, column);
                        break;
                    case '`':
                        Add(DbmlTokenKind.Expression, ReadUntil("`", true, false, line, column), line, column);
                        break;
                    case '#':
                        Add(DbmlTokenKind.Color, "#" + ReadWord(), line, column);
                        break;
                    case '/':
                        if (Match("/"))
                        {
                            while (!AtEnd && (Peek() != '\n'))
                            {
                                Advance();
                            }
                        }
                        else if (Match("*"))
                        {
                            ReadUntil("*/", true, false, line, column);
                        }
                        else
                        {
                            throw Error(line, column, "Unexpected character '/'");
                        }
                        break;
                    case '<':
                    case '>':
                    case '-':
                    case '?':
                        Add(DbmlTokenKind.Operator, ReadOperator(c), line, column);
                        break;
                    default:
                        if (char.IsLetter(c) || (c == '_'))
                        {
                            Add(DbmlTokenKind.Identifier, c + ReadWord(), line, column);
                        }
                        else if (char.IsDigit(c))
                        {
                            ReadNumberOrIdentifier(c, line, column);
                        }
                        else
                        {
                            throw Error(line, column, "Unexpected character '" + c + "'");
                        }
                        break;
                }
            }

            Add(DbmlTokenKind.EndOfFile, "", _line, _column);
        }

        /// <summary>A --- fenced block at the very top of the file holds metadata, and is ignored</summary>
        private void SkipFrontMatter()
        {
            if (!_text.StartsWith("---\n", StringComparison.Ordinal) && !_text.StartsWith("---\r\n", StringComparison.Ordinal))
            {
                return;
            }
            int closing = _text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (closing == -1)
            {
                return;
            }
            int end = _text.IndexOf('\n', closing + 1);
            if (end == -1)
            {
                end = _text.Length;
            }
            while (_pos < end)
            {
                Advance();
            }
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || (c == '_') || (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark);
        }

        private string ReadWord()
        {
            int start = _pos;
            while (!AtEnd && IsWordChar(Peek()))
            {
                Advance();
            }
            return _text.Substring(start, _pos - start);
        }

        /// <summary>Numbers can have a fraction and an exponent; a word that starts with digits, such as 2fa, is a name</summary>
        private void ReadNumberOrIdentifier(char first, int line, int column)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(first);
            while (char.IsDigit(Peek()))
            {
                sb.Append(Advance());
            }
            if ((Peek() == '.') && char.IsDigit(Peek(1)))
            {
                sb.Append(Advance());
                while (char.IsDigit(Peek()))
                {
                    sb.Append(Advance());
                }
            }
            if (((Peek() == 'e') || (Peek() == 'E'))
                && (char.IsDigit(Peek(1)) || (((Peek(1) == '+') || (Peek(1) == '-')) && char.IsDigit(Peek(2)))))
            {
                sb.Append(Advance());
                if (!char.IsDigit(Peek()))
                {
                    sb.Append(Advance());
                }
                while (char.IsDigit(Peek()))
                {
                    sb.Append(Advance());
                }
            }

            if (IsWordChar(Peek()))
            {
                string text = sb.ToString();
                if (text.Contains('.'))
                {
                    throw Error(line, column, "Invalid number '" + text + ReadWord() + "'");
                }
                Add(DbmlTokenKind.Identifier, text + ReadWord(), line, column);
                return;
            }

            Add(DbmlTokenKind.Number, sb.ToString(), line, column);
        }

        /// <summary>Relationship operators: &lt; &gt; - &lt;&gt; with an optional ? on either side</summary>
        private string ReadOperator(char first)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(first);
            // A minus sign, as in 'default: -1', is a '-' operator followed by a number
            while ((Peek() == '<') || (Peek() == '>') || (Peek() == '-') || (Peek() == '?'))
            {
                sb.Append(Advance());
            }
            return sb.ToString();
        }

        private string ReadUntil(string stop, bool allowNewLine, bool escapes, int line, int column)
        {
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw Error(line, column, "The text that starts here is never closed with " + stop);
                }
                if (Match(stop))
                {
                    return sb.ToString();
                }
                if (!allowNewLine && (Peek() == '\n'))
                {
                    throw Error(line, column, "The text that starts here is never closed with " + stop + " before the end of the line");
                }

                char c = Advance();
                if (escapes && (c == '\\'))
                {
                    sb.Append(ReadEscape(line, column));
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        private string ReadEscape(int line, int column)
        {
            if (AtEnd)
            {
                return "\\";
            }
            char c = Advance();
            switch (c)
            {
                case '\r':
                    if (Peek() == '\n')
                    {
                        Advance();
                    }
                    return "";
                case '\n': return "";
                case 't': return "\t";
                case 'n': return "\n";
                case 'r': return "\r";
                case '0': return "\0";
                case 'b': return "\b";
                case 'v': return "\v";
                case 'f': return "\f";
                case ' ': return "\\ ";
                case 'u':
                    string hex = "";
                    for (int i = 0; i < 4; i++)
                    {
                        if (!Uri.IsHexDigit(Peek()))
                        {
                            throw Error(line, column, "Invalid unicode escape '\\u" + hex + "', it must be \\uHHHH");
                        }
                        hex += Advance();
                    }
                    return ((char)Convert.ToInt32(hex, 16)).ToString();
                default:
                    // \\ \' \" \` and any other character stand for themselves
                    return c.ToString();
            }
        }

        /// <summary>
        /// A ''' string drops its leading blank lines and the indentation shared by all its lines,
        /// the same way dbml-parse does
        /// </summary>
        private static string NormalizeBlockString(string text)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int first = 0;
            while ((first < lines.Length) && (lines[first].Trim().Length == 0))
            {
                first++;
            }
            if (first == lines.Length)
            {
                return string.Join("\n", lines, first, 0);
            }

            int indent = int.MaxValue;
            for (int i = first; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.Length > 0)
                {
                    indent = Math.Min(indent, lines[i].Length - trimmed.Length);
                }
            }

            StringBuilder sb = new StringBuilder();
            for (int i = first; i < lines.Length; i++)
            {
                if (i > first)
                {
                    sb.Append('\n');
                }
                sb.Append(lines[i].Length >= indent ? lines[i].Substring(indent) : lines[i].TrimStart());
            }
            return sb.ToString();
        }
    }
}
