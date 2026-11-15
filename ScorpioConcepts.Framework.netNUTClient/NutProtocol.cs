using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ScorpioConcepts.Framework.netNUTClient
{
    /// <summary>
    /// The rules of the upsd network protocol: how arguments are written into a command line,
    /// and how a reply line is split back into its words.
    /// </summary>
    /// <remarks>
    /// A protocol line is a list of words separated by spaces and ended by a newline. A word that holds
    /// spaces is written in double quotes, and inside a word a backslash escapes the next character.
    /// Every argument goes through <see cref="Word"/> or <see cref="Quote"/>, so a value can never end the
    /// line early or add a word: that is what stops a value from injecting a second command.
    /// </remarks>
    internal static class NutProtocol
    {
        /// <summary>The port upsd listens on unless it is configured otherwise.</summary>
        internal const ushort DefaultPort = 3493;

        /// <summary>
        /// Check that a name (a UPS, variable, command or user name) is a single plain word, and return it.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="value"/> is empty, or holds spaces, quotes, backslashes or control characters.</exception>
        internal static string Word(string value, string paramName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(paramName);
            }
            if (value.Length == 0)
            {
                throw new ArgumentException("The value cannot be empty.", paramName);
            }
            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '"' || c == '\\')
                {
                    throw new ArgumentException("The value must be a single word without spaces, quotes, backslashes or control characters.", paramName);
                }
            }

            return value;
        }

        /// <summary>
        /// Write a value as one quoted word, escaping backslashes and double quotes.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="value"/> holds a control character such as a newline, which upsd would read as the end of the command.</exception>
        internal static string Quote(string value, string paramName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(paramName);
            }

            StringBuilder quoted = new StringBuilder(value.Length + 2);
            quoted.Append('"');
            foreach (char c in value)
            {
                if (char.IsControl(c))
                {
                    throw new ArgumentException("The value cannot contain control characters such as line breaks.", paramName);
                }
                if (c == '"' || c == '\\')
                {
                    quoted.Append('\\');
                }
                quoted.Append(c);
            }
            quoted.Append('"');

            return quoted.ToString();
        }

        /// <summary>
        /// Write a value as a plain word when it is one, and as a quoted word otherwise.
        /// Used for passwords, which may hold any character but are usually plain words.
        /// </summary>
        internal static string QuoteIfNeeded(string value, string paramName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(paramName);
            }
            if (value.Length > 0)
            {
                bool plain = true;
                foreach (char c in value)
                {
                    if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '"' || c == '\\')
                    {
                        plain = false;
                        break;
                    }
                }
                if (plain)
                {
                    return value;
                }
            }

            return Quote(value, paramName);
        }

        /// <summary>
        /// Split one reply line into its words, removing quotes and escapes.
        /// </summary>
        internal static List<string> Tokenize(string line)
        {
            List<string> words = new List<string>();
            StringBuilder current = new StringBuilder();
            bool inQuotes = false;
            bool inWord = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\\' && i + 1 < line.Length)
                {
                    // An escaped character is always part of the word, inside quotes or not
                    current.Append(line[++i]);
                    inWord = true;
                }
                else if (inQuotes)
                {
                    if (c == '"')
                    {
                        inQuotes = false;
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else if (c == '"')
                {
                    // "" is an empty word, so a quote starts a word even before any character
                    inQuotes = true;
                    inWord = true;
                }
                else if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    if (inWord)
                    {
                        words.Add(current.ToString());
                        current.Clear();
                        inWord = false;
                    }
                }
                else
                {
                    current.Append(c);
                    inWord = true;
                }
            }

            if (inWord)
            {
                words.Add(current.ToString());
            }

            return words;
        }

        /// <summary>
        /// Split a server address into host and port. Accepts "host", "host:port", an IPv6 address
        /// such as "::1", and an IPv6 address with a port such as "[::1]:3493".
        /// </summary>
        /// <exception cref="ArgumentException">The address is empty, or an IPv6 address in brackets is not closed.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The port is not a number from 1 to 65535.</exception>
        internal static void ParseServer(string server, string paramName, out string host, out ushort port)
        {
            if (server == null)
            {
                throw new ArgumentNullException(paramName);
            }

            string value = server.Trim();
            string? portText = null;
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                int close = value.IndexOf(']');
                if (close < 0)
                {
                    throw new ArgumentException("The IPv6 address is missing its closing ']'.", paramName);
                }
                host = value.Substring(1, close - 1);
                string rest = value.Substring(close + 1);
                if (rest.Length > 0)
                {
                    if (rest[0] != ':')
                    {
                        throw new ArgumentException("Only a port can follow an IPv6 address in brackets, as in [::1]:3493.", paramName);
                    }
                    portText = rest.Substring(1);
                }
            }
            else
            {
                int first = value.IndexOf(':');
                if (first >= 0 && first == value.LastIndexOf(':'))
                {
                    // One colon: host:port
                    host = value.Substring(0, first);
                    portText = value.Substring(first + 1);
                }
                else
                {
                    // No colon, or several: a host name or a bare IPv6 address, which has no port
                    host = value;
                }
            }

            if (host.Length == 0)
            {
                throw new ArgumentException("The server address has no host name.", paramName);
            }

            port = DefaultPort;
            if (portText != null)
            {
                if (ushort.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) == false || port == 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, server, "The port must be a number from 1 to 65535.");
                }
            }
        }
    }
}
