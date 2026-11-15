using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.SqlClient;

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
        /// Generate a Database Schema JSON file from the database
        /// </summary>
        /// <param name="targetFile">Filename of file to store the Database Schema in</param>
        public static void GenerateSchema(string targetFile)
        {
            GenerateSchema(targetFile, new DBDefaultRecords(), DBSchemaFormat.Json);
        }

        /// <summary>
        /// Generate a Database Schema JSON file from the database, along with the given default records
        /// </summary>
        /// <param name="targetFile">Filename of file to store the Database Schema in</param>
        /// <param name="defaultRecords">List of default records to include in the Database Schema</param>
        public static void GenerateSchema(string targetFile, DBDefaultRecords defaultRecords)
        {
            GenerateSchema(targetFile, defaultRecords, DBSchemaFormat.Json);
        }

        /// <summary>
        /// Generate a Database Schema file in the given format from the database, along with the given default records
        /// </summary>
        /// <param name="targetFile">Filename of file to store the Database Schema in</param>
        /// <param name="defaultRecords">List of default records to include in the Database Schema</param>
        /// <param name="format">The file format to write</param>
        public static void GenerateSchema(string targetFile, DBDefaultRecords defaultRecords, DBSchemaFormat format)
        {
            DebugLog.Clear();

            DBSchema dbSchema = populateDBSchema();

            WriteSchemaFile(dbSchema, targetFile, defaultRecords, format);
        }

        /// <summary>
        /// Generate a Database Schema XML file from the database
        /// </summary>
        /// <remarks>New schemas should use <see cref="GenerateSchema(string)"/>, which writes JSON.</remarks>
        /// <param name="targetFile">Filename of file to store Database Schema XML</param>
        public static void GenerateSchemaXML(string targetFile)
        {
            GenerateSchema(targetFile, new DBDefaultRecords(), DBSchemaFormat.Xml);
        }

        /// <summary>
        /// Generate a Database Schema XML file from the database, along with the given default records
        /// </summary>
        /// <remarks>New schemas should use <see cref="GenerateSchema(string, DBDefaultRecords)"/>, which writes JSON.</remarks>
        /// <param name="targetFile">Filename of file to store Database Schema XML</param>
        /// <param name="defaultRecords">List of default records to include in Database Schema XML</param>
        public static void GenerateSchemaXML(string targetFile, DBDefaultRecords defaultRecords)
        {
            GenerateSchema(targetFile, defaultRecords, DBSchemaFormat.Xml);
        }

        /// <summary>
        /// Convert a Database Schema file to another format, for example to move an existing XML schema to JSON.
        /// No database connection is needed.
        /// </summary>
        /// <param name="sourceFile">The schema file to read, in any supported format</param>
        /// <param name="targetFile">The schema file to write</param>
        /// <param name="targetFormat">The format to write</param>
        public static void ConvertSchema(string sourceFile, string targetFile, DBSchemaFormat targetFormat)
        {
            DBSchema schema = ReadSchemaFile(sourceFile, DetectFormat(sourceFile));

            // The default records were read into the tables; the writers take them separately
            DBDefaultRecords defaultRecords = new DBDefaultRecords();
            foreach (DBTable table in schema.Tables.Values)
            {
                if (table.DefaultRecords.Count > 0)
                {
                    defaultRecords.Add(table.Name, table.DefaultRecords);
                }
            }

            WriteSchemaFile(schema, targetFile, defaultRecords, targetFormat);
        }

        /// <summary>
        /// Compare the actual Database Schema with the Schema stored in a schema file, and fix any problems.
        /// The file can be JSON, XML or DBML; the format is detected from its content.
        /// </summary>
        /// <param name="schemaFile">Filename of the JSON, XML or DBML file that contains the Schema to check against</param>
        public static void CheckDatabaseSchema(string schemaFile)
        {
            CheckDatabaseSchema(schemaFile, DetectFormat(schemaFile));
        }

        /// <summary>
        /// Compare the actual Database Schema with the Schema stored in a schema file of the given format, and fix any problems
        /// </summary>
        /// <param name="schemaFile">Filename of the file that contains the Schema to check against</param>
        /// <param name="format">The format of the schema file</param>
        public static void CheckDatabaseSchema(string schemaFile, DBSchemaFormat format)
        {
            DebugLog.Clear();

            // Step 1: Get the Schema as specified in the schema file
            DebugLog.Add("Getting DBSchema as per " + format.ToString().ToUpperInvariant() + " file...");
            DBSchema fileSchema = ReadSchemaFile(schemaFile, format);

            // Step 2: Get the Schema as it is currently in the database
            DebugLog.Add("Getting DBSchema as per database...");
            DBSchema dbSchema = populateDBSchema();

            // Step 3: Create any missing tables
            List<string> createdTables = new List<string>();
            foreach (string tableName in fileSchema.Tables.Keys)
            {
                if (dbSchema.Tables.ContainsKey(tableName) == false)
                {
                    DebugLog.Add("Table '" + tableName + "' not found in database!!");
                    CreateDBTable(fileSchema.Tables[tableName]);

                    createdTables.Add(tableName);
                }
            }

            // Step 3.1: If we had to create any tables, reload the Schema from the database
            if (createdTables.Count > 0)
            {
                dbSchema = populateDBSchema();
            }

            // Step 4: Check all columns exist and all indexes exist
            foreach (DBTable fileTable in fileSchema.Tables.Values)
            {
                DBTable dbTable = dbSchema.Tables[fileTable.Name];

                foreach (DBTable.Column fileColumn in fileTable.Columns)
                {
                    if (dbTable.FindColumn(fileColumn.Name) == null)
                    {
                        DebugLog.Add("Table '" + fileTable.Name + "' does not have column '" + fileColumn.Name + "'!!");
                        CreateDBColumn(dbTable, fileColumn);
                    }
                }

                foreach (DBTable.Index fileIndex in fileTable.Indexes)
                {
                    if (dbTable.FindIndex(fileIndex.Name) == null)
                    {
                        DebugLog.Add("Table '" + fileTable.Name + "' does not have index '" + fileIndex.Name + "'!!");
                        CreateDBIndex(dbTable, fileIndex);
                    }
                }
            }

            // Step 5: Check that all foreign keys exist
            foreach (DBTable fileTable in fileSchema.Tables.Values)
            {
                DBTable dbTable = dbSchema.Tables[fileTable.Name];

                foreach (DBTable.ForeignKey fileFKey in fileTable.ForeignKeys)
                {
                    if (dbTable.FindForeignKey(fileFKey.Name) == null)
                    {
                        DebugLog.Add("Table '" + fileTable.Name + "' does not have foreign key '" + fileFKey.Name + "'!!");
                        CreateDBForeignKey(dbTable, fileFKey);
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
        /// Work out whether a schema file is JSON, XML or DBML from how its content starts, after any
        /// whitespace and // or /* */ comments (both JSON and DBML files can start with a comment)
        /// </summary>
        internal static DBSchemaFormat DetectFormat(string schemaFile)
        {
            string text;
            // StreamReader skips a byte order mark, so the first character read is real content
            using (StreamReader reader = new StreamReader(schemaFile, Encoding.UTF8, true))
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
                    i = (end == -1) ? text.Length : end + 1;
                }
                else if (string.CompareOrdinal(text, i, "/*", 0, 2) == 0)
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = (end == -1) ? text.Length : end + 2;
                }
                else
                {
                    break;
                }
            }

            if (i < text.Length)
            {
                if (text[i] == '{')
                {
                    return DBSchemaFormat.Json;
                }
                if (text[i] == '<')
                {
                    return DBSchemaFormat.Xml;
                }
                if ((i == 0) && text.StartsWith("---", StringComparison.Ordinal))
                {
                    // DBML front matter
                    return DBSchemaFormat.Dbml;
                }

                int wordEnd = i;
                while ((wordEnd < text.Length) && char.IsLetter(text[wordEnd]))
                {
                    wordEnd++;
                }
                if (Array.IndexOf(DbmlKeywords, text.Substring(i, wordEnd - i).ToLowerInvariant()) >= 0)
                {
                    return DBSchemaFormat.Dbml;
                }
            }

            throw new FormatException("'" + schemaFile + "' is not a JSON, XML or DBML schema file");
        }

        /// <summary>The words a DBML file can start with</summary>
        private static readonly string[] DbmlKeywords =
        {
            "table", "ref", "records", "project", "note", "tablegroup", "tablepartial", "enum", "diagramview", "metadata", "use", "reuse",
        };

        internal static DBSchema ReadSchemaFile(string schemaFile, DBSchemaFormat format)
        {
            switch (format)
            {
                case DBSchemaFormat.Json:
                    return DBSchemaJson.Read(schemaFile);
                case DBSchemaFormat.Xml:
                    return DBSchemaXml.Read(schemaFile);
                case DBSchemaFormat.Dbml:
                    return DBSchemaDbml.Read(schemaFile);
                default:
                    throw new ArgumentOutOfRangeException("format", format, "Unknown schema format");
            }
        }

        internal static void WriteSchemaFile(DBSchema schema, string targetFile, DBDefaultRecords defaultRecords, DBSchemaFormat format)
        {
            DebugLog.Add("Creating DBSchema " + format.ToString().ToUpperInvariant() + " file '" + targetFile + "'...");
            switch (format)
            {
                case DBSchemaFormat.Json:
                    DBSchemaJson.Write(schema, targetFile, defaultRecords);
                    break;
                case DBSchemaFormat.Xml:
                    DBSchemaXml.Write(schema, targetFile, defaultRecords);
                    break;
                case DBSchemaFormat.Dbml:
                    DBSchemaDbml.Write(schema, targetFile, defaultRecords);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("format", format, "Unknown schema format");
            }
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
