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
    private readonly Dictionary<string, DBTable> _tables = new();
    private readonly List<DBTable> _order = new();

    internal DBSchemaDefinition()
    {
        Tables = new ReadOnlyDictionary<string, DBTable>(_tables);
        TablesInOrder = _order.AsReadOnly();
    }

    /// <summary>The tables by name.</summary>
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
    /// Reads a schema file. Whether it is JSON or XML is worked out from its first character.
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

    /// <summary>
    /// Works out whether a schema file is JSON or XML from its first non-whitespace character.
    /// </summary>
    /// <exception cref="FormatException">The file starts with neither <c>{</c> nor <c>&lt;</c>.</exception>
    public static DBSchemaFormat DetectFormat(string schemaFile)
    {
        if (schemaFile is null) throw new ArgumentNullException(nameof(schemaFile));

        // StreamReader skips a byte order mark, so the first character read is real content
        using (var reader = new StreamReader(schemaFile, Encoding.UTF8, true))
        {
            int c;
            while ((c = reader.Read()) != -1)
            {
                if (char.IsWhiteSpace((char)c)) continue;
                if (c == '{') return DBSchemaFormat.Json;
                if (c == '<') return DBSchemaFormat.Xml;
                break;
            }
        }

        throw new FormatException("'" + schemaFile + "' is not a JSON or XML schema file");
    }
}
