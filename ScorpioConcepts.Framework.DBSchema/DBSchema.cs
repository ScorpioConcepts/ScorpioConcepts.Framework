using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Data.SqlClient;
using System.Xml;

namespace ScorpioConcepts.Framework.DBSchema
{
    /// <summary>
    /// This class represents an entire database schema as read from file or from the database
    /// </summary>
    public class DBSchema
    {
        #region Static Interface
        /// <summary>
        /// Database connection to use
        /// </summary>
        internal static SqlConnection DatabaseConnection { get; private set; }
        /// <summary>
        /// A log of activity useful for debugging
        /// </summary>
        public static List<String> DebugLog { get; private set; }

        static DBSchema()
        {
            DebugLog = new List<string>();
        }

        /// <summary>
        /// Initialize the DBSchema to use the given SQL Connection
        /// </summary>
        /// <param name="conn">SQL Connection Object to use</param>
        public static void InitializeDB(SqlConnection conn)
        {
            DatabaseConnection = conn;
        }

        /// <summary>
        /// Generate a Database Schema XML file from the database
        /// </summary>
        /// <param name="targetFile">Filename of file to store Database Schema XML</param>
        public static void GenerateSchemaXML(string targetFile)
        {
            GenerateSchemaXML(targetFile, new DBDefaultRecords());
        }

        /// <summary>
        /// Generate a Database Schema XML file from the database, along with the given default records
        /// </summary>
        /// <param name="targetFile">Filename of file to store Database Schema XML</param>
        /// <param name="defaultRecords">List of default records to include in Database Schema XML</param>
        public static void GenerateSchemaXML(string targetFile, DBDefaultRecords defaultRecords)
        {
            DebugLog.Clear();

            DBSchema dbSchema = populateDBSchema();

            WriteSchemaXML(dbSchema, targetFile, defaultRecords);
        }

