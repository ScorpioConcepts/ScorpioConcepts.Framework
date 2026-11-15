using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ScorpioConcepts.Framework.DBSchema
{
    /// <summary>
    /// Parses the subset of DBML that a <see cref="DBSchemaDefinition"/> can hold, and builds the schema from it.
    /// </summary>
    /// <remarks>
    /// Parsing happens first, into plain classes, so that references and records can name tables
    /// (or table aliases) that are defined further down the file. Elements that only affect how a
    /// diagram looks (Project, Note, TableGroup, DiagramView, Metadata, colours, notes and custom
    /// properties) are skipped. Anything that would change the database structure but that DBSchema
    /// cannot create is rejected with a <see cref="FormatException"/>, so it is never silently lost.
    /// </remarks>
    internal sealed class DbmlReader
    {
        #region Parsed elements

        private sealed class Setting
        {
            /// <summary>The setting name, in lower case, with multi-word names such as "not null" joined by one space</summary>
            public string Key = "";
            /// <summary>The tokens after the ':', if any</summary>
            public List<DbmlToken> Value = new List<DbmlToken>();
            /// <summary>For an inline ref: the operator and the other end</summary>
            public string? RefOperator;
            public Endpoint? RefTarget;
            public int Line;
            public int Column;
        }

        private sealed class Endpoint
        {
            public string Table = "";
            public List<string> Columns = new List<string>();
            public int Line;
            public int Column;
        }

        private sealed class ParsedColumn
        {
            public string Name = "";
            public string Type = "";
            public List<Setting> Settings = new List<Setting>();
            public int Line;
            public int Column;
        }

        private sealed class ParsedIndexColumn
        {
            public string Name = "";
            public bool Descending;
        }

        private sealed class ParsedIndex
        {
            public List<ParsedIndexColumn> Columns = new List<ParsedIndexColumn>();
            public List<Setting> Settings = new List<Setting>();
            public int Line;
            public int Column;
        }

        private sealed class ParsedRecords
        {
            /// <summary>The table the records are for</summary>
            public string Table = "";
            /// <summary>The column list, or null to use every column of the table in order</summary>
            public List<string>? Columns;
            /// <summary>Each row's values; a null value is an empty field</summary>
            public List<List<List<DbmlToken>?>> Rows = new List<List<List<DbmlToken>?>>();
            public bool Example;
            public int Line;
            public int Column;
        }

        private sealed class ParsedTable
        {
            public string Name = "";
            public string? Alias;
            public List<Setting> Settings = new List<Setting>();
            public List<ParsedColumn> Columns = new List<ParsedColumn>();
            public List<ParsedIndex> Indexes = new List<ParsedIndex>();
            public List<ParsedRecords> Records = new List<ParsedRecords>();
            public int Line;
            public int Column;
        }

        private sealed class ParsedRef
        {
            public string? Name;
            public Endpoint Left = new Endpoint();
            public string Operator = "";
            public Endpoint Right = new Endpoint();
            /// <summary>Written in a column's settings, where Left is the column itself</summary>
            public bool Inline;
            public List<Setting> Settings = new List<Setting>();
            public int Line;
            public int Column;
        }

        #endregion

        private readonly List<DbmlToken> _tokens;
        private int _pos;

        private readonly List<ParsedTable> _tables = new List<ParsedTable>();
        private readonly List<ParsedRef> _refs = new List<ParsedRef>();
        private readonly List<ParsedRecords> _records = new List<ParsedRecords>();

        private DbmlReader(List<DbmlToken> tokens)
        {
            _tokens = tokens;
        }

        /// <summary>
        /// Read a schema from DBML text
        /// </summary>
        /// <exception cref="FormatException">The text is not valid DBML, or uses something DBSchema cannot create</exception>
        public static DBSchemaDefinition Parse(string text)
        {
            DbmlReader reader = new DbmlReader(DbmlLexer.Tokenize(text));
            reader.ParseProgram();
            return reader.Build();
        }

        #region Token helpers

        private DbmlToken Peek(int ahead = 0)
        {
            int i = Math.Min(_pos + ahead, _tokens.Count - 1);
            return _tokens[i];
        }

        private DbmlToken Next()
        {
            DbmlToken token = _tokens[_pos];
            if (token.Kind != DbmlTokenKind.EndOfFile)
            {
                _pos++;
            }
            return token;
        }

        private static FormatException Error(DbmlToken at, string message)
        {
            return DbmlLexer.Error(at.Line, at.Column, message);
        }

        private static FormatException Error(int line, int column, string message)
        {
            return DbmlLexer.Error(line, column, message);
        }

        private void SkipNewLines()
        {
            while (Peek().Kind == DbmlTokenKind.NewLine)
            {
                _pos++;
            }
        }

        private DbmlToken Expect(string punctuation, string what)
        {
            DbmlToken token = Peek();
            if (!token.Is(punctuation))
            {
                throw Error(token, "Expected '" + punctuation + "' " + what + ", found " + token.Describe());
            }
            return Next();
        }

        /// <summary>A statement ends at the end of its line, or where the enclosing block closes</summary>
        private void ExpectEndOfLine()
        {
            DbmlToken token = Peek();
            if (token.Kind == DbmlTokenKind.NewLine)
            {
                Next();
            }
            else if ((token.Kind != DbmlTokenKind.EndOfFile) && !token.Is("}"))
            {
                throw Error(token, "Expected the end of the line, found " + token.Describe());
            }
        }

        private string ParseName(string what)
        {
            DbmlToken token = Peek();
            if (!token.IsName)
            {
                throw Error(token, "Expected " + what + ", found " + token.Describe());
            }
            return Next().Text;
        }

        /// <summary>A name such as users, "my table" or dbo.users, as its parts</summary>
        private List<string> ParseQualifiedName(string what)
        {
            List<string> parts = new List<string>();
            parts.Add(ParseName(what));
            while (Peek().Is(".") && Peek(1).IsName)
            {
                Next();
                parts.Add(Next().Text);
            }
            return parts;
        }

        /// <summary>
        /// DBSchema only works with the dbo schema; DBML calls the default schema public, so either
        /// name is accepted
        /// </summary>
        private static string TableName(List<string> parts, DbmlToken at)
        {
            if (parts.Count == 1)
            {
                return parts[0];
            }
            if ((parts.Count == 2)
                && (string.Equals(parts[0], "dbo", StringComparison.OrdinalIgnoreCase) || string.Equals(parts[0], "public", StringComparison.OrdinalIgnoreCase)))
            {
                return parts[1];
            }
            throw Error(at, "'" + string.Join(".", parts.ToArray()) + "' is in another schema; DBSchema only supports tables in the dbo schema");
        }

        #endregion

        #region Parsing

        private void ParseProgram()
        {
            while (true)
            {
                SkipNewLines();
                DbmlToken token = Peek();
                if (token.Kind == DbmlTokenKind.EndOfFile)
                {
                    return;
                }
                if (token.Kind != DbmlTokenKind.Identifier)
                {
                    throw Error(token, "Expected Table, Ref or Records, found " + token.Describe());
                }

                switch (token.Text.ToLowerInvariant())
                {
                    case "table":
                        ParseTable();
                        break;
                    case "ref":
                        ParseRefStatement();
                        break;
                    case "records":
                        Next();
                        ParseRecords(null, token);
                        break;
                    case "project":
                    case "note":
                    case "tablegroup":
                    case "diagramview":
                    case "metadata":
                        // These only describe the project or how its diagram looks
                        SkipElement();
                        break;
                    case "enum":
                        throw Error(token, "Enums are not supported, because SQL Server has no enum type");
                    case "tablepartial":
                        throw Error(token, "TablePartial is not supported; write the columns into each table");
                    case "use":
                    case "reuse":
                        throw Error(token, "Importing other DBML files is not supported; DBSchema reads a single file");
                    default:
                        throw Error(token, "'" + token.Text + "' is not supported; expected Table, Ref or Records");
                }
            }
        }

        /// <summary>
        /// Skip a whole element: either everything up to the end of its line, or through the end of its { } block
        /// </summary>
        private void SkipElement()
        {
            int depth = 0;
            while (true)
            {
                DbmlToken token = Peek();
                if (token.Kind == DbmlTokenKind.EndOfFile)
                {
                    if (depth > 0)
                    {
                        throw Error(token, "A '{' is never closed");
                    }
                    return;
                }
                if ((token.Kind == DbmlTokenKind.NewLine) && (depth == 0))
                {
                    // The block can open on the next line
                    int save = _pos;
                    SkipNewLines();
                    if (!Peek().Is("{"))
                    {
                        _pos = save;
                        Next();
                        return;
                    }
                    continue;
                }
                if ((depth == 0) && token.Is("}"))
                {
                    // The end of the block this element is in
                    return;
                }

                Next();
                if (token.Is("{") || token.Is("[") || token.Is("("))
                {
                    depth++;
                }
                else if (token.Is("}") || token.Is("]") || token.Is(")"))
                {
                    depth--;
                    if ((depth == 0) && token.Is("}"))
                    {
                        return;
                    }
                }
            }
        }

        private void ParseTable()
        {
            DbmlToken keyword = Next();
            DbmlToken nameToken = Peek();
            ParsedTable table = new ParsedTable();
            table.Name = TableName(ParseQualifiedName("a table name"), nameToken);
            table.Line = nameToken.Line;
            table.Column = nameToken.Column;

            if (Peek().IsKeyword("as"))
            {
                Next();
                table.Alias = ParseName("a table alias");
            }
            if (Peek().Is("["))
            {
                // Colours, notes and custom properties; only clustered_index matters here
                table.Settings = ParseSettings();
            }

            SkipNewLines();
            Expect("{", "to start table '" + table.Name + "'");

            while (true)
            {
                SkipNewLines();
                DbmlToken token = Peek();
                if (token.Is("}"))
                {
                    Next();
                    break;
                }
                if (token.Kind == DbmlTokenKind.EndOfFile)
                {
                    throw Error(keyword, "Table '" + table.Name + "' is never closed with '}'");
                }
                if (token.Is("~"))
                {
                    throw Error(token, "TablePartial is not supported; write the columns into each table");
                }

                DbmlToken following = Peek(1);
                if (token.IsKeyword("indexes") && following.Is("{"))
                {
                    ParseIndexes(table);
                }
                else if (token.IsKeyword("checks") && following.Is("{"))
                {
                    throw Error(token, "Check constraints are not supported");
                }
                else if (token.IsKeyword("records") && (following.Is("(") || following.Is("[") || following.Is("{")))
                {
                    Next();
                    table.Records.Add(ParseRecords(table.Name, token));
                }
                else if (token.IsKeyword("note") && (following.Is(":") || following.Is("{")))
                {
                    SkipElement();
                }
                else
                {
                    ParsedColumn column = ParseColumn();
                    table.Columns.Add(column);
                    AddInlineRefs(table, column);
                }
            }

            _tables.Add(table);
        }

        /// <summary>A column's [ref: ...] settings become relationships here, so foreign keys stay in file order</summary>
        private void AddInlineRefs(ParsedTable table, ParsedColumn column)
        {
            foreach (Setting setting in column.Settings)
            {
                if (setting.Key != "ref")
                {
                    continue;
                }
                if (setting.RefTarget == null)
                {
                    throw Error(setting.Line, setting.Column, "The ref of column '" + column.Name + "' needs a target, as in [ref: > users.id]");
                }

                Endpoint self = new Endpoint { Table = table.Name, Line = setting.Line, Column = setting.Column };
                self.Columns.Add(column.Name);
                _refs.Add(new ParsedRef
                {
                    Left = self,
                    Operator = setting.RefOperator ?? "",
                    Right = setting.RefTarget,
                    Inline = true,
                    Line = setting.Line,
                    Column = setting.Column,
                });
            }
        }

        private ParsedColumn ParseColumn()
        {
            DbmlToken start = Peek();
            ParsedColumn column = new ParsedColumn();
            column.Name = ParseName("a column name");
            column.Line = start.Line;
            column.Column = start.Column;
            column.Type = ParseType(column.Name);
            column.Settings = Peek().Is("[") ? ParseSettings() : new List<Setting>();
            ExpectEndOfLine();
            return column;
        }

        /// <summary>A type such as int, varchar(max), decimal(10,2) or "double precision"</summary>
        private string ParseType(string columnName)
        {
            DbmlToken token = Peek();
            if (token.Kind == DbmlTokenKind.QuotedName)
            {
                return Next().Text;
            }
            if (token.Kind != DbmlTokenKind.Identifier)
            {
                throw Error(token, "Column '" + columnName + "' needs a type, found " + token.Describe());
            }

            string type = string.Join(".", ParseQualifiedName("a type").ToArray());
            if (Peek().Is("("))
            {
                Next();
                type += "(";
                while (!Peek().Is(")"))
                {
                    DbmlToken part = Next();
                    if (part.Is(","))
                    {
                        type += ",";
                    }
                    else if ((part.Kind == DbmlTokenKind.Number) || (part.Kind == DbmlTokenKind.Identifier))
                    {
                        type += part.Text;
                    }
                    else
                    {
                        throw Error(part, "Unexpected " + part.Describe() + " in the type of column '" + columnName + "'");
                    }
                }
                Next();
                type += ")";
            }
            return type;
        }

        /// <summary>A [ ] list of settings, such as [pk, not null, default: 0, ref: > users.id]</summary>
        private List<Setting> ParseSettings()
        {
            Expect("[", "to start the settings");
            List<Setting> settings = new List<Setting>();

            while (true)
            {
                SkipNewLines();
                if (Peek().Is("]"))
                {
                    Next();
                    return settings;
                }

                DbmlToken start = Peek();
                Setting setting = new Setting { Line = start.Line, Column = start.Column };
                List<string> words = new List<string>();
                while (Peek().IsName)
                {
                    words.Add(Next().Text.ToLowerInvariant());
                }
                if (words.Count == 0)
                {
                    throw Error(start, "Expected a setting name, found " + start.Describe());
                }
                setting.Key = string.Join(" ", words.ToArray());

                if (Peek().Is(":"))
                {
                    Next();
                    if (setting.Key == "ref")
                    {
                        DbmlToken op = Peek();
                        if (op.Kind != DbmlTokenKind.Operator)
                        {
                            throw Error(op, "Expected <, > or - after 'ref:', found " + op.Describe());
                        }
                        setting.RefOperator = Next().Text;
                        setting.RefTarget = ParseEndpoint();
                    }
                    else
                    {
                        int depth = 0;
                        while (true)
                        {
                            DbmlToken token = Peek();
                            if (token.Kind == DbmlTokenKind.EndOfFile)
                            {
                                throw Error(start, "The settings list is never closed with ']'");
                            }
                            if ((depth == 0) && (token.Is(",") || token.Is("]")))
                            {
                                break;
                            }
                            if (token.Is("(") || token.Is("[") || token.Is("{")) depth++;
                            if (token.Is(")") || token.Is("]") || token.Is("}")) depth--;
                            Next();
                            if (token.Kind != DbmlTokenKind.NewLine)
                            {
                                setting.Value.Add(token);
                            }
                        }
                    }
                }
                settings.Add(setting);

                SkipNewLines();
                DbmlToken separator = Peek();
                if (separator.Is(","))
                {
                    Next();
                }
                else if (!separator.Is("]"))
                {
                    throw Error(separator, "Expected ',' or ']' in the settings, found " + separator.Describe());
                }
            }
        }

        /// <summary>One end of a relationship: table.column, table.(a, b), or either with a schema in front</summary>
        private Endpoint ParseEndpoint()
        {
            DbmlToken start = Peek();
            Endpoint endpoint = new Endpoint { Line = start.Line, Column = start.Column };
            List<string> parts = new List<string>();
            bool composite = false;

            while (true)
            {
                if (Peek().Is("(") && (parts.Count > 0))
                {
                    Next();
                    while (true)
                    {
                        endpoint.Columns.Add(ParseName("a column name"));
                        if (!Peek().Is(","))
                        {
                            break;
                        }
                        Next();
                    }
                    Expect(")", "to close the column list");
                    composite = true;
                    break;
                }

                parts.Add(ParseName("a table and column, such as users.id"));
                if (!Peek().Is("."))
                {
                    break;
                }
                Next();
            }

            if (!composite)
            {
                if (parts.Count < 2)
                {
                    throw Error(start, "Expected a table and column, such as users.id, found '" + parts[0] + "'");
                }
                endpoint.Columns.Add(parts[parts.Count - 1]);
                parts.RemoveAt(parts.Count - 1);
            }
            endpoint.Table = TableName(parts, start);
            return endpoint;
        }

        private void ParseRefStatement()
        {
            Next();
            string? name = null;
            if (Peek().IsName)
            {
                name = Next().Text;
            }

            if (Peek().Is(":"))
            {
                Next();
                ParseRefLine(name);
                ExpectEndOfLine();
                return;
            }

            SkipNewLines();
            DbmlToken open = Expect("{", "or ':' after Ref");
            int count = 0;
            while (true)
            {
                SkipNewLines();
                if (Peek().Is("}"))
                {
                    Next();
                    break;
                }
                if (Peek().Kind == DbmlTokenKind.EndOfFile)
                {
                    throw Error(open, "The Ref block is never closed with '}'");
                }
                if (++count > 1 && name != null)
                {
                    throw Error(Peek(), "A named Ref block can only hold one relationship, because each foreign key needs its own name");
                }
                ParseRefLine(name);
                ExpectEndOfLine();
            }
        }

        private void ParseRefLine(string? name)
        {
            DbmlToken start = Peek();
            ParsedRef parsed = new ParsedRef { Name = name, Line = start.Line, Column = start.Column };
            parsed.Left = ParseEndpoint();
            DbmlToken op = Peek();
            if (op.Kind != DbmlTokenKind.Operator)
            {
                throw Error(op, "Expected a relationship operator (<, > or -), found " + op.Describe());
            }
            parsed.Operator = Next().Text;
            parsed.Right = ParseEndpoint();
            if (Peek().Is("["))
            {
                parsed.Settings = ParseSettings();
            }
            _refs.Add(parsed);
        }

        private void ParseIndexes(ParsedTable table)
        {
            Next();
            DbmlToken open = Expect("{", "after indexes");
            while (true)
            {
                SkipNewLines();
                DbmlToken token = Peek();
                if (token.Is("}"))
                {
                    Next();
                    return;
                }
                if (token.Kind == DbmlTokenKind.EndOfFile)
                {
                    throw Error(open, "The indexes block of table '" + table.Name + "' is never closed with '}'");
                }

                ParsedIndex index = new ParsedIndex { Line = token.Line, Column = token.Column };
                if (token.Is("("))
                {
                    Next();
                    while (true)
                    {
                        index.Columns.Add(ParseIndexColumn(table, true));
                        if (!Peek().Is(","))
                        {
                            break;
                        }
                        Next();
                    }
                    Expect(")", "to close the index columns");
                }
                else
                {
                    index.Columns.Add(ParseIndexColumn(table, false));
                }

                index.Settings = Peek().Is("[") ? ParseSettings() : new List<Setting>();
                ExpectEndOfLine();
                table.Indexes.Add(index);
            }
        }

        private ParsedIndexColumn ParseIndexColumn(ParsedTable table, bool inParentheses)
        {
            DbmlToken token = Peek();
            if (token.Kind == DbmlTokenKind.Expression)
            {
                throw Error(token, "Indexes on expressions are not supported; an index of table '" + table.Name + "' uses `" + token.Text + "`");
            }

            ParsedIndexColumn column = new ParsedIndexColumn();
            column.Name = ParseName("an index column");
            if (inParentheses && Peek().Is("["))
            {
                foreach (Setting setting in ParseSettings())
                {
                    if (setting.Key == "desc")
                    {
                        column.Descending = true;
                    }
                    else if (setting.Key != "asc")
                    {
                        throw Error(setting.Line, setting.Column, "An index column can only be [asc] or [desc]");
                    }
                }
            }
            return column;
        }

        /// <summary>Everything after the records keyword: [table] [(columns)] [settings] { rows }</summary>
        private ParsedRecords ParseRecords(string? insideTable, DbmlToken keyword)
        {
            ParsedRecords records = new ParsedRecords { Table = insideTable ?? "", Line = keyword.Line, Column = keyword.Column };
            if (insideTable == null)
            {
                DbmlToken nameToken = Peek();
                records.Table = TableName(ParseQualifiedName("the table the records are for"), nameToken);
            }

            if (Peek().Is("("))
            {
                Next();
                records.Columns = new List<string>();
                while (true)
                {
                    records.Columns.Add(ParseName("a column name"));
                    if (!Peek().Is(","))
                    {
                        break;
                    }
                    Next();
                }
                Expect(")", "to close the column list");
            }
            else if (insideTable == null)
            {
                throw Error(keyword, "Records outside a table must list their columns, as in records " + records.Table + "(id, name)");
            }

            if (Peek().Is("["))
            {
                records.Example = ParseSettings().Any(s => s.Key == "example");
            }

            SkipNewLines();
            DbmlToken open = Expect("{", "to start the records");
            while (true)
            {
                SkipNewLines();
                DbmlToken token = Peek();
                if (token.Is("}"))
                {
                    Next();
                    break;
                }
                if (token.Kind == DbmlTokenKind.EndOfFile)
                {
                    throw Error(open, "The records of table '" + records.Table + "' are never closed with '}'");
                }

                List<List<DbmlToken>?> row = new List<List<DbmlToken>?>();
                List<DbmlToken> value = new List<DbmlToken>();
                while (true)
                {
                    DbmlToken part = Peek();
                    if ((part.Kind == DbmlTokenKind.NewLine) || (part.Kind == DbmlTokenKind.EndOfFile) || part.Is("}"))
                    {
                        row.Add(value.Count > 0 ? value : null);
                        break;
                    }
                    Next();
                    if (part.Is(","))
                    {
                        row.Add(value.Count > 0 ? value : null);
                        value = new List<DbmlToken>();
                    }
                    else
                    {
                        value.Add(part);
                    }
                }
                records.Rows.Add(row);
            }

            if (insideTable == null)
            {
                _records.Add(records);
            }
            return records;
        }

        #endregion

        #region Building the schema

        /// <summary>A column's settings, worked out before the column is added, because the model is read-only</summary>
        private sealed class ColumnPlan
        {
            public ParsedColumn Parsed = new ParsedColumn();
            public bool? NotNull;
            public bool Identity;
            public string? IdentitySeed;
            public string? IdentityIncrement;
            public string? DefaultSql;
            public string? DefaultName;
            public bool Persisted;
            public bool Computed;
        }

        private DBSchemaDefinition Build()
        {
            var schema = new DBSchemaDefinition();
            var aliases = new Dictionary<string, string>(SqlName.Comparer);
            var parsedTables = new Dictionary<string, ParsedTable>(SqlName.Comparer);

            foreach (ParsedTable table in _tables)
            {
                if (parsedTables.ContainsKey(table.Name))
                {
                    throw Error(table.Line, table.Column, "Table '" + table.Name + "' is defined more than once");
                }
                parsedTables.Add(table.Name, table);
                if (table.Alias != null)
                {
                    if (aliases.ContainsKey(table.Alias))
                    {
                        throw Error(table.Line, table.Column, "The alias '" + table.Alias + "' is used for more than one table");
                    }
                    aliases.Add(table.Alias, table.Name);
                }
            }

            foreach (ParsedTable table in _tables)
            {
                BuildTable(schema, table);
            }

            // A table's own name wins over an alias, and the name is used as the table defines it
            string Resolve(string name)
            {
                if (schema.Tables.TryGetValue(name, out DBTable? table)) return table.Name;
                return aliases.TryGetValue(name, out string? aliased) ? aliased : name;
            }

            foreach (ParsedRef parsed in _refs)
            {
                BuildForeignKey(schema, parsed, Resolve);
            }

            foreach (ParsedTable table in _tables)
            {
                foreach (ParsedRecords records in table.Records)
                {
                    BuildRecords(schema.Tables[table.Name], records);
                }
            }
            foreach (ParsedRecords records in _records)
            {
                if (!schema.Tables.TryGetValue(Resolve(records.Table), out DBTable? table))
                {
                    throw Error(records.Line, records.Column, "The records are for table '" + records.Table + "', which is not defined");
                }
                BuildRecords(table, records);
            }

            return schema;
        }

        private static void BuildTable(DBSchemaDefinition schema, ParsedTable parsed)
        {
            var columnNames = new HashSet<string>(SqlName.Comparer);
            var plans = new List<ColumnPlan>();
            var primaryKey = new List<string>();
            string? primaryKeyName = null;
            bool primaryKeyFromIndex = false;
            var uniqueColumns = new List<string>();

            foreach (ParsedColumn column in parsed.Columns)
            {
                if (!columnNames.Add(column.Name))
                {
                    throw Error(column.Line, column.Column, "Table '" + parsed.Name + "' has more than one column called '" + column.Name + "'");
                }
                plans.Add(PlanColumn(parsed, column, primaryKey, uniqueColumns));
            }

            if (primaryKey.Count > 1)
            {
                throw Error(parsed.Line, parsed.Column, "Table '" + parsed.Name + "' marks more than one column as [pk]; use indexes { ("
                    + string.Join(", ", primaryKey) + ") [pk] } for a composite key");
            }

            // The table names its clustered index, because DBML has no clustered setting for an index;
            // clustered_index: false clusters nothing
            string? clusteredName = null;
            bool clusterNothing = false;
            Setting? clusteredSetting = parsed.Settings.Find(s => s.Key == "clustered_index");
            if (clusteredSetting != null)
            {
                if (clusteredSetting.Value.Count == 1 && clusteredSetting.Value[0].IsKeyword("false"))
                {
                    clusterNothing = true;
                }
                else if (clusteredSetting.Value.Count == 1 && clusteredSetting.Value[0].Kind == DbmlTokenKind.String)
                {
                    clusteredName = clusteredSetting.Value[0].Text;
                }
                else
                {
                    // Other DBML tools only allow strings, numbers, colours and booleans in custom settings
                    throw Error(clusteredSetting.Line, clusteredSetting.Column, "clustered_index must be an index name such as 'CIX_"
                        + parsed.Name + "', or false to cluster nothing");
                }
            }
            bool clusteredFound = false;

            var indexes = new List<(string Name, bool Unique, List<ParsedIndexColumn> Columns)>();
            foreach (ParsedIndex index in parsed.Indexes)
            {
                bool pk = false;
                bool unique = false;
                string? name = null;
                foreach (Setting setting in index.Settings)
                {
                    switch (setting.Key)
                    {
                        case "pk":
                        case "primary key":
                            pk = true;
                            break;
                        case "unique":
                            unique = true;
                            break;
                        case "name":
                            name = NameValue(setting, "The index name");
                            break;
                        case "clustered":
                            // Other DBML tools reject unknown index settings, so the table says which index is clustered
                            throw Error(setting.Line, setting.Column, "DBML does not allow 'clustered' on an index; name the index in the table settings instead, as in Table "
                                + parsed.Name + " [clustered_index: 'IX_name']");
                        case "type":
                            string type = NameValue(setting, "The index type");
                            if (!string.Equals(type, "btree", StringComparison.OrdinalIgnoreCase))
                            {
                                throw Error(setting.Line, setting.Column, "Index type '" + type + "' is not supported; SQL Server indexes are btree");
                            }
                            break;
                        default:
                            // Notes and custom properties
                            break;
                    }
                }

                foreach (ParsedIndexColumn column in index.Columns)
                {
                    if (!columnNames.Contains(column.Name))
                    {
                        throw Error(index.Line, index.Column, "An index of table '" + parsed.Name + "' uses column '" + column.Name + "', which the table does not have");
                    }
                }

                if (pk)
                {
                    if (primaryKey.Count > 0 || primaryKeyFromIndex)
                    {
                        throw Error(index.Line, index.Column, "Table '" + parsed.Name + "' has more than one primary key");
                    }
                    if (index.Columns.Exists(c => c.Descending))
                    {
                        throw Error(index.Line, index.Column, "A primary key cannot have a descending column");
                    }
                    primaryKeyFromIndex = true;
                    primaryKeyName = name;
                    primaryKey.AddRange(index.Columns.Select(c => c.Name));
                    continue;
                }

                name ??= "IX_" + parsed.Name + "_" + string.Join("_", index.Columns.Select(c => c.Name));
                if (indexes.Exists(i => SqlName.Equal(i.Name, name)))
                {
                    throw Error(index.Line, index.Column, "Table '" + parsed.Name + "' has more than one index called '" + name + "'");
                }
                var seen = new HashSet<string>(SqlName.Comparer);
                foreach (ParsedIndexColumn column in index.Columns)
                {
                    if (!seen.Add(column.Name))
                    {
                        throw Error(index.Line, index.Column, "Index '" + name + "' lists column '" + column.Name + "' more than once");
                    }
                }
                indexes.Add((name, unique, index.Columns));
            }

            // A [unique] column gets a unique index of its own, unless it is the whole primary key already
            foreach (string column in uniqueColumns)
            {
                if (primaryKey.Count == 1 && SqlName.Equal(primaryKey[0], column))
                {
                    continue;
                }
                string name = "IX_" + parsed.Name + "_" + column;
                int existing = indexes.FindIndex(i => SqlName.Equal(i.Name, name));
                if (existing < 0)
                {
                    indexes.Add((name, true, new List<ParsedIndexColumn> { new ParsedIndexColumn { Name = column } }));
                }
                else if (!indexes[existing].Unique || indexes[existing].Columns.Count != 1 || !SqlName.Equal(indexes[existing].Columns[0].Name, column))
                {
                    throw Error(parsed.Line, parsed.Column, "Column '" + column + "' of table '" + parsed.Name + "' is [unique], but its index name '" + name + "' is already used by a different index");
                }
            }

            DBTable table = schema.AddTable(parsed.Name);
            foreach (ColumnPlan plan in plans)
            {
                // Primary key and identity columns cannot hold NULL, so SQL Server makes them NOT NULL
                bool inPrimaryKey = primaryKey.Exists(c => SqlName.Equal(c, plan.Parsed.Name));
                table.AddColumn(plan.Parsed.Name, DataType(plan), Options(plan, inPrimaryKey));
            }

            if (primaryKey.Count > 0)
            {
                table.PrimaryKey.Name = primaryKeyName ?? "PK_" + parsed.Name;
                foreach (string column in primaryKey)
                {
                    table.PrimaryKey.AddColumn(column);
                }

                // The primary key is clustered unless the table clusters another index, or nothing
                bool clustered = !clusterNothing && (clusteredName == null || SqlName.Equal(clusteredName, table.PrimaryKey.Name));
                table.PrimaryKey.Clustered = clustered;
                clusteredFound |= clusteredName != null && clustered;
            }

            foreach ((string name, bool unique, List<ParsedIndexColumn> columns) in indexes)
            {
                bool clustered = clusteredName != null && SqlName.Equal(clusteredName, name);
                clusteredFound |= clustered;
                DBIndex index = table.AddIndex(name, clustered, unique);
                foreach (ParsedIndexColumn column in columns)
                {
                    index.AddColumn(column.Name, column.Descending);
                }
            }

            if (clusteredName != null && !clusteredFound)
            {
                throw Error(clusteredSetting!.Line, clusteredSetting.Column, "Table '" + parsed.Name + "' has clustered_index '" + clusteredName + "', but no index with that name");
            }
        }

        private static ColumnPlan PlanColumn(ParsedTable table, ParsedColumn column, List<string> primaryKey, List<string> uniqueColumns)
        {
            var plan = new ColumnPlan { Parsed = column };
            string context = "column '" + column.Name + "' of table '" + table.Name + "'";
            plan.Computed = column.Type.StartsWith("AS ", StringComparison.OrdinalIgnoreCase) || column.Type.StartsWith("AS(", StringComparison.OrdinalIgnoreCase);
            Setting? seedSetting = null;

            foreach (Setting setting in column.Settings)
            {
                switch (setting.Key)
                {
                    case "pk":
                    case "primary key":
                        primaryKey.Add(column.Name);
                        break;
                    case "not null":
                        plan.NotNull = true;
                        break;
                    case "null":
                        plan.NotNull = false;
                        break;
                    case "increment":
                        plan.Identity = true;
                        break;
                    case "identity_seed":
                        plan.IdentitySeed = IntegerValue(setting, "identity_seed");
                        seedSetting = setting;
                        break;
                    case "identity_increment":
                        plan.IdentityIncrement = IntegerValue(setting, "identity_increment");
                        seedSetting = setting;
                        break;
                    case "unique":
                        uniqueColumns.Add(column.Name);
                        break;
                    case "default":
                        plan.DefaultSql = DefaultSql(setting, context);
                        break;
                    case "default_name":
                        plan.DefaultName = NameValue(setting, "default_name");
                        break;
                    case "persisted":
                        plan.Persisted = BoolValue(setting, "persisted");
                        break;
                    case "ref":
                        // Already turned into a relationship by AddInlineRefs
                        break;
                    case "check":
                        throw Error(setting.Line, setting.Column, "Check constraints are not supported, but " + context + " has one");
                    default:
                        // Notes and custom properties do not change the database
                        break;
                }
            }

            if (seedSetting != null && !plan.Identity)
            {
                throw Error(seedSetting.Line, seedSetting.Column, "identity_seed and identity_increment need [increment] on " + context);
            }
            if (plan.DefaultName != null && plan.DefaultSql == null)
            {
                throw Error(column.Line, column.Column, "default_name needs a default on " + context);
            }
            if (plan.Computed)
            {
                if (plan.Identity || plan.DefaultSql != null)
                {
                    throw Error(column.Line, column.Column, "Computed " + context + " cannot have increment or a default");
                }
                if (plan.NotNull == true && !plan.Persisted)
                {
                    throw Error(column.Line, column.Column, "Computed " + context + " can only be not null when it is persisted");
                }
            }
            else if (plan.Persisted)
            {
                throw Error(column.Line, column.Column, "Only a computed column, whose type is AS (expression), can be persisted, but " + context + " is not computed");
            }

            return plan;
        }

        /// <summary>The type, with identity written the way the database reads it back: identity(seed,increment) only when not 1,1</summary>
        private static string DataType(ColumnPlan plan)
        {
            string type = plan.Parsed.Type;
            if (!plan.Identity) return type;

            string seed = plan.IdentitySeed ?? "1";
            string increment = plan.IdentityIncrement ?? "1";
            return type + " identity" + (seed != "1" || increment != "1" ? "(" + seed + "," + increment + ")" : "");
        }

        /// <summary>The options the way the database reads them back, as in NOT NULL CONSTRAINT [DF_x] DEFAULT ((0))</summary>
        private static string Options(ColumnPlan plan, bool inPrimaryKey)
        {
            if (plan.Computed)
            {
                // NULL and NOT NULL are only allowed on a persisted computed column
                return plan.Persisted ? (plan.NotNull == true ? "PERSISTED NOT NULL" : "PERSISTED") : "";
            }

            bool notNull = plan.NotNull ?? (inPrimaryKey || plan.Identity);
            string options = notNull ? "NOT NULL" : "NULL";
            if (plan.DefaultSql != null)
            {
                if (plan.DefaultName != null) options += " CONSTRAINT " + SqlName.Quote(plan.DefaultName);
                options += " DEFAULT " + plan.DefaultSql;
            }
            return options;
        }

        private static void BuildForeignKey(DBSchemaDefinition schema, ParsedRef parsed, Func<string, string> resolve)
        {
            string op = parsed.Operator.Replace("?", "");
            Endpoint foreign;
            Endpoint primary;
            switch (op)
            {
                case ">":
                    foreign = parsed.Left;
                    primary = parsed.Right;
                    break;
                case "<":
                    foreign = parsed.Right;
                    primary = parsed.Left;
                    break;
                case "-":
                    // One-to-one: the column with the inline ref, or the second column of a Ref statement, holds the key
                    foreign = parsed.Inline ? parsed.Left : parsed.Right;
                    primary = parsed.Inline ? parsed.Right : parsed.Left;
                    break;
                case "<>":
                    throw Error(parsed.Line, parsed.Column, "Many-to-many relationships (<>) are not supported; add a table that joins the two");
                default:
                    throw Error(parsed.Line, parsed.Column, "'" + parsed.Operator + "' is not a relationship operator; use <, > or -");
            }

            if (foreign.Columns.Count != primary.Columns.Count)
            {
                throw Error(parsed.Line, parsed.Column, "Both sides of a relationship need the same number of columns");
            }

            foreach (Setting setting in parsed.Settings)
            {
                if (setting.Key == "delete" || setting.Key == "update")
                {
                    string action = string.Join(" ", setting.Value.Select(t => t.Text.ToLowerInvariant()));
                    if (action != "no action")
                    {
                        throw Error(setting.Line, setting.Column, "ON " + setting.Key.ToUpperInvariant() + " " + action.ToUpperInvariant()
                            + " is not supported; DBSchema creates foreign keys without referential actions");
                    }
                }
            }

            if (!schema.Tables.TryGetValue(resolve(foreign.Table), out DBTable? foreignTable))
            {
                throw Error(foreign.Line, foreign.Column, "The foreign key is on table '" + foreign.Table + "', which is not defined");
            }
            CheckColumns(foreignTable, foreign);
            string primaryTableName = resolve(primary.Table);
            if (schema.Tables.TryGetValue(primaryTableName, out DBTable? primaryTable))
            {
                CheckColumns(primaryTable, primary);
            }

            string name = parsed.Name ?? "FK_" + foreignTable.Name + "_" + string.Join("_", foreign.Columns);
            if (foreignTable.FindForeignKey(name) != null)
            {
                throw Error(parsed.Line, parsed.Column, "Table '" + foreignTable.Name + "' has more than one foreign key called '" + name + "'");
            }
            DBForeignKey foreignKey = foreignTable.AddForeignKey(name, primaryTableName);
            for (int i = 0; i < foreign.Columns.Count; i++)
            {
                foreignKey.AddColumn(foreign.Columns[i], primary.Columns[i]);
            }
        }

        private static void CheckColumns(DBTable table, Endpoint endpoint)
        {
            foreach (string column in endpoint.Columns)
            {
                if (table.FindColumn(column) == null)
                {
                    throw Error(endpoint.Line, endpoint.Column, "Table '" + table.Name + "' has no column called '" + column + "'");
                }
            }
        }

        private static void BuildRecords(DBTable table, ParsedRecords records)
        {
            List<string> columns = records.Columns ?? table.Columns.Select(c => c.Name).ToList();
            foreach (string column in columns)
            {
                if (table.FindColumn(column) == null)
                {
                    throw Error(records.Line, records.Column, "The records of table '" + table.Name + "' use column '" + column + "', which the table does not have");
                }
            }

            // Example records only illustrate the data, and are never inserted
            if (records.Example)
            {
                return;
            }

            foreach (List<List<DbmlToken>?> row in records.Rows)
            {
                if (row.Count != columns.Count)
                {
                    DbmlToken? first = row.Where(v => v != null).Select(v => v![0]).FirstOrDefault();
                    string where = first != null ? "line " + first.Line.ToString(CultureInfo.InvariantCulture) : "a row";
                    throw Error(records.Line, records.Column, "Records of table '" + table.Name + "', " + where + ": expected "
                        + columns.Count.ToString(CultureInfo.InvariantCulture) + " values, found " + row.Count.ToString(CultureInfo.InvariantCulture));
                }

                DBDefaultRecord record = table.AddDefaultRecord();
                for (int i = 0; i < columns.Count; i++)
                {
                    List<DbmlToken>? value = row[i];
                    // An empty field is NULL, as in other DBML tools
                    if (value == null)
                    {
                        record.AddValue(columns[i], DBNull.Value);
                    }
                    // `DEFAULT` leaves the column out of the insert, so the database fills in its default,
                    // the same as INSERT ... VALUES (DEFAULT) in the SQL other DBML tools generate
                    else if (!IsDefaultKeyword(value))
                    {
                        record.AddValue(columns[i], RecordValue(value, "column '" + columns[i] + "' of table '" + table.Name + "'"));
                    }
                }
            }
        }

        #endregion

        #region Values

        /// <summary>
        /// Turn a DBML default into the SQL that follows DEFAULT, wrapped the way SQL Server reports it
        /// (numbers in two sets of parentheses, everything else in one), so a schema read back from
        /// the database matches
        /// </summary>
        private static string DefaultSql(Setting setting, string context)
        {
            List<DbmlToken> value = setting.Value;
            if (value.Count == 1)
            {
                DbmlToken token = value[0];
                switch (token.Kind)
                {
                    case DbmlTokenKind.Number:
                        return "((" + token.Text + "))";
                    case DbmlTokenKind.String:
                        return "('" + token.Text.Replace("'", "''") + "')";
                    case DbmlTokenKind.Expression:
                        return "(" + token.Text + ")";
                    case DbmlTokenKind.Identifier:
                        if (token.IsKeyword("true")) return "((1))";
                        if (token.IsKeyword("false")) return "((0))";
                        if (token.IsKeyword("null")) return "(NULL)";
                        break;
                }
            }
            else if (IsNegativeNumber(value))
            {
                return "((-" + value[1].Text + "))";
            }

            throw Error(setting.Line, setting.Column, "The default of " + context + " must be a number, a 'string', true, false, null or a `expression`");
        }

        private static bool IsNegativeNumber(List<DbmlToken> value) =>
            value.Count == 2 && value[0].Kind == DbmlTokenKind.Operator && value[0].Text == "-" && value[1].Kind == DbmlTokenKind.Number;

        private static object RecordValue(List<DbmlToken> value, string context)
        {
            DbmlToken token = value[0];
            bool negative = false;
            if (IsNegativeNumber(value))
            {
                negative = true;
                token = value[1];
            }
            else if (value.Count != 1)
            {
                throw Error(token, "The value of " + context + " must be a number, a 'string', true, false or null");
            }

            switch (token.Kind)
            {
                case DbmlTokenKind.String:
                    return token.Text;
                case DbmlTokenKind.Number:
                    return Number((negative ? "-" : "") + token.Text);
                case DbmlTokenKind.Identifier:
                    if (token.IsKeyword("true")) return true;
                    if (token.IsKeyword("false")) return false;
                    if (token.IsKeyword("null")) return DBNull.Value;
                    break;
                case DbmlTokenKind.Expression:
                    throw Error(token, "Expressions such as `" + token.Text + "` cannot be inserted as default records, because records are inserted as parameters; only `DEFAULT` is allowed");
            }

            throw Error(token, "The value of " + context + " must be a number, a 'string', true, false or null, found " + token.Describe());
        }

        private static bool IsDefaultKeyword(List<DbmlToken> value) =>
            value.Count == 1 && value[0].Kind == DbmlTokenKind.Expression
            && string.Equals(value[0].Text.Trim(), "DEFAULT", StringComparison.OrdinalIgnoreCase);

        /// <summary>Numbers get the same types the JSON reader gives them</summary>
        private static object Number(string text)
        {
            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int intValue)) return intValue;
            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long longValue)) return longValue;
            if (!text.Contains('e') && !text.Contains('E')
                && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal decimalValue))
            {
                return decimalValue;
            }
            return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        /// <summary>A whole number, possibly negative, as identity seeds and increments are</summary>
        private static string IntegerValue(Setting setting, string what)
        {
            List<DbmlToken> value = setting.Value;
            DbmlToken? number = value.Count == 1 ? value[0] : IsNegativeNumber(value) ? value[1] : null;
            if (number != null && number.Kind == DbmlTokenKind.Number && number.Text.All(char.IsDigit))
            {
                return (value.Count == 2 ? "-" : "") + number.Text;
            }
            throw Error(setting.Line, setting.Column, "'" + what + "' must be a whole number");
        }

        /// <summary>A true or false custom setting; other DBML tools need the value, so a bare name is not enough</summary>
        private static bool BoolValue(Setting setting, string what)
        {
            if (setting.Value.Count == 1)
            {
                if (setting.Value[0].IsKeyword("true")) return true;
                if (setting.Value[0].IsKeyword("false")) return false;
            }
            throw Error(setting.Line, setting.Column, "'" + what + "' must be true or false, as in " + what + ": true");
        }

        private static string NameValue(Setting setting, string what)
        {
            if (setting.Value.Count == 1 && (setting.Value[0].Kind == DbmlTokenKind.String || setting.Value[0].IsName))
            {
                return setting.Value[0].Text;
            }
            throw Error(setting.Line, setting.Column, what + " must be a 'string'");
        }

        #endregion
    }
}
