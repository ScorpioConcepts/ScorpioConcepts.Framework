using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace ScorpioConcepts.Framework.DBSchema;

/// <summary>
/// Reads and writes the original XML schema file format
/// </summary>
internal static class DBSchemaXml
{
    /// <summary>
    /// Write the given schema, including each table's default records, to an XML file
    /// </summary>
    internal static void Write(DBSchemaDefinition schema, string targetFile)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            NewLineChars = Environment.NewLine,
            NewLineHandling = NewLineHandling.Entitize,
            NewLineOnAttributes = false,
        };

        // Dispose the writer even when writing fails, so the file is not left locked
        using XmlWriter writer = XmlWriter.Create(targetFile, settings);

        writer.WriteStartElement("tables");
        foreach (DBTable table in schema.TablesInOrder)
        {
            writer.WriteStartElement("table");
            writer.WriteAttributeString("name", table.Name);

            foreach (DBColumn column in table.Columns)
            {
                writer.WriteStartElement("column");
                writer.WriteAttributeString("name", column.Name);
                writer.WriteAttributeString("datatype", column.DataType);
                writer.WriteAttributeString("options", column.Options);
                writer.WriteEndElement();
            }

            if (table.PrimaryKey.Columns.Count > 0)
            {
                writer.WriteStartElement("primary_key");
                writer.WriteAttributeString("name", table.PrimaryKey.Name);
                foreach (string keyColumn in table.PrimaryKey.Columns)
                {
                    writer.WriteStartElement("column");
                    writer.WriteAttributeString("name", keyColumn);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }

            foreach (DBIndex index in table.Indexes)
            {
                writer.WriteStartElement("index");
                writer.WriteAttributeString("name", index.Name);
                writer.WriteAttributeString("clustered", index.Clustered.ToString());
                writer.WriteAttributeString("unique", index.Unique.ToString());
                foreach (DBIndexColumn column in index.Columns)
                {
                    writer.WriteStartElement("column");
                    writer.WriteAttributeString("name", column.Name);
                    writer.WriteAttributeString("desc", column.Descending.ToString());
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }

            foreach (DBForeignKey foreignKey in table.ForeignKeys)
            {
                writer.WriteStartElement("foreign_key");
                writer.WriteAttributeString("name", foreignKey.Name);
                writer.WriteAttributeString("column", foreignKey.Column);
                writer.WriteAttributeString("pk_table", foreignKey.ReferencedTable);
                writer.WriteAttributeString("pk_column", foreignKey.ReferencedColumn);
                writer.WriteEndElement();
            }

            foreach (DBDefaultRecord record in table.DefaultRecords)
            {
                writer.WriteStartElement("default_record");
                foreach (KeyValuePair<string, object?> kvp in record.OrderedValues)
                {
                    if (kvp.Value is null || kvp.Value is DBNull)
                    {
                        // XML has no null value, so leave the column out and let the database fill it in
                        continue;
                    }

                    writer.WriteStartElement("columnValue");
                    writer.WriteAttributeString("column", kvp.Key);
                    if (kvp.Value is string text)
                    {
                        writer.WriteCData(text);
                    }
                    else
                    {
                        writer.WriteValue(kvp.Value);
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// Read a schema from an XML file
    /// </summary>
    /// <exception cref="FormatException">A required attribute is missing.</exception>
    internal static DBSchemaDefinition Read(string schemaXMLFile)
    {
        var schema = new DBSchemaDefinition();
        var settings = new XmlReaderSettings { IgnoreWhitespace = true };

        // Dispose the reader even when parsing fails, so the file is not left locked
        using XmlReader reader = XmlReader.Create(schemaXMLFile, settings);

        reader.MoveToContent();
        if (reader.IsEmptyElement)
        {
            // <tables />
            reader.ReadStartElement("tables");
            return schema;
        }

        reader.ReadStartElement("tables");
        while (reader.NodeType == XmlNodeType.Element)
        {
            if (reader.Name != "table")
            {
                // Unknown elements are ignored, so newer files can still be read
                reader.Skip();
                continue;
            }

            DBTable table = schema.AddTable(RequiredAttribute(reader, "name", "a <table>"));
            if (reader.IsEmptyElement)
            {
                // <table name="..." /> has no children to read
                reader.Read();
                continue;
            }

            reader.ReadStartElement();
            while (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "column":
                        ReadColumn(reader, table);
                        break;
                    case "primary_key":
                        ReadPrimaryKey(reader, table);
                        break;
                    case "index":
                        ReadIndex(reader, table);
                        break;
                    case "foreign_key":
                        ReadForeignKey(reader, table);
                        break;
                    case "default_record":
                        ReadDefaultRecord(reader, table);
                        break;
                    default:
                        // Without this an unknown element was never consumed and the loop never ended
                        reader.Skip();
                        break;
                }
            }
            reader.ReadEndElement();
        }
        reader.ReadEndElement();

        return schema;
    }

    private static string RequiredAttribute(XmlReader reader, string name, string element)
    {
        string? value = reader.GetAttribute(name);
        if (string.IsNullOrEmpty(value))
        {
            throw new FormatException("The \"" + name + "\" attribute is missing for " + element);
        }

        return value!;
    }

    private static bool BoolAttribute(XmlReader reader, string name)
    {
        string? value = reader.GetAttribute(name);
        return value is not null && bool.Parse(value);
    }

    /// <summary>Moves past an element whose children have been read, or that had none.</summary>
    private static void EndElement(XmlReader reader, string name)
    {
        reader.ReadStartElement();
        if (reader.NodeType == XmlNodeType.EndElement && reader.Name == name)
        {
            reader.ReadEndElement();
        }
    }

    private static void ReadDefaultRecord(XmlReader reader, DBTable table)
    {
        DBDefaultRecord record = table.AddDefaultRecord();
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return;
        }

        reader.ReadStartElement();
        while (reader.NodeType == XmlNodeType.Element && reader.Name == "columnValue")
        {
            string column = RequiredAttribute(reader, "column", "a <columnValue> in table '" + table.Name + "'");
            reader.ReadStartElement();
            record.AddValue(column, reader.ReadContentAsObject());

            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "columnValue")
            {
                reader.ReadEndElement();
            }
        }
        reader.ReadEndElement();
    }

    private static void ReadIndex(XmlReader reader, DBTable table)
    {
        string context = "an <index> in table '" + table.Name + "'";
        DBIndex index = table.AddIndex(RequiredAttribute(reader, "name", context), BoolAttribute(reader, "clustered"), BoolAttribute(reader, "unique"));
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return;
        }

        reader.ReadStartElement();
        while (reader.NodeType == XmlNodeType.Element && reader.Name == "column")
        {
            index.AddColumn(RequiredAttribute(reader, "name", "a column of " + context), BoolAttribute(reader, "desc"));
            EndElement(reader, "column");
        }
        reader.ReadEndElement();
    }

    private static void ReadForeignKey(XmlReader reader, DBTable table)
    {
        string context = "a <foreign_key> in table '" + table.Name + "'";
        table.AddForeignKey(
            RequiredAttribute(reader, "name", context),
            RequiredAttribute(reader, "column", context),
            RequiredAttribute(reader, "pk_table", context),
            RequiredAttribute(reader, "pk_column", context));
        EndElement(reader, "foreign_key");
    }

    private static void ReadPrimaryKey(XmlReader reader, DBTable table)
    {
        table.PrimaryKey.Name = reader.GetAttribute("name");
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return;
        }

        reader.ReadStartElement();
        while (reader.NodeType == XmlNodeType.Element && reader.Name == "column")
        {
            table.PrimaryKey.AddColumn(RequiredAttribute(reader, "name", "a primary key column in table '" + table.Name + "'"));
            EndElement(reader, "column");
        }
        reader.ReadEndElement();
    }

    private static void ReadColumn(XmlReader reader, DBTable table)
    {
        string context = "a <column> in table '" + table.Name + "'";
        table.AddColumn(
            RequiredAttribute(reader, "name", context),
            RequiredAttribute(reader, "datatype", context),
            reader.GetAttribute("options") ?? string.Empty);
        EndElement(reader, "column");
    }
}
