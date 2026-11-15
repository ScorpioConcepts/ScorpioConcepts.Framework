using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ScorpioConcepts.Framework.DBSchema;

/// <summary>
/// Reads and writes the JSON schema file format.
/// </summary>
/// <remarks>
/// A schema file looks like this (only "name" and "columns" are needed for a table):
/// <code>
/// {
///   "tables": [
///     {
///       "name": "Customers",
///       "columns": [
///         { "name": "Id", "dataType": "int identity", "options": "NOT NULL" },
///         { "name": "Email", "dataType": "varchar(100)", "options": "NULL" }
///       ],
///       "primaryKey": { "name": "PK_Customers", "columns": [ "Id" ] },
///       "indexes": [
///         { "name": "IX_Customers_Email", "clustered": false, "unique": true,
///           "columns": [ { "name": "Email", "descending": false } ] }
///       ],
///       "foreignKeys": [
///         { "name": "FK_...", "column": "...", "referencedTable": "...", "referencedColumn": "..." }
///       ],
///       "defaultRecords": [ { "Id": 1, "Email": null } ]
///     }
///   ]
/// }
/// </code>
/// Index and primary key columns can also be given as plain strings. Property names are not case
/// sensitive, comments and trailing commas are allowed, and unknown properties are ignored.
/// </remarks>
internal static class DBSchemaJson
{
    private static readonly JsonWriterOptions WriterOptions = new JsonWriterOptions
    {
        Indented = true,
        // Keep SQL such as DEFAULT ('') readable instead of escaping the quotes as '
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions ReaderOptions = new JsonDocumentOptions
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Write the given schema, including each table's default records, to a JSON file
    /// </summary>
    internal static void Write(DBSchemaDefinition schema, string targetFile)
    {
        using (FileStream stream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None))
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("tables");
            foreach (DBTable table in schema.TablesInOrder)
            {
                writer.WriteStartObject();
                writer.WriteString("name", table.Name);

                writer.WriteStartArray("columns");
                foreach (DBColumn column in table.Columns)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", column.Name);
                    writer.WriteString("dataType", column.DataType);
                    writer.WriteString("options", column.Options);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                if (table.PrimaryKey.Columns.Count > 0)
                {
                    writer.WriteStartObject("primaryKey");
                    writer.WriteString("name", table.PrimaryKey.Name);
                    writer.WriteStartArray("columns");
                    foreach (string keyColumn in table.PrimaryKey.Columns)
                    {
                        writer.WriteStringValue(keyColumn);
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                if (table.Indexes.Count > 0)
                {
                    writer.WriteStartArray("indexes");
                    foreach (DBIndex index in table.Indexes)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", index.Name);
                        writer.WriteBoolean("clustered", index.Clustered);
                        writer.WriteBoolean("unique", index.Unique);
                        writer.WriteStartArray("columns");
                        foreach (DBIndexColumn column in index.Columns)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("name", column.Name);
                            writer.WriteBoolean("descending", column.Descending);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }

                if (table.ForeignKeys.Count > 0)
                {
                    writer.WriteStartArray("foreignKeys");
                    foreach (DBForeignKey fkey in table.ForeignKeys)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", fkey.Name);
                        writer.WriteString("column", fkey.Column);
                        writer.WriteString("referencedTable", fkey.ReferencedTable);
                        writer.WriteString("referencedColumn", fkey.ReferencedColumn);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }

                if (table.DefaultRecords.Count > 0)
                {
                    writer.WriteStartArray("defaultRecords");
                    foreach (DBDefaultRecord record in table.DefaultRecords)
                    {
                        writer.WriteStartObject();
                        foreach (KeyValuePair<string, object?> kvp in record.OrderedValues)
                        {
                            writer.WritePropertyName(kvp.Key);
                            WriteValue(writer, kvp.Value);
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        if ((value == null) || (value is DBNull))
        {
            writer.WriteNullValue();
        }
        else if (value is string)
        {
            writer.WriteStringValue((string)value);
        }
        else if (value is bool)
        {
            writer.WriteBooleanValue((bool)value);
        }
        else if ((value is int) || (value is short) || (value is byte) || (value is sbyte) || (value is ushort))
        {
            writer.WriteNumberValue(Convert.ToInt32(value, CultureInfo.InvariantCulture));
        }
        else if ((value is long) || (value is uint))
        {
            writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }
        else if (value is ulong)
        {
            writer.WriteNumberValue((ulong)value);
        }
        else if (value is decimal)
        {
            writer.WriteNumberValue((decimal)value);
        }
        else if ((value is double) || (value is float))
        {
            writer.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
        }
        else if (value is DateTime)
        {
            writer.WriteStringValue((DateTime)value);
        }
        else if (value is DateTimeOffset)
        {
            writer.WriteStringValue((DateTimeOffset)value);
        }
        else if (value is Guid)
        {
            writer.WriteStringValue((Guid)value);
        }
        else
        {
            writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Read a schema from a JSON file
    /// </summary>
    /// <exception cref="JsonException">The file is not valid JSON</exception>
    /// <exception cref="FormatException">The JSON is valid, but is not a schema</exception>
    internal static DBSchemaDefinition Read(string schemaJSONFile)
    {
        var schema = new DBSchemaDefinition();

        using (FileStream stream = File.OpenRead(schemaJSONFile))
        using (JsonDocument document = JsonDocument.Parse(stream, ReaderOptions))
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("A JSON schema file must contain an object with a \"tables\" array");
            }

            foreach (JsonElement tableElement in OptionalArray(root, "tables", "the schema"))
            {
                ReadTable(schema, tableElement);
            }
        }

        return schema;
    }

    private static void ReadTable(DBSchemaDefinition schema, JsonElement element)
    {
        RequireObject(element, "Each entry in \"tables\"");
        string tableName = RequiredString(element, "name", "a table");
        DBTable table = schema.AddTable(tableName);
        string context = "table '" + tableName + "'";

        foreach (JsonElement column in OptionalArray(element, "columns", context))
        {
            RequireObject(column, "Each column of " + context);
            string columnName = RequiredString(column, "name", "a column of " + context);
            string columnContext = "column '" + columnName + "' of " + context;
            table.AddColumn(columnName, RequiredString(column, "dataType", columnContext), OptionalString(column, "options", columnContext) ?? string.Empty);
        }

        JsonElement primaryKey;
        if (TryGetProperty(element, "primaryKey", out primaryKey) && (primaryKey.ValueKind != JsonValueKind.Null))
        {
            string keyContext = "the primary key of " + context;
            RequireObject(primaryKey, "The \"primaryKey\" of " + context);
            table.PrimaryKey.Name = OptionalString(primaryKey, "name", keyContext);
            foreach (JsonElement keyColumn in OptionalArray(primaryKey, "columns", keyContext))
            {
                table.PrimaryKey.AddColumn(ColumnName(keyColumn, keyContext));
            }
        }

        foreach (JsonElement indexElement in OptionalArray(element, "indexes", context))
        {
            RequireObject(indexElement, "Each index of " + context);
            string indexName = RequiredString(indexElement, "name", "an index of " + context);
            string indexContext = "index '" + indexName + "' of " + context;
            DBIndex index = table.AddIndex(indexName,
                OptionalBool(indexElement, "clustered", indexContext),
                OptionalBool(indexElement, "unique", indexContext));

            foreach (JsonElement indexColumn in OptionalArray(indexElement, "columns", indexContext))
            {
                bool descending = (indexColumn.ValueKind == JsonValueKind.Object) && OptionalBool(indexColumn, "descending", indexContext);
                index.AddColumn(ColumnName(indexColumn, indexContext), descending);
            }
        }

        foreach (JsonElement fkey in OptionalArray(element, "foreignKeys", context))
        {
            RequireObject(fkey, "Each foreign key of " + context);
            string fkeyName = RequiredString(fkey, "name", "a foreign key of " + context);
            string fkeyContext = "foreign key '" + fkeyName + "' of " + context;
            table.AddForeignKey(fkeyName,
                RequiredString(fkey, "column", fkeyContext),
                RequiredString(fkey, "referencedTable", fkeyContext),
                RequiredString(fkey, "referencedColumn", fkeyContext));
        }

        foreach (JsonElement recordElement in OptionalArray(element, "defaultRecords", context))
        {
            RequireObject(recordElement, "Each default record of " + context);
            DBDefaultRecord record = table.AddDefaultRecord();
            foreach (JsonProperty value in recordElement.EnumerateObject())
            {
                record.AddValue(value.Name, ReadValue(value.Value, "column '" + value.Name + "' in a default record of " + context));
            }
        }
    }

    private static object ReadValue(JsonElement value, string context)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString()!;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return DBNull.Value;
            case JsonValueKind.Number:
                int intValue;
                if (value.TryGetInt32(out intValue)) return intValue;
                long longValue;
                if (value.TryGetInt64(out longValue)) return longValue;
                decimal decimalValue;
                if (value.TryGetDecimal(out decimalValue)) return decimalValue;
                return value.GetDouble();
            default:
                throw new FormatException("The value of " + context + " must be a string, number, true, false or null");
        }
    }

    /// <summary>A key or index column, given either as "Name" or as { "name": "Name", ... }</summary>
    private static string ColumnName(JsonElement column, string context)
    {
        if (column.ValueKind == JsonValueKind.String)
        {
            return column.GetString()!;
        }
        if (column.ValueKind == JsonValueKind.Object)
        {
            return RequiredString(column, "name", "a column of " + context);
        }

        throw new FormatException("Each column of " + context + " must be a string or an object with a \"name\"");
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default(JsonElement);
        return false;
    }

    private static void RequireObject(JsonElement element, string what)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException(what + " must be a JSON object");
        }
    }

    private static string RequiredString(JsonElement element, string name, string context)
    {
        string? value = OptionalString(element, name, context);
        if (value is null || value.Length == 0)
        {
            throw new FormatException("\"" + name + "\" is missing for " + context);
        }

        return value;
    }

    private static string? OptionalString(JsonElement element, string name, string context)
    {
        JsonElement value;
        if ((TryGetProperty(element, name, out value) == false) || (value.ValueKind == JsonValueKind.Null))
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("\"" + name + "\" of " + context + " must be a string");
        }

        return value.GetString()!;
    }

    private static bool OptionalBool(JsonElement element, string name, string context)
    {
        JsonElement value;
        if ((TryGetProperty(element, name, out value) == false) || (value.ValueKind == JsonValueKind.Null))
        {
            return false;
        }
        if ((value.ValueKind != JsonValueKind.True) && (value.ValueKind != JsonValueKind.False))
        {
            throw new FormatException("\"" + name + "\" of " + context + " must be true or false");
        }

        return value.GetBoolean();
    }

    private static IEnumerable<JsonElement> OptionalArray(JsonElement element, string name, string context)
    {
        JsonElement value;
        if ((TryGetProperty(element, name, out value) == false) || (value.ValueKind == JsonValueKind.Null))
        {
            return Array.Empty<JsonElement>();
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("\"" + name + "\" of " + context + " must be an array");
        }

        // Materialise the elements so they can be read after the caller moves on
        List<JsonElement> items = new List<JsonElement>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            items.Add(item);
        }
        return items;
    }
}
