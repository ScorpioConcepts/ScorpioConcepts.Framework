using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ScorpioConcepts.Framework.DBSchema;

/// <summary>
/// Reads and writes schema files in DBML (https://dbml.dbdiagram.io), the language used by dbdiagram.io.
/// </summary>
/// <remarks>
/// A schema file looks like this:
/// <code>
/// Table Orders [clustered_index: 'CIX_Orders_PlacedOn'] {
///   Id int [pk, increment, not null]
///   CustomerId int [not null]
///   PlacedOn datetime2(3) [not null, default: `getdate()`, default_name: 'DF_Orders_PlacedOn']
///   Total "AS ([Qty]*[Price])" [persisted: true]
///
///   indexes {
///     (PlacedOn [desc]) [name: 'CIX_Orders_PlacedOn']
///   }
///
///   records (CustomerId, PlacedOn) {
///     1, `DEFAULT`
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
/// clustered index in a custom property, clustered_index. The primary key is clustered unless that names another index,
/// or is false to cluster nothing.</item>
/// <item>Defaults are written as plain DBML values where possible (default: 0, default: 'text'), and as
/// `expressions` otherwise. Reading adds the parentheses SQL Server reports, so DEFAULT ((0)). A named default
/// constraint has default_name.</item>
/// <item>Identity seeds and increments other than 1 are the column properties identity_seed and identity_increment.</item>
/// <item>A computed column has its AS (expression) as a quoted type, and persisted: true if it is stored.</item>
/// <item>In records, null or an empty field inserts NULL, and `DEFAULT` leaves the column out of the inserted row,
/// so it gets the column's default.</item>
/// <item>Elements that only affect a diagram, such as Project, Note and TableGroup, are ignored. Enums, checks,
/// TablePartial, other schemas, many-to-many refs and referential actions cannot be created by DBSchema, so they
/// are rejected instead of being silently dropped.</item>
/// </list>
/// </remarks>
internal static class DBSchemaDbml
{
    private static readonly Regex PlainName = new(@"^[\p{L}_][\p{L}\p{Nd}_]*$");
    private static readonly Regex PlainType = new(@"^[\p{L}_][\p{L}\p{Nd}_]*(\([\p{L}\p{Nd}_]+(,[\p{L}\p{Nd}_]+)*\))?$");
    private static readonly Regex PlainNumber = new(@"^-?[0-9]+(\.[0-9]+)?$");
    private static readonly Regex SqlString = new(@"^'((?:[^']|'')*)'$", RegexOptions.Singleline);
    private static readonly Regex Identity = new(@"^(.*?)\s+identity(?:\s*\(\s*(-?[0-9]+)\s*,\s*(-?[0-9]+)\s*\))?$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex LeadingNullability = new(@"^(NOT\s+NULL|NULL)(\s+|$)", RegexOptions.IgnoreCase);
    // Also accept the nullability at the end, as in DEFAULT 0 NOT NULL, but not the NULL of DEFAULT NULL
    private static readonly Regex TrailingNullability = new(@"(?<!DEFAULT\s*)\s+(NOT\s+NULL|NULL)$", RegexOptions.IgnoreCase);
    private static readonly Regex Default = new(@"^(?:CONSTRAINT\s+(\[(?:[^\]]|\]\])+\]|[^\s\[]+)\s+)?DEFAULT\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>Names that mean something in a table body, so they are quoted to keep them names</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "as", "checks", "enum", "indexes", "note", "project", "records", "ref", "table", "tablegroup", "tablepartial",
    };

    /// <summary>
    /// Write the given schema, including each table's default records, to a DBML file
    /// </summary>
    /// <exception cref="NotSupportedException">Something in the schema cannot be expressed in DBML</exception>
    internal static void Write(DBSchemaDefinition schema, string targetFile)
    {
        // Build the whole file first, so a table that cannot be written leaves no half-written file behind
        var dbml = new StringBuilder();
        foreach (DBTable table in schema.TablesInOrder)
        {
            if (dbml.Length > 0)
            {
                dbml.AppendLine();
            }
            WriteTable(dbml, table);
        }

        bool firstRef = true;
        foreach (DBTable table in schema.TablesInOrder)
        {
            foreach (DBForeignKey foreignKey in table.ForeignKeys)
            {
                if (firstRef)
                {
                    dbml.AppendLine();
                    firstRef = false;
                }
                dbml.Append("Ref ").Append(Name(foreignKey.Name)).Append(": ")
                    .Append(Endpoint(table.Name, foreignKey.Columns))
                    .Append(" > ")
                    .Append(Endpoint(foreignKey.ReferencedTable, foreignKey.ReferencedColumns))
                    .AppendLine();
            }
        }

        File.WriteAllText(targetFile, dbml.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// Read a schema from a DBML file
    /// </summary>
    /// <exception cref="FormatException">The file is not valid DBML, or uses something DBSchema cannot create</exception>
    internal static DBSchemaDefinition Read(string schemaDbmlFile) => DbmlReader.Parse(File.ReadAllText(schemaDbmlFile));

    private static void WriteTable(StringBuilder dbml, DBTable table)
    {
        DBPrimaryKey primaryKey = table.PrimaryKey;
        bool hasPrimaryKey = primaryKey.Columns.Count > 0;

        // A single-column key with the usual name is written on the column, the DBML way
        bool inlineKey = primaryKey.Columns.Count == 1
            && (primaryKey.Name == "PK_" + table.Name || string.IsNullOrEmpty(primaryKey.Name))
            && table.FindColumn(primaryKey.Columns[0]) != null;

        dbml.Append("Table ").Append(Name(table.Name)).Append(ClusteredIndexSetting(table)).AppendLine(" {");
        foreach (DBColumn column in table.Columns)
        {
            bool inPrimaryKey = primaryKey.Columns.Any(c => SqlName.Equal(c, column.Name));
            var settings = new List<string>();
            if (inlineKey && inPrimaryKey)
            {
                settings.Add("pk");
            }

            string type = column.IsComputed ? ComputedType(table, column, settings) : ColumnType(table, column, settings, inPrimaryKey);
            dbml.Append("  ").Append(Name(column.Name)).Append(' ').Append(PlainType.IsMatch(type) ? type : Quote(type));
            if (settings.Count > 0)
            {
                dbml.Append(" [").Append(string.Join(", ", settings)).Append(']');
            }
            dbml.AppendLine();
        }

        var indexes = new List<string>();
        if (hasPrimaryKey && !inlineKey)
        {
            string settings = "pk";
            if (!string.IsNullOrEmpty(primaryKey.Name) && primaryKey.Name != "PK_" + table.Name)
            {
                settings += ", name: " + StringLiteral(primaryKey.Name!);
            }
            indexes.Add("(" + string.Join(", ", primaryKey.Columns.Select(Name)) + ") [" + settings + "]");
        }
        foreach (DBIndex index in table.Indexes)
        {
            string columns = string.Join(", ", index.Columns.Select(c => Name(c.Name) + (c.Descending ? " [desc]" : "")));
            indexes.Add("(" + columns + ") [name: " + StringLiteral(index.Name) + (index.Unique ? ", unique" : "") + "]");
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

        if (table.DefaultRecords.Count > 0)
        {
            WriteRecords(dbml, table.DefaultRecords);
        }

        dbml.AppendLine("}");
    }

    /// <summary>
    /// DBML has no clustered setting for an index, so the table names its clustered index in a custom property.
    /// Nothing is written for the usual case, a clustered primary key; false means nothing is clustered.
    /// </summary>
    private static string ClusteredIndexSetting(DBTable table)
    {
        List<DBIndex> clustered = table.Indexes.Where(i => i.Clustered).ToList();
        bool hasPrimaryKey = table.PrimaryKey.Columns.Count > 0;
        if (clustered.Count > 1 || (clustered.Count == 1 && hasPrimaryKey && table.PrimaryKey.Clustered))
        {
            throw new NotSupportedException("Table '" + table.Name + "' has more than one clustered index, which SQL Server does not allow");
        }

        if (clustered.Count == 1) return " [clustered_index: " + StringLiteral(clustered[0].Name) + "]";
        if (hasPrimaryKey && !table.PrimaryKey.Clustered) return " [clustered_index: false]";
        return "";
    }

    /// <summary>
    /// A computed column's AS (expression) becomes its (quoted) type, which other DBML tools pass through to SQL as it is.
    /// Its options can only be empty, PERSISTED or PERSISTED NOT NULL.
    /// </summary>
    private static string ComputedType(DBTable table, DBColumn column, List<string> settings)
    {
        switch (column.Options.Trim().ToUpperInvariant())
        {
            case "":
                break;
            // Other DBML tools need a value on a custom setting, so it is persisted: true rather than a bare persisted
            case "PERSISTED":
                settings.Add("persisted: true");
                break;
            case "PERSISTED NOT NULL":
                settings.Add("not null");
                settings.Add("persisted: true");
                break;
            default:
                throw new NotSupportedException("The options '" + column.Options + "' of computed column '" + column.Name + "' in table '" + table.Name
                    + "' cannot be written as DBML, which only has PERSISTED and PERSISTED NOT NULL for a computed column");
        }
        return column.DataType;
    }

    /// <summary>The DBML type of an ordinary column, adding its settings for identity, nullability and default</summary>
    private static string ColumnType(DBTable table, DBColumn column, List<string> settings, bool inPrimaryKey)
    {
        string type = column.DataType;
        Match identity = Identity.Match(type);
        if (identity.Success)
        {
            type = identity.Groups[1].Value;
            settings.Add("increment");
            if (identity.Groups[2].Success && (identity.Groups[2].Value != "1" || identity.Groups[3].Value != "1"))
            {
                settings.Add("identity_seed: " + identity.Groups[2].Value);
                settings.Add("identity_increment: " + identity.Groups[3].Value);
            }
        }

        ParseOptions(table, column, out bool notNull, out string? defaultName, out string? defaultSql);
        if (notNull)
        {
            settings.Add("not null");
        }
        else if (identity.Success || inPrimaryKey)
        {
            // Reading assumes key and identity columns are NOT NULL unless told otherwise
            settings.Add("null");
        }
        if (defaultSql != null)
        {
            settings.Add("default: " + DefaultValue(table, column, defaultSql));
            if (defaultName != null)
            {
                settings.Add("default_name: " + StringLiteral(defaultName));
            }
        }
        return type;
    }

    /// <summary>
    /// DBML has one column list per records block, so it holds every column any record uses;
    /// a record without a value for one of them gets `DEFAULT`, which reads back as no value
    /// </summary>
    private static void WriteRecords(StringBuilder dbml, IReadOnlyList<DBDefaultRecord> records)
    {
        var columns = new List<string>();
        foreach (DBDefaultRecord record in records)
        {
            foreach (string column in record.Columns)
            {
                if (!columns.Exists(c => SqlName.Equal(c, column)))
                {
                    columns.Add(column);
                }
            }
        }

        dbml.AppendLine();
        dbml.Append("  records (").Append(string.Join(", ", columns.Select(Name))).AppendLine(") {");
        foreach (DBDefaultRecord record in records)
        {
            IEnumerable<string> values = columns.Select(c => record.Values.TryGetValue(c, out object? value) ? RecordValue(value) : "`DEFAULT`");
            dbml.Append("    ").AppendLine(string.Join(", ", values));
        }
        dbml.AppendLine("  }");
    }

    private static string RecordValue(object? value)
    {
        switch (value)
        {
            case null:
            case DBNull:
                return "null";
            case string text:
                return StringLiteral(text);
            case bool flag:
                return flag ? "true" : "false";
            case int or long or short or byte or sbyte or ushort or uint or ulong or decimal:
                return Convert.ToString(value, CultureInfo.InvariantCulture)!;
            case double number:
                return number.ToString("R", CultureInfo.InvariantCulture);
            case float number:
                return number.ToString("R", CultureInfo.InvariantCulture);
            // ISO 8601 without trailing zero fractions, the same as the JSON writer
            case DateTime date:
                return StringLiteral(date.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture));
            case DateTimeOffset date:
                return StringLiteral(date.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture));
            default:
                return StringLiteral(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
        }
    }

    private static string Endpoint(string table, IReadOnlyList<string> columns) =>
        columns.Count == 1
            ? Name(table) + "." + Name(columns[0])
            : Name(table) + ".(" + string.Join(", ", columns.Select(Name)) + ")";

    /// <summary>
    /// Split column options such as "NOT NULL CONSTRAINT [DF_x] DEFAULT ((0))" into the null setting, the default's
    /// name and the default
    /// </summary>
    private static void ParseOptions(DBTable table, DBColumn column, out bool notNull, out string? defaultName, out string? defaultSql)
    {
        string options = column.Options.Trim();
        notNull = false;
        defaultName = null;
        defaultSql = null;

        Match nullability = LeadingNullability.Match(options);
        if (!nullability.Success)
        {
            nullability = TrailingNullability.Match(options);
        }
        if (nullability.Success)
        {
            notNull = nullability.Value.Trim().StartsWith("NOT", StringComparison.OrdinalIgnoreCase);
            options = options.Remove(nullability.Index, nullability.Length).Trim();
        }

        if (options.Length == 0)
        {
            return;
        }

        Match defaultMatch = Default.Match(options);
        if (!defaultMatch.Success)
        {
            throw new NotSupportedException("The options '" + column.Options + "' of column '" + column.Name + "' in table '" + table.Name
                + "' cannot be written as DBML, which only has NULL, NOT NULL and DEFAULT");
        }
        if (defaultMatch.Groups[1].Success)
        {
            string name = defaultMatch.Groups[1].Value;
            defaultName = name.StartsWith("[", StringComparison.Ordinal) ? name.Substring(1, name.Length - 2).Replace("]]", "]") : name;
        }
        defaultSql = defaultMatch.Groups[2].Value.Trim();
    }

    /// <summary>
    /// Write a SQL default, such as ((0)), ('text') or (getdate()), as a DBML value: a plain number or
    /// string where it is one, and an `expression` otherwise
    /// </summary>
    private static string DefaultValue(DBTable table, DBColumn column, string defaultSql)
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
        while (value.Length >= 2 && value[0] == '(' && MatchingParenthesis(value) == value.Length - 1)
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
            else if (!inString && c == '(')
            {
                depth++;
            }
            else if (!inString && c == ')')
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

    private static string Name(string name) => PlainName.IsMatch(name) && !Keywords.Contains(name) ? name : Quote(name);

    private static string Quote(string name) => "\"" + Escape(name).Replace("\"", "\\\"") + "\"";

    private static string StringLiteral(string value) => "'" + Escape(value).Replace("'", "\\'") + "'";

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
