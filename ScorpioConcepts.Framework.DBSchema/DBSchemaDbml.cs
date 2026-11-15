using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ScorpioConcepts.Framework.DBSchema
{
    /// <summary>
    /// Reads and writes schema files in DBML (https://dbml.dbdiagram.io), the language used by dbdiagram.io.
    /// </summary>
    /// <remarks>
    /// A schema file looks like this:
    /// <code>
    /// Table Customers {
    ///   Id int [pk, increment, not null]
    ///   Name varchar(50) [not null, default: '']
    ///   Email nvarchar(100)
    ///
    ///   indexes {
    ///     (Name, Email [desc]) [name: 'IX_Customers_Name_Email', unique]
    ///   }
    ///
    ///   records (Id, Name) {
    ///     1, 'Example'
    ///   }
    /// }
    ///
    /// Ref FK_Orders_Customers: Orders.CustomerId > Customers.Id
    /// </code>
    /// Only the parts of DBML that describe a SQL Server table structure are used:
    /// <list type="bullet">
    /// <item>Names DBML leaves optional get generated: PK_table for a primary key, IX_table_columns for an index
    /// (including the unique index made for a [unique] column) and FK_table_columns for a foreign key.</item>
    /// <item>DBML has no clustered setting for an index (other tools reject unknown index settings), so a table names its
    /// clustered index in a custom property: Table Orders [clustered_index: 'CIX_Orders'] { ... }.</item>
    /// <item>Defaults are written as plain DBML values where possible (default: 0, default: 'text'), and as
    /// `expressions` otherwise. Reading adds the parentheses SQL Server reports, so DEFAULT ((0)).</item>
    /// <item>In records, null or an empty field inserts NULL, and `DEFAULT` leaves the column out of the inserted row,
    /// so it gets the column's default.</item>
    /// <item>Elements that only affect a diagram, such as Project, Note and TableGroup, are ignored. Enums, checks,
    /// TablePartial, other schemas, many-to-many refs and referential actions cannot be created by DBSchema, so they
    /// are rejected instead of being silently dropped.</item>
    /// </list>
    /// </remarks>
    internal static class DBSchemaDbml
    {
        private static readonly Regex PlainName = new Regex(@"^[\p{L}_][\p{L}\p{Nd}_]*$");
        private static readonly Regex PlainType = new Regex(@"^[\p{L}_][\p{L}\p{Nd}_]*(\([\p{L}\p{Nd}_]+(,[\p{L}\p{Nd}_]+)*\))?$");
        private static readonly Regex PlainNumber = new Regex(@"^-?[0-9]+(\.[0-9]+)?$");
        private static readonly Regex SqlString = new Regex(@"^'((?:[^']|'')*)'$", RegexOptions.Singleline);

        /// <summary>Names that mean something in a table body, so they are quoted to keep them names</summary>
        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "as", "checks", "enum", "indexes", "note", "project", "records", "ref", "table", "tablegroup", "tablepartial",
        };

        /// <summary>
        /// Write the given Database Schema, along with the given default records, to a DBML file
        /// </summary>
        /// <exception cref="NotSupportedException">A column's options cannot be expressed in DBML</exception>
        internal static void Write(DBSchema dbSchema, string targetFile, DBDefaultRecords defaultRecords)
        {
            DBSchema.DebugLog.Add("Found " + dbSchema.Tables.Count.ToString(CultureInfo.InvariantCulture) + " Tables...");

            // Build the whole file first, so a table that cannot be written leaves no half-written file behind
            StringBuilder dbml = new StringBuilder();
            foreach (DBTable table in dbSchema.Tables.Values)
            {
                DBSchema.DebugLog.Add("DBSchema DBML for table '" + table.Name + "'...");
                if (dbml.Length > 0)
                {
                    dbml.AppendLine();
                }
                List<DBDefaultRecord> records;
                defaultRecords.TryGetValue(table.Name, out records);
                WriteTable(dbml, table, records);
                DBSchema.DebugLog.Add("DB Schema DBML generated for table '" + table.Name + "'");
            }

            bool firstRef = true;
            foreach (DBTable table in dbSchema.Tables.Values)
            {
                foreach (IGrouping<string, DBTable.ForeignKey> fkey in table.ForeignKeys.GroupBy(f => f.Name))
                {
                    if (firstRef)
                    {
                        dbml.AppendLine();
                        firstRef = false;
                    }
                    WriteRef(dbml, table, fkey.Key, fkey.ToList());
                }
            }

            File.WriteAllText(targetFile, dbml.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// Read a schema from a DBML file
        /// </summary>
        /// <exception cref="FormatException">The file is not valid DBML, or uses something DBSchema cannot create</exception>
        internal static DBSchema Read(string schemaDbmlFile)
        {
            return DbmlReader.Parse(File.ReadAllText(schemaDbmlFile));
        }

        private static void WriteTable(StringBuilder dbml, DBTable table, List<DBDefaultRecord> records)
        {
            // A single-column key with the usual name is written on the column, the DBML way
            bool inlineKey = (table.PrimaryKey.KeyColumns.Count == 1)
                && ((table.PrimaryKey.Name == "PK_" + table.Name) || string.IsNullOrEmpty(table.PrimaryKey.Name))
                && (table.FindColumn(table.PrimaryKey.KeyColumns[0]) != null);

            // DBML has no clustered setting for an index, so the table names its clustered index in a custom property
            List<DBTable.Index> clustered = table.Indexes.Where(i => i.Clustered).ToList();
            if (clustered.Count > 1)
            {
                throw new NotSupportedException("Table '" + table.Name + "' has more than one clustered index, which SQL Server does not allow");
            }

            dbml.Append("Table ").Append(Name(table.Name));
            if (clustered.Count == 1)
            {
                dbml.Append(" [clustered_index: ").Append(StringLiteral(clustered[0].Name)).Append(']');
            }
            dbml.AppendLine(" {");
            foreach (DBTable.Column column in table.Columns)
            {
                List<string> settings = new List<string>();
                if (inlineKey && (column.Name == table.PrimaryKey.KeyColumns[0]))
                {
                    settings.Add("pk");
                }

                string type = column.DataType;
                const string identity = " identity";
                bool isIdentity = type.EndsWith(identity, StringComparison.OrdinalIgnoreCase);
                if (isIdentity)
                {
                    type = type.Substring(0, type.Length - identity.Length).TrimEnd();
                    settings.Add("increment");
                }

                bool notNull;
                string defaultSql;
                ParseOptions(table, column, out notNull, out defaultSql);
                if (notNull)
                {
                    settings.Add("not null");
                }
                else if (isIdentity || table.PrimaryKey.KeyColumns.Contains(column.Name))
                {
                    // Reading assumes key and identity columns are NOT NULL unless told otherwise
                    settings.Add("null");
                }
                if (defaultSql != null)
                {
                    settings.Add("default: " + DefaultValue(table, column, defaultSql));
                }

                dbml.Append("  ").Append(Name(column.Name)).Append(' ').Append(PlainType.IsMatch(type) ? type : Quote(type));
                if (settings.Count > 0)
                {
                    dbml.Append(" [").Append(string.Join(", ", settings.ToArray())).Append(']');
                }
                dbml.AppendLine();
            }

            List<string> indexes = new List<string>();
            if ((table.PrimaryKey.KeyColumns.Count > 0) && !inlineKey)
            {
                string settings = "pk";
                if (!string.IsNullOrEmpty(table.PrimaryKey.Name) && (table.PrimaryKey.Name != "PK_" + table.Name))
                {
                    settings += ", name: " + StringLiteral(table.PrimaryKey.Name);
                }
                indexes.Add("(" + string.Join(", ", table.PrimaryKey.KeyColumns.Select(Name).ToArray()) + ") [" + settings + "]");
            }
            foreach (DBTable.Index index in table.Indexes)
            {
                string columns = string.Join(", ", index.Columns.Select(c => Name(c.Key) + (c.Value ? " [desc]" : "")).ToArray());
                string settings = "name: " + StringLiteral(index.Name);
                if (index.Unique)
                {
                    settings += ", unique";
                }
                indexes.Add("(" + columns + ") [" + settings + "]");
            }
            if (indexes.Count > 0)
            {
                dbml.AppendLine();
                dbml.AppendLine("  indexes {");
                foreach (string index in indexes)
                {
                    dbml.Append("    ").AppendLine(index);
                }
                dbml.AppendLine("  }");
            }

            if ((records != null) && (records.Count > 0))
            {
                DBSchema.DebugLog.Add("Saving Default Records for table '" + table.Name + "'...");
                WriteRecords(dbml, records);
            }

            dbml.AppendLine("}");
        }

        /// <summary>
        /// DBML has one column list per records block, so it holds every column any record uses;
        /// a record without a value for one of them gets `DEFAULT`, which reads back as no value
        /// </summary>
        private static void WriteRecords(StringBuilder dbml, List<DBDefaultRecord> records)
        {
            List<string> columns = new List<string>();
            foreach (DBDefaultRecord record in records)
            {
                foreach (string column in record.Values.Keys)
                {
                    if (!columns.Contains(column))
                    {
                        columns.Add(column);
                    }
                }
            }

            dbml.AppendLine();
            dbml.Append("  records (").Append(string.Join(", ", columns.Select(Name).ToArray())).AppendLine(") {");
            foreach (DBDefaultRecord record in records)
            {
                List<string> values = new List<string>();
                foreach (string column in columns)
                {
                    object value;
                    values.Add(record.Values.TryGetValue(column, out value) ? RecordValue(value) : "`DEFAULT`");
                }
                dbml.Append("    ").AppendLine(string.Join(", ", values.ToArray()));
            }
            dbml.AppendLine("  }");
        }

        private static string RecordValue(object value)
        {
            if ((value == null) || (value is DBNull))
            {
                return "null";
            }
            if (value is string)
            {
                return StringLiteral((string)value);
            }
            if (value is bool)
            {
                return ((bool)value) ? "true" : "false";
            }
            if ((value is int) || (value is long) || (value is short) || (value is byte) || (value is sbyte)
                || (value is ushort) || (value is uint) || (value is ulong) || (value is decimal))
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            if (value is double)
            {
                return ((double)value).ToString("R", CultureInfo.InvariantCulture);
            }
            if (value is float)
            {
                return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            }
            // ISO 8601 without trailing zero fractions, the same as the JSON writer
            if (value is DateTime)
            {
                return StringLiteral(((DateTime)value).ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture));
            }
            if (value is DateTimeOffset)
            {
                return StringLiteral(((DateTimeOffset)value).ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture));
            }
            return StringLiteral(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void WriteRef(StringBuilder dbml, DBTable table, string name, List<DBTable.ForeignKey> columns)
        {
            string referencedTable = columns[0].PKeyTable;
            if (columns.Any(c => c.PKeyTable != referencedTable))
            {
                throw new NotSupportedException("Foreign key '" + name + "' of table '" + table.Name + "' references more than one table, which DBML cannot express");
            }

            dbml.Append("Ref ").Append(Name(name)).Append(": ")
                .Append(Endpoint(table.Name, columns.Select(c => c.Column).ToList()))
                .Append(" > ")
                .Append(Endpoint(referencedTable, columns.Select(c => c.PKeyColumn).ToList()))
                .AppendLine();
        }

        private static string Endpoint(string table, List<string> columns)
        {
            if (columns.Count == 1)
            {
                return Name(table) + "." + Name(columns[0]);
            }
            return Name(table) + ".(" + string.Join(", ", columns.Select(Name).ToArray()) + ")";
        }

        /// <summary>
        /// Split column options such as "NOT NULL DEFAULT ((0))" into the null setting and the default
        /// </summary>
        private static void ParseOptions(DBTable table, DBTable.Column column, out bool notNull, out string defaultSql)
        {
            string options = (column.Options ?? "").Trim();
            notNull = false;

            Match nullability = Regex.Match(options, @"^(NOT\s+NULL|NULL)(\s+|$)", RegexOptions.IgnoreCase);
            if (!nullability.Success)
            {
                // Also accept the nullability at the end, as in DEFAULT 0 NOT NULL, but not the NULL of DEFAULT NULL
                nullability = Regex.Match(options, @"(?<!DEFAULT\s*)\s+(NOT\s+NULL|NULL)$", RegexOptions.IgnoreCase);
            }
            if (nullability.Success)
            {
                notNull = nullability.Value.Trim().StartsWith("NOT", StringComparison.OrdinalIgnoreCase);
                options = options.Remove(nullability.Index, nullability.Length).Trim();
            }

            defaultSql = null;
            if (options.Length == 0)
            {
                return;
            }

            Match defaultMatch = Regex.Match(options, @"^DEFAULT\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!defaultMatch.Success)
            {
                throw new NotSupportedException("The options '" + column.Options + "' of column '" + column.Name + "' in table '" + table.Name
                    + "' cannot be written as DBML, which only has NULL, NOT NULL and DEFAULT");
            }
            defaultSql = defaultMatch.Groups[1].Value.Trim();
        }

        /// <summary>
        /// Write a SQL default, such as ((0)), ('text') or (getdate()), as a DBML value: a plain number or
        /// string where it is one, and an `expression` otherwise
        /// </summary>
        private static string DefaultValue(DBTable table, DBTable.Column column, string defaultSql)
        {
            string value = StripParentheses(defaultSql);
            if (PlainNumber.IsMatch(value))
            {
                return value;
            }
            Match text = SqlString.Match(value);
            if (text.Success)
            {
                return StringLiteral(text.Groups[1].Value.Replace("''", "'"));
            }
            if (string.Equals(value, "NULL", StringComparison.OrdinalIgnoreCase))
            {
                return "null";
            }
            if (value.Contains('`'))
            {
                throw new NotSupportedException("The default of column '" + column.Name + "' in table '" + table.Name + "' contains a backtick, which a DBML expression cannot hold");
            }
            // This includes N'text', so the N survives a round trip
            return "`" + value + "`";
        }

        /// <summary>Remove parentheses that wrap the whole expression, as in ((0)) or (getdate())</summary>
        private static string StripParentheses(string sql)
        {
            string value = sql.Trim();
            while ((value.Length >= 2) && (value[0] == '(') && (MatchingParenthesis(value) == value.Length - 1))
            {
                value = value.Substring(1, value.Length - 2).Trim();
            }
            return value;
        }

        /// <summary>The position of the ')' that closes the '(' at the start, skipping over 'strings'</summary>
        private static int MatchingParenthesis(string sql)
        {
            int depth = 0;
            bool inString = false;
            for (int i = 0; i < sql.Length; i++)
            {
                char c = sql[i];
                if (c == '\'')
                {
                    inString = !inString;
                }
                else if (!inString && (c == '('))
                {
                    depth++;
                }
                else if (!inString && (c == ')'))
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }
            return -1;
        }

        private static string Name(string name)
        {
            return (PlainName.IsMatch(name) && !Keywords.Contains(name)) ? name : Quote(name);
        }

        private static string Quote(string name)
        {
            return "\"" + Escape(name).Replace("\"", "\\\"") + "\"";
        }

        private static string StringLiteral(string value)
        {
            return "'" + Escape(value).Replace("'", "\\'") + "'";
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }
    }
}
