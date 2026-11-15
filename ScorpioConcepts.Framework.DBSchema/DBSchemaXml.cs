using System;
using System.Collections.Generic;
using System.Xml;

namespace ScorpioConcepts.Framework.DBSchema
{
    /// <summary>
    /// Reads and writes the original XML schema file format
    /// </summary>
    internal static class DBSchemaXml
    {
        /// <summary>
        /// Write the given Database Schema, along with the given default records, to an XML file
        /// </summary>
        internal static void Write(DBSchema dbSchema, string targetFile, DBDefaultRecords defaultRecords)
        {
            XmlWriterSettings settings = new XmlWriterSettings();
            settings.Indent = true;
            settings.NewLineChars = Environment.NewLine;
            settings.NewLineHandling = NewLineHandling.Entitize;
            settings.NewLineOnAttributes = false;

            // Dispose the writer even when writing fails, so the file is not left locked
            using (XmlWriter writer = XmlWriter.Create(targetFile, settings))
            {
                DBSchema.DebugLog.Add("Found " + dbSchema.Tables.Count.ToString() + " Tables...");

                writer.WriteStartElement("tables");
                foreach (DBTable table in dbSchema.Tables.Values)
                {
                    string tableName = table.Name;

                    DBSchema.DebugLog.Add("DBSchema XML for table '" + tableName + "'...");
                    writer.WriteStartElement("table");
                    writer.WriteAttributeString("name", tableName);

                    foreach( DBTable.Column column in table.Columns)
                    {
                        writer.WriteStartElement("column");
                        writer.WriteAttributeString("name", column.Name);
                        writer.WriteAttributeString("datatype", column.DataType);
                        writer.WriteAttributeString("options", column.Options);
                        writer.WriteEndElement();
                    }

                    if( table.PrimaryKey.KeyColumns.Count > 0 )
                    {
                        writer.WriteStartElement("primary_key");
                        writer.WriteAttributeString("name", table.PrimaryKey.Name);
                        foreach (string keyColumn in table.PrimaryKey.KeyColumns)
                        {
                            writer.WriteStartElement("column");
                            writer.WriteAttributeString("name", keyColumn);
                            writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                    }

                    foreach (DBTable.Index idx in table.Indexes)
                    {
                        writer.WriteStartElement("index");
                        writer.WriteAttributeString("name", idx.Name);
                        writer.WriteAttributeString("clustered", idx.Clustered.ToString());
                        writer.WriteAttributeString("unique", idx.Unique.ToString());
                        foreach (KeyValuePair<string,bool> column in idx.Columns)
                        {
                            writer.WriteStartElement("column");
                            writer.WriteAttributeString("name", column.Key);
                            writer.WriteAttributeString("desc", column.Value.ToString());
                            writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                    }

                    foreach( DBTable.ForeignKey fkey in table.ForeignKeys)
                    {
                        writer.WriteStartElement("foreign_key");
                        writer.WriteAttributeString("name", fkey.Name);
                        writer.WriteAttributeString("column", fkey.Column);
                        writer.WriteAttributeString("pk_table", fkey.PKeyTable);
                        writer.WriteAttributeString("pk_column", fkey.PKeyColumn);
                        writer.WriteEndElement();
                    }

                    if (defaultRecords.ContainsKey(tableName))
                    {
                        DBSchema.DebugLog.Add("Saving Default Records for table '" + tableName + "'...");
                        List<DBDefaultRecord> tableDefaultRecords = defaultRecords[tableName];
                        foreach (DBDefaultRecord record in tableDefaultRecords)
                        {
                            writer.WriteStartElement("default_record");
                            foreach (KeyValuePair<string, object> kvp in record.Values)
                            {
                                if ((kvp.Value == null) || (kvp.Value is DBNull))
                                {
                                    // XML has no null value, so leave the column out and let the database fill it in
                                    continue;
                                }

                                writer.WriteStartElement("columnValue");
                                writer.WriteAttributeString("column", kvp.Key);
                                if (kvp.Value.GetType() == typeof(string))
                                {
                                    writer.WriteCData(kvp.Value.ToString());
                                }
                                else
                                {
                                    writer.WriteValue(kvp.Value);
                                }
                                writer.WriteEndElement();
                            }
                            writer.WriteEndElement();
                        }
                    }

                    writer.WriteEndElement();
                    DBSchema.DebugLog.Add("DB Schema XML generated for table '" + tableName + "'");
                }

                writer.WriteEndElement();
            }
        }

        /// <summary>
        /// Read a schema from an XML file
        /// </summary>
        internal static DBSchema Read(string schemaXMLFile)
        {
            DBSchema xmlSchema = new DBSchema();

            XmlReaderSettings settings = new XmlReaderSettings();
            settings.IgnoreWhitespace = true;

            // Dispose the reader even when parsing fails, so the file is not left locked
            using (XmlReader reader = XmlReader.Create(schemaXMLFile, settings))
            {
                reader.MoveToContent();
                if (reader.IsEmptyElement)
                {
                    // <tables />
                    reader.ReadStartElement("tables");
                    return xmlSchema;
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

                    DBTable table = xmlSchema.AddTable(reader.GetAttribute("name"));
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
                                populateXMLColumn(reader, table);
                                break;
                            case "primary_key":
                                populateXMLPrimaryKey(reader, table);
                                break;
                            case "index":
                                populateXMLIndex(reader, table);
                                break;
                            case "foreign_key":
                                populateXMLForeignKey(reader, table);
                                break;
                            case "default_record":
                                populateXMLDefaultRecord(reader, table);
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
            }

            return xmlSchema;
        }

        private static void populateXMLDefaultRecord(XmlReader reader, DBTable table)
        {
            reader.ReadStartElement();
            DBDefaultRecord rec = table.AddDefaultRecord();

            while ((reader.NodeType == XmlNodeType.Element) && (reader.Name == "columnValue"))
            {
                String recColumnName = reader.GetAttribute("column");
                reader.ReadStartElement();
                object recData = reader.ReadContentAsObject();
                rec.AddValue(recColumnName, recData);

                if ((reader.NodeType == XmlNodeType.EndElement) && (reader.Name == "columnValue"))
                {
                    reader.ReadEndElement();
                }
            }
            reader.ReadEndElement();
        }
        private static void populateXMLIndex(XmlReader reader, DBTable table)
        {
            string idxName = reader.GetAttribute("name");
            bool idxClustered = Boolean.Parse(reader.GetAttribute("clustered").ToString());
            bool idxUnique = Boolean.Parse(reader.GetAttribute("unique").ToString());
            reader.ReadStartElement();

            DBTable.Index idx = table.AddIndex(idxName, idxClustered, idxUnique);

            while ((reader.NodeType == XmlNodeType.Element) && (reader.Name == "column"))
            {
                String idxColumnName = reader.GetAttribute("name");
                bool idxColumnDesc = Boolean.Parse(reader.GetAttribute("desc").ToString());
                idx.AddIndexColumn(idxColumnName, idxColumnDesc);
                reader.ReadStartElement();
                if ((reader.NodeType == XmlNodeType.EndElement) && (reader.Name == "column"))
                {
                    reader.ReadEndElement();
                }
            }
            reader.ReadEndElement();
        }
        private static void populateXMLForeignKey(XmlReader reader, DBTable table)
        {
            string fkeyName = reader.GetAttribute("name");
            string fkeyColumn = reader.GetAttribute("column");
            string fkeyPKeyTable = reader.GetAttribute("pk_table");
            string fkeyPKeyColumn = reader.GetAttribute("pk_column");
            table.AddForeignKey(fkeyName, fkeyColumn, fkeyPKeyTable, fkeyPKeyColumn);

            reader.ReadStartElement();
            if ((reader.NodeType == XmlNodeType.EndElement) && (reader.Name == "foreign_key"))
            {
                reader.ReadEndElement();
            }
        }
        private static void populateXMLPrimaryKey(XmlReader reader, DBTable table)
        {
            table.PrimaryKey.Name = reader.GetAttribute("name");
            reader.ReadStartElement();
            while ((reader.NodeType == XmlNodeType.Element) && (reader.Name == "column"))
            {
                String pkeyColumnName = reader.GetAttribute("name");
                table.PrimaryKey.AddKeyColumn(pkeyColumnName);
                reader.ReadStartElement();
                if ((reader.NodeType == XmlNodeType.EndElement) && (reader.Name == "column"))
                {
                    reader.ReadEndElement();
                }
            }
            reader.ReadEndElement();
        }
        private static void populateXMLColumn(XmlReader reader, DBTable table)
        {
            string columnName = reader.GetAttribute("name");
            string columnDataType = reader.GetAttribute("datatype");
            string columnOptions = reader.GetAttribute("options");
            table.AddColumn(columnName, columnDataType, columnOptions);

            reader.ReadStartElement();
            if ((reader.NodeType == XmlNodeType.EndElement) && (reader.Name == "column"))
            {
                reader.ReadEndElement();
            }
        }
    }
}
