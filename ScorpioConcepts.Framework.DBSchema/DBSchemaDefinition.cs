using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;

namespace ScorpioConcepts.Framework.DBSchema;

/// <summary>
/// The structure of a whole database: its tables, as read from a schema file or from the database.
/// </summary>
public sealed class DBSchemaDefinition
{
    private readonly Dictionary<string, DBTable> _tables = new(SqlName.Comparer);
    private readonly List<DBTable> _order = new();

    internal DBSchemaDefinition()
    {
        Tables = new ReadOnlyDictionary<string, DBTable>(_tables);
        TablesInOrder = _order.AsReadOnly();
    }

    /// <summary>The tables by name. Like SQL Server, the names are not case sensitive.</summary>
    public IReadOnlyDictionary<string, DBTable> Tables { get; }

    /// <summary>The tables in the order they were read.</summary>
    public IReadOnlyList<DBTable> TablesInOrder { get; }

    internal DBTable AddTable(string tableName)
    {
        if (_tables.ContainsKey(tableName))
        {
            throw new ArgumentException("Table '" + tableName + "' already exists in the schema", nameof(tableName));
        }

        var table = new DBTable(tableName);
        _tables.Add(tableName, table);
        _order.Add(table);
        return table;
    }

    /// <summary>
    /// Reads a schema file. Whether it is JSON, XML or DBML is worked out from how it starts.
    /// </summary>
    /// <param name="schemaFile">The path of the schema file.</param>
    /// <exception cref="FormatException">The file is not a valid schema file.</exception>
    public static DBSchemaDefinition Load(string schemaFile) => Load(schemaFile, DetectFormat(schemaFile));

    /// <summary>Reads a schema file in the given format.</summary>
    /// <param name="schemaFile">The path of the schema file.</param>
    /// <param name="format">The format of the file.</param>
    /// <exception cref="FormatException">The file is not a valid schema file.</exception>
    public static DBSchemaDefinition Load(string schemaFile, DBSchemaFormat format)
    {
        if (schemaFile is null) throw new ArgumentNullException(nameof(schemaFile));

        return format switch
        {
            DBSchemaFormat.Json => DBSchemaJson.Read(schemaFile),
            DBSchemaFormat.Xml => DBSchemaXml.Read(schemaFile),
            DBSchemaFormat.Dbml => DBSchemaDbml.Read(schemaFile),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown schema format"),
        };
    }

    /// <summary>
    /// Writes this schema to a file, including each table's <see cref="DBTable.DefaultRecords"/>.
    /// </summary>
    /// <param name="targetFile">The path of the file to write. An existing file is replaced.</param>
    /// <param name="format">The format to write. JSON is the default.</param>
    public void Save(string targetFile, DBSchemaFormat format = DBSchemaFormat.Json)
    {
        if (targetFile is null) throw new ArgumentNullException(nameof(targetFile));

        switch (format)
        {
            case DBSchemaFormat.Json:
                DBSchemaJson.Write(this, targetFile);
                break;
            case DBSchemaFormat.Xml:
                DBSchemaXml.Write(this, targetFile);
                break;
            case DBSchemaFormat.Dbml:
                DBSchemaDbml.Write(this, targetFile);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown schema format");
        }
    }

    /// <summary>
    /// Adds the given default records to the tables they belong to. Records for tables not in this schema are
    /// left out.
    /// </summary>
    internal void AddDefaultRecords(DBDefaultRecords defaultRecords)
    {
        foreach (string tableName in defaultRecords.Tables)
        {
            if (_tables.TryGetValue(tableName, out DBTable? table))
            {
                foreach (DBDefaultRecord record in defaultRecords[tableName])
                {
                    table.AddDefaultRecord(record);
                }
            }
        }
    }

    /// <summary>The words a DBML file can start with.</summary>
    private static readonly string[] DbmlKeywords =
    {
        "table", "ref", "records", "project", "note", "tablegroup", "tablepartial", "enum", "diagramview", "metadata", "use", "reuse",
    };

    /// <summary>
    /// Works out whether a schema file is JSON, XML or DBML from how it starts, after any whitespace and
    /// <c>//</c> or <c>/* */</c> comments (both JSON and DBML files can start with a comment).
    /// </summary>
    /// <exception cref="FormatException">The file starts with neither <c>{</c>, <c>&lt;</c> nor a DBML keyword.</exception>
    public static DBSchemaFormat DetectFormat(string schemaFile)
    {
        if (schemaFile is null) throw new ArgumentNullException(nameof(schemaFile));

        string text;
        // StreamReader skips a byte order mark, so the first character read is real content
        using (var reader = new StreamReader(schemaFile, Encoding.UTF8, true))
        {
            text = reader.ReadToEnd();
        }

        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (string.CompareOrdinal(text, i, "//", 0, 2) == 0)
            {
                int end = text.IndexOf('\n', i);
                i = end == -1 ? text.Length : end + 1;
            }
            else if (string.CompareOrdinal(text, i, "/*", 0, 2) == 0)
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end == -1 ? text.Length : end + 2;
            }
            else
            {
                break;
            }
        }

        if (i < text.Length)
        {
            if (text[i] == '{') return DBSchemaFormat.Json;
            if (text[i] == '<') return DBSchemaFormat.Xml;
            // DBML front matter
            if (i == 0 && text.StartsWith("---", StringComparison.Ordinal)) return DBSchemaFormat.Dbml;

            int wordEnd = i;
            while (wordEnd < text.Length && char.IsLetter(text[wordEnd])) wordEnd++;
            if (Array.IndexOf(DbmlKeywords, text.Substring(i, wordEnd - i).ToLowerInvariant()) >= 0) return DBSchemaFormat.Dbml;
        }

        throw new FormatException("'" + schemaFile + "' is not a JSON, XML or DBML schema file");
    }
}