        /// <summary>
        /// Write the given Database Schema, along with the given default records, to an XML file
        /// </summary>
        internal static void WriteSchemaXML(DBSchema dbSchema, string targetFile, DBDefaultRecords defaultRecords)
        {
            XmlWriterSettings settings = new XmlWriterSettings();
            settings.Indent = true;
            settings.NewLineChars = Environment.NewLine;
            settings.NewLineHandling = NewLineHandling.Entitize;
            settings.NewLineOnAttributes = false;

            DebugLog.Add("Creating DBSchema XML file '" + targetFile + "'...");
            // Dispose the writer even when writing fails, so the file is not left locked
            using (XmlWriter writer = XmlWriter.Create(targetFile, settings))
            {
                DebugLog.Add("Found " + dbSchema.Tables.Count.ToString() + " Tables...");

                writer.WriteStartElement("tables");
                foreach (DBTable table in dbSchema.Tables.Values)
                {
                    string tableName = table.Name;

                    DebugLog.Add("DBSchema XML for table '" + tableName + "'...");
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
                        DebugLog.Add("Saving Default Records for table '" + tableName + "'...");
                        List<DBDefaultRecord> tableDefaultRecords = defaultRecords[tableName];
                        foreach (DBDefaultRecord record in tableDefaultRecords)
                        {
                            writer.WriteStartElement("default_record");
                            foreach (KeyValuePair<string, object> kvp in record.Values)
                            {
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
                    DebugLog.Add("DB Schema XML generated for table '" + tableName + "'");
                }

                writer.WriteEndElement();
            }
        }

        /// <summary>
        /// Compare the actual Database Schema with the Schema stored in the XML file, and fix any problems
        /// </summary>
        /// <param name="schemaXMLFile">Filename of the XML file that contains the Schema to check against</param>
        public static void CheckDatabaseSchema(string schemaXMLFile)
        {
            DebugLog.Clear();

            // Step 1: Get the Schema as specified in XML
            DebugLog.Add("Getting DBSchema as per XML file...");
            DBSchema xmlSchema = populateXMLSchema(schemaXMLFile);

            // Step 2: Get the Schema as it is currently in the database
            DebugLog.Add("Getting DBSchema as per database...");
            DBSchema dbSchema = populateDBSchema();

            // Step 3: Create any missing tables
            List<string> createdTables = new List<string>();
            foreach (string tableName in xmlSchema.Tables.Keys)
            {
                if (dbSchema.Tables.ContainsKey(tableName) == false)
                {
                    DebugLog.Add("Table '" + tableName + "' not found in database!!");
                    CreateDBTable(xmlSchema.Tables[tableName]);

                    createdTables.Add(tableName);
                }
            }

            // Step 3.1: If we had to create any tables, reload the Schema from the database
            if (createdTables.Count > 0)
            {
                dbSchema = populateDBSchema();
            }

            // Step 4: Check all columns exist and all indexes exist
            foreach (DBTable xmlTable in xmlSchema.Tables.Values)
            {
                DBTable dbTable = dbSchema.Tables[xmlTable.Name];

                foreach (DBTable.Column xmlColumn in xmlTable.Columns)
                {
                    if (dbTable.FindColumn(xmlColumn.Name) == null)
                    {
                        DebugLog.Add("Table '" + xmlTable.Name + "' does not have column '" + xmlColumn.Name + "'!!");
                        CreateDBColumn(dbTable, xmlColumn);
                    }
                }

                foreach (DBTable.Index xmlIndex in xmlTable.Indexes)
                {
                    if (dbTable.FindIndex(xmlIndex.Name) == null)
                    {
                        DebugLog.Add("Table '" + xmlTable.Name + "' does not have index '" + xmlIndex.Name + "'!!");
                        CreateDBIndex(dbTable, xmlIndex);
                    }
                }
            }

            // Step 5: Check that all foreign keys exist
            foreach (DBTable xmlTable in xmlSchema.Tables.Values)
            {
                DBTable dbTable = dbSchema.Tables[xmlTable.Name];

                foreach (DBTable.ForeignKey xmlFKey in xmlTable.ForeignKeys)
                {
                    if (dbTable.FindForeignKey(xmlFKey.Name) == null)
                    {
                        DebugLog.Add("Table '" + xmlTable.Name + "' does not have foreign key '" + xmlFKey.Name + "'!!");
                        CreateDBForeignKey(dbTable, xmlFKey);
                    }
                }
            }
        }

        private static DBSchema populateDBSchema()
        {
            DBSchema dbSchema = new DBSchema();

            List<String> tables = new List<string>();

            DebugLog.Add("Reading Database Tables...");
            using (SqlCommand tablesSQL = new SqlCommand("exec sp_tables", DatabaseConnection))
            using (SqlDataReader tablesReader = tablesSQL.ExecuteReader())
            {
                while (tablesReader.Read())
                {
                    string tableName = tablesReader["TABLE_NAME"].ToString();
                    string tableOwner = tablesReader["TABLE_OWNER"].ToString();
                    string tableType = tablesReader["TABLE_TYPE"].ToString();
                    if ((tableOwner == "dbo") && (tableType == "TABLE")
                        && (tableName != "dtproperties"))
                    {
                        tables.Add(tableName);
                    }
                }
            }
            DebugLog.Add("Found " + tables.Count.ToString() + " Tables...");

            foreach (string tableName in tables)
            {
                DebugLog.Add("Populate DBSchema for table '" + tableName + "'...");
                DBTable table = dbSchema.AddTable(tableName);

                DebugLog.Add("Getting Columns for table '" + tableName + "'...");
                string selectColumnsSQL = "SELECT c.name, t.name AS type_name, c.max_length, c.precision, c.scale,";
                selectColumnsSQL += " c.is_nullable, c.is_identity, dc.definition AS default_definition";
                selectColumnsSQL += " FROM sys.columns c";
                selectColumnsSQL += " JOIN sys.types t ON (t.user_type_id = c.user_type_id)";
                selectColumnsSQL += " LEFT JOIN sys.default_constraints dc ON (dc.object_id = c.default_object_id)";
                selectColumnsSQL += " WHERE c.object_id = OBJECT_ID(@table)";
                selectColumnsSQL += " ORDER BY c.column_id";

                using (SqlCommand columnsSQL = new SqlCommand(selectColumnsSQL, DatabaseConnection))
                {
                    columnsSQL.Parameters.AddWithValue("@table", "[dbo].[" + tableName + "]");
                    using (SqlDataReader columnsReader = columnsSQL.ExecuteReader())
                    {
                        while (columnsReader.Read())
                        {
                            string columnName = columnsReader.GetString(0);
                            string columnType = FormatDataType(
                                columnsReader.GetString(1),
                                columnsReader.GetInt16(2),
                                columnsReader.GetByte(3),
                                columnsReader.GetByte(4),
                                columnsReader.GetBoolean(6));
                            string columnOptions = columnsReader.GetBoolean(5) ? "NULL" : "NOT NULL";
                            if (columnsReader.IsDBNull(7) == false)
                            {
                                columnOptions += " DEFAULT " + columnsReader.GetString(7);
                            }

                            table.AddColumn(columnName, columnType, columnOptions);
                        }
                    }
                }

                DebugLog.Add("Getting Primary Keys for table '" + tableName + "'...");
                Dictionary<string, List<string>> pkeys = new Dictionary<string, List<string>>();

                using (SqlCommand pkeySQL = new SqlCommand("exec sp_pkeys [" + tableName + "]", DatabaseConnection))
                using (SqlDataReader pkeysReader = pkeySQL.ExecuteReader())
                {
                    while (pkeysReader.Read())
                    {
                        string keyName = pkeysReader["PK_NAME"].ToString();
                        string keyColumnName = pkeysReader["COLUMN_NAME"].ToString();
                        if (pkeys.ContainsKey(keyName) == false)
                        {
                            pkeys.Add(keyName, new List<string>());
                        }

                        pkeys[keyName].Add(keyColumnName);
                    }
                }

                foreach (string keyName in pkeys.Keys)
                {
                    table.PrimaryKey.Name = keyName;
                    foreach (string keyColumn in pkeys[keyName])
                    {
                        table.PrimaryKey.AddKeyColumn(keyColumn);
                    }
                }

                Version sqlVer = new Version(DatabaseConnection.ServerVersion);

                if (sqlVer.Major > 8)
                {
                    string selectIndexesSQL = "SELECT idx.name AS idx_name,idx.type_desc,idx.is_unique,cols.name AS col_name,ixc.is_descending_key";
                    selectIndexesSQL += " FROM sys.indexes idx";
                    selectIndexesSQL += " JOIN sys.index_columns ixc on (idx.index_id = ixc.index_id)";
                    selectIndexesSQL += " JOIN sys.columns cols on (ixc.column_id = cols.column_id)";
                    selectIndexesSQL += " WHERE idx.object_id = OBJECT_ID(@table)";
                    selectIndexesSQL += " AND ixc.object_id = idx.object_id";
                    selectIndexesSQL += " AND cols.object_id = idx.object_id";
                    selectIndexesSQL += " AND idx.is_primary_key = 0";
                    // Included columns are not key columns, and key columns must keep their order
                    selectIndexesSQL += " AND ixc.is_included_column = 0";
                    selectIndexesSQL += " ORDER BY idx.name, ixc.key_ordinal";

                    DebugLog.Add("Getting Indexes for table '" + tableName + "'...");
                    using (SqlCommand indexesSQL = new SqlCommand(selectIndexesSQL, DatabaseConnection))
                    {
                        indexesSQL.Parameters.AddWithValue("@table", "[dbo].[" + tableName + "]");
                        using (SqlDataReader indexesReader = indexesSQL.ExecuteReader())
                        {
                            while (indexesReader.Read())
                            {
                                string idxName = indexesReader["idx_name"].ToString();
                                string idxType = indexesReader["type_desc"].ToString();
                                bool idxUnique = Boolean.Parse(indexesReader["is_unique"].ToString());
                                string idxColName = indexesReader["col_name"].ToString();
                                bool idxColDesc = Boolean.Parse(indexesReader["is_descending_key"].ToString());

                                DBTable.Index idx = table.FindIndex(idxName);
                                if (idx == null)
                                {
                                    idx = table.AddIndex(idxName, (idxType == "CLUSTERED"), idxUnique);
                                }

                                idx.AddIndexColumn(idxColName, idxColDesc);
                            }
                        }
                    }
                }

                DebugLog.Add("Getting Foreign Keys for table '" + tableName + "'...");
                using (SqlCommand fkeySQL = new SqlCommand("exec sp_fkeys @fktable_name=[" + tableName + "]", DatabaseConnection))
                using (SqlDataReader fkeysReader = fkeySQL.ExecuteReader())
                {
                    while (fkeysReader.Read())
                    {
                        string keyName = fkeysReader["FK_NAME"].ToString();
                        string keyColumnName = fkeysReader["FKCOLUMN_NAME"].ToString();
                        string pkeyTableName = fkeysReader["PKTABLE_NAME"].ToString();
                        string pkeyColumnName = fkeysReader["PKCOLUMN_NAME"].ToString();

                        table.AddForeignKey(keyName, keyColumnName, pkeyTableName, pkeyColumnName);
                    }
                }

                DebugLog.Add("DB Schema generated for table '" + tableName + "'");
            }

            return dbSchema;
        }

        /// <summary>
        /// Build a column data type, including its size, the way it would be written in a CREATE TABLE statement
        /// </summary>
        /// <param name="typeName">Type name from sys.types</param>
        /// <param name="maxLength">sys.columns.max_length: size in bytes, or -1 for (max)</param>
        /// <param name="precision">sys.columns.precision</param>
        /// <param name="scale">sys.columns.scale</param>
        /// <param name="isIdentity">Whether the column is an IDENTITY column</param>
        internal static string FormatDataType(string typeName, short maxLength, byte precision, byte scale, bool isIdentity)
        {
            string dataType = typeName;
            switch (typeName.ToLowerInvariant())
            {
                case "char":
                case "varchar":
                case "binary":
                case "varbinary":
                    dataType += (maxLength == -1) ? "(max)" : "(" + maxLength.ToString() + ")";
                    break;
                case "nchar":
                case "nvarchar":
                    // max_length is in bytes, and each Unicode character takes two
                    dataType += (maxLength == -1) ? "(max)" : "(" + (maxLength / 2).ToString() + ")";
                    break;
                case "decimal":
                case "numeric":
                    dataType += "(" + precision.ToString() + "," + scale.ToString() + ")";
                    break;
                case "datetime2":
                case "datetimeoffset":
                case "time":
                    dataType += "(" + scale.ToString() + ")";
                    break;
                case "float":
                    if (precision != 53)
                    {
                        dataType += "(" + precision.ToString() + ")";
                    }
                    break;
            }

            if (isIdentity)
            {
                dataType += " identity";
            }

            return dataType;
        }

        internal static DBSchema populateXMLSchema(string schemaXMLFile)
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

        private static void CreateDBTable(DBTable table)
        {
            DebugLog.Add("Creating table " + table.Name + "...");

            string createSQL = "CREATE TABLE [" + table.Name + "] (" + Environment.NewLine;
            foreach (DBTable.Column column in table.Columns)
            {
                createSQL += "\t[" + column.Name + "] " + column.DataType;
                if (column.DataType.ToLower().EndsWith("identity") == true)
                {
                    createSQL += "(1,1)";
                }
                createSQL += " " + column.Options + "," + Environment.NewLine;
            }

            if ((table.PrimaryKey.Name != null) && (table.PrimaryKey.Name != ""))
            {
                createSQL += "\tCONSTRAINT [" + table.PrimaryKey.Name + "] PRIMARY KEY CLUSTERED (" + Environment.NewLine;
                foreach (string pkeyColumn in table.PrimaryKey.KeyColumns)
                {
                    createSQL += "\t\t[" + pkeyColumn + "] ASC," + Environment.NewLine;
                }
                createSQL = createSQL.TrimEnd(new char[] { ',', '\r', '\n' });
                createSQL += Environment.NewLine + "\t)";
            }
            createSQL = createSQL.TrimEnd(new char[] { ',', '\r', '\n' });
            createSQL += Environment.NewLine + ");" + Environment.NewLine;

            //DebugLog.Add(createSQL.ToString());

            SqlCommand createQuery = new SqlCommand(createSQL, DatabaseConnection);
            createQuery.ExecuteNonQuery();

            foreach (DBDefaultRecord record in table.DefaultRecords)
            {
                string insertSQL = "INSERT INTO [" + table.Name + "](" + Environment.NewLine;
                string insertParams = "";
                foreach (string columnName in record.Values.Keys)
                {
                    insertSQL += "\t[" + columnName + "]," + Environment.NewLine;
                    insertParams += "\t@" + columnName + "," + Environment.NewLine;
                }
                insertSQL = insertSQL.TrimEnd(new char[] { ',', '\r', '\n' });
                insertSQL += Environment.NewLine + ") VALUES(" + Environment.NewLine;

                insertParams = insertParams.TrimEnd(new char[] { ',', '\r', '\n' });
                insertSQL += insertParams;
                insertSQL += Environment.NewLine + ");" + Environment.NewLine;

                SqlCommand insertQuery = new SqlCommand(insertSQL, DatabaseConnection);
                foreach (KeyValuePair<string, object> kvp in record.Values)
                {
                    insertQuery.Parameters.AddWithValue("@" + kvp.Key, kvp.Value);
                }

                DebugLog.Add("Inserting default record into table " + table.Name + "...");

                insertQuery.ExecuteNonQuery();
            }
        }
        private static void CreateDBColumn(DBTable table, DBTable.Column column)
        {
            DebugLog.Add("Altering table " + table.Name + " adding column '" + column.Name + "'...");

            string alterSQL = "ALTER TABLE [" + table.Name + "] ADD " + Environment.NewLine;
            alterSQL += "\t[" + column.Name + "] " + column.DataType;
            if (column.DataType.ToLower().EndsWith("identity") == true)
            {
                alterSQL += "(1,1)";
            }
            alterSQL += " " + column.Options + " ;" + Environment.NewLine;

            SqlCommand alterQuery = new SqlCommand(alterSQL, DatabaseConnection);
            alterQuery.ExecuteNonQuery();
        }
        private static void CreateDBIndex(DBTable table, DBTable.Index index)
        {
            DebugLog.Add("Altering table " + table.Name + " adding index '" + index.Name + "'...");

            string createSQL = "CREATE";
            if( index.Unique == true )
            {
                createSQL += " UNIQUE";
            }
            if( index.Clustered == true )
            {
                createSQL += " CLUSTERED";
            }
            createSQL += " INDEX [" + index.Name + "] ON [" + table.Name + "] (" + Environment.NewLine;
            foreach (KeyValuePair<string, bool> idxColumn in index.Columns)
            {
                createSQL += "\t[" + idxColumn.Key + "]";
                if (idxColumn.Value == true)
                {
                    createSQL += " DESC";
                }
                createSQL += "," + Environment.NewLine;
            }
            createSQL = createSQL.TrimEnd(new char[] { ',', '\r', '\n' });
            createSQL += ") ;" + Environment.NewLine;

            SqlCommand createQuery = new SqlCommand(createSQL, DatabaseConnection);
            createQuery.ExecuteNonQuery();
        }
        private static void CreateDBForeignKey(DBTable table, DBTable.ForeignKey fkey)
        {
            DebugLog.Add("Altering table " + table.Name + " adding foreign key '" + fkey.Name + "'...");

            string alterSQL = "ALTER TABLE [" + table.Name + "] WITH CHECK ADD CONSTRAINT [" + fkey.Name+"]" + Environment.NewLine;
            alterSQL += "\tFOREIGN KEY([" + fkey.Column + "])" + Environment.NewLine;
            alterSQL += "\t\tREFERENCES [" + fkey.PKeyTable + "] ([" + fkey.PKeyColumn + "]) ;" + Environment.NewLine;

            SqlCommand alterQuery = new SqlCommand(alterSQL, DatabaseConnection);
            alterQuery.ExecuteNonQuery();
        }

        #endregion

        /// <summary>
        /// A list of all the Tables in the Schema
        /// </summary>
        public Dictionary<string, DBTable> Tables { get; private set; }

        internal DBSchema()
        {
            this.Tables = new Dictionary<string, DBTable>();
        }

        internal DBTable AddTable(string tableName)
        {
            if (this.Tables.ContainsKey(tableName) == true)
            {
                throw new ArgumentException("Table '" + tableName + "' already exists in the schema!!", "tableName");
            }

            DBTable table = new DBTable(tableName);
            this.Tables.Add(tableName, table);

            return table;
        }
    }
}
