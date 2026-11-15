using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ScorpioConcepts.Framework.DBSchema;

/// <summary>
/// Reads the structure of a SQL Server database, writes it to schema files, and brings a database up to date with
/// a schema file. Each instance works on one database.
/// </summary>
/// <remarks>
/// It only ever adds tables, columns, indexes and foreign keys; it never drops or changes existing ones. If the
/// connection is closed, each call opens it and closes it again when it finishes. An instance is not meant to be
/// used from several threads at once, just like the <see cref="SqlConnection"/> it uses.
/// </remarks>
public sealed partial class DBSchemaManager
{
    private readonly ILogger _logger;

    /// <summary>Creates a manager for the database that <paramref name="connection"/> connects to.</summary>
    /// <param name="connection">
    /// The connection to use. It can be open or closed. The manager never disposes it; that stays with the caller.
    /// </param>
    /// <param name="logger">Where to log what the manager does. Nothing is logged if it is null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public DBSchemaManager(SqlConnection connection, ILogger? logger = null)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The connection the manager uses.</summary>
    public SqlConnection Connection { get; }

    /// <summary>Reads the structure of every table in the database's <c>dbo</c> schema.</summary>
    public DBSchemaDefinition ReadDatabaseSchema()
    {
        using (OpenConnection())
        {
            return ReadDatabase();
        }
    }

    /// <summary>
    /// Writes the structure of the database to a schema file, optionally with default records.
    /// </summary>
    /// <param name="targetFile">The path of the file to write. An existing file is replaced.</param>
    /// <param name="defaultRecords">Default records to include. Records for tables that don't exist are left out.</param>
    /// <param name="format">The format to write. JSON is the default.</param>
    public void GenerateSchema(string targetFile, DBDefaultRecords? defaultRecords = null, DBSchemaFormat format = DBSchemaFormat.Json)
    {
        if (targetFile is null) throw new ArgumentNullException(nameof(targetFile));

        DBSchemaDefinition schema = ReadDatabaseSchema();
        if (defaultRecords is not null) schema.AddDefaultRecords(defaultRecords);

        LogWritingSchemaFile(format, targetFile);
        schema.Save(targetFile, format);
    }

    /// <summary>
    /// Compares the database with a schema file and adds whatever is missing. Whether the file is JSON or XML is
    /// worked out from its content.
    /// </summary>
    /// <param name="schemaFile">The path of the schema file.</param>
    public void CheckDatabaseSchema(string schemaFile) => CheckDatabaseSchema(DBSchemaDefinition.Load(schemaFile));

    /// <summary>Compares the database with a schema file in the given format and adds whatever is missing.</summary>
    /// <param name="schemaFile">The path of the schema file.</param>
    /// <param name="format">The format of the file.</param>
    public void CheckDatabaseSchema(string schemaFile, DBSchemaFormat format) =>
        CheckDatabaseSchema(DBSchemaDefinition.Load(schemaFile, format));

    /// <summary>
    /// Compares the database with <paramref name="schema"/> and adds whatever is missing: tables (with their
    /// primary key and default records), then columns and indexes, then foreign keys.
    /// </summary>
    /// <param name="schema">The structure the database should have.</param>
    public void CheckDatabaseSchema(DBSchemaDefinition schema)
    {
        if (schema is null) throw new ArgumentNullException(nameof(schema));

        using (OpenConnection())
        {
            DBSchemaDefinition database = ReadDatabase();

            bool createdTables = false;
            foreach (DBTable table in schema.TablesInOrder)
            {
                if (!database.Tables.ContainsKey(table.Name))
                {
                    LogMissingTable(table.Name);
                    CreateTable(table);
                    createdTables = true;
                }
            }

            if (createdTables)
            {
                database = ReadDatabase();
            }

            foreach (DBTable fileTable in schema.TablesInOrder)
            {
                DBTable dbTable = database.Tables[fileTable.Name];

                foreach (DBColumn column in fileTable.Columns)
                {
                    if (dbTable.FindColumn(column.Name) is null)
                    {
                        LogMissingObject(fileTable.Name, "column", column.Name);
                        CreateColumn(dbTable, column);
                    }
                }

                foreach (DBIndex index in fileTable.Indexes)
                {
                    if (dbTable.FindIndex(index.Name) is null)
                    {
                        LogMissingObject(fileTable.Name, "index", index.Name);
                        CreateIndex(dbTable, index);
                    }
                }
            }

            // Foreign keys last, so the tables and columns they refer to exist
            foreach (DBTable fileTable in schema.TablesInOrder)
            {
                DBTable dbTable = database.Tables[fileTable.Name];

                foreach (DBForeignKey foreignKey in fileTable.ForeignKeys)
                {
                    if (dbTable.FindForeignKey(foreignKey.Name) is null)
                    {
                        LogMissingObject(fileTable.Name, "foreign key", foreignKey.Name);
                        CreateForeignKey(dbTable, foreignKey);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Converts a schema file to another format, for example to move an existing XML schema to JSON. No database
    /// connection is needed.
    /// </summary>
    /// <param name="sourceFile">The schema file to read, in either format.</param>
    /// <param name="targetFile">The schema file to write.</param>
    /// <param name="targetFormat">The format to write.</param>
    public static void ConvertSchema(string sourceFile, string targetFile, DBSchemaFormat targetFormat) =>
        DBSchemaDefinition.Load(sourceFile).Save(targetFile, targetFormat);

    /// <summary>Opens the connection if it is closed, and closes it again when disposed.</summary>
    private ConnectionScope OpenConnection()
    {
        if (Connection.State != ConnectionState.Closed) return default;

        Connection.Open();
        return new ConnectionScope(Connection);
    }

    private readonly struct ConnectionScope(SqlConnection? connectionToClose) : IDisposable
    {
        public void Dispose() => connectionToClose?.Close();
    }

    private DBSchemaDefinition ReadDatabase()
    {
        var schema = new DBSchemaDefinition();

        var tables = new List<string>();
        LogReadingTables();
        using (var command = new SqlCommand("exec sp_tables", Connection))
        using (SqlDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string tableName = Text(reader["TABLE_NAME"]);
                string tableOwner = Text(reader["TABLE_OWNER"]);
                string tableType = Text(reader["TABLE_TYPE"]);
                if (tableOwner == "dbo" && tableType == "TABLE" && tableName != "dtproperties")
                {
                    tables.Add(tableName);
                }
            }
        }
        LogFoundTables(tables.Count);

        Version serverVersion = new Version(Connection.ServerVersion);

        foreach (string tableName in tables)
        {
            LogReadingTable(tableName);
            DBTable table = schema.AddTable(tableName);
            string qualifiedName = "[dbo].[" + tableName + "]";

            const string columnsSql = """
                SELECT c.name, t.name AS type_name, c.max_length, c.precision, c.scale,
                    c.is_nullable, c.is_identity, dc.definition AS default_definition
                FROM sys.columns c
                JOIN sys.types t ON (t.user_type_id = c.user_type_id)
                LEFT JOIN sys.default_constraints dc ON (dc.object_id = c.default_object_id)
                WHERE c.object_id = OBJECT_ID(@table)
                ORDER BY c.column_id
                """;
            using (var command = new SqlCommand(columnsSql, Connection))
            {
                command.Parameters.AddWithValue("@table", qualifiedName);
                using SqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string columnType = FormatDataType(
                        reader.GetString(1),
                        reader.GetInt16(2),
                        reader.GetByte(3),
                        reader.GetByte(4),
                        reader.GetBoolean(6));
                    string columnOptions = reader.GetBoolean(5) ? "NULL" : "NOT NULL";
                    if (!reader.IsDBNull(7))
                    {
                        columnOptions += " DEFAULT " + reader.GetString(7);
                    }

                    table.AddColumn(reader.GetString(0), columnType, columnOptions);
                }
            }

            using (SqlCommand command = Command("exec sp_pkeys [" + tableName + "]"))
            using (SqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    table.PrimaryKey.Name = Text(reader["PK_NAME"]);
                    table.PrimaryKey.AddColumn(Text(reader["COLUMN_NAME"]));
                }
            }

            if (serverVersion.Major > 8)
            {
                const string indexesSql = """
                    SELECT idx.name AS idx_name, idx.type_desc, idx.is_unique, cols.name AS col_name, ixc.is_descending_key
                    FROM sys.indexes idx
                    JOIN sys.index_columns ixc ON (idx.index_id = ixc.index_id)
                    JOIN sys.columns cols ON (ixc.column_id = cols.column_id)
                    WHERE idx.object_id = OBJECT_ID(@table)
                    AND ixc.object_id = idx.object_id
                    AND cols.object_id = idx.object_id
                    AND idx.is_primary_key = 0
                    AND ixc.is_included_column = 0
                    ORDER BY idx.name, ixc.key_ordinal
                    """;
                using var command = new SqlCommand(indexesSql, Connection);
                command.Parameters.AddWithValue("@table", qualifiedName);
                using SqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string indexName = reader.GetString(0);
                    DBIndex index = table.FindIndex(indexName)
                        ?? table.AddIndex(indexName, reader.GetString(1) == "CLUSTERED", reader.GetBoolean(2));
                    index.AddColumn(reader.GetString(3), reader.GetBoolean(4));
                }
            }

            using (SqlCommand command = Command("exec sp_fkeys @fktable_name=[" + tableName + "]"))
            using (SqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    table.AddForeignKey(
                        Text(reader["FK_NAME"]),
                        Text(reader["FKCOLUMN_NAME"]),
                        Text(reader["PKTABLE_NAME"]),
                        Text(reader["PKCOLUMN_NAME"]));
                }
            }
        }

        return schema;
    }

    private static string Text(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    /// <summary>
    /// Builds a column data type, including its size, the way it would be written in a <c>CREATE TABLE</c> statement.
    /// </summary>
    /// <param name="typeName">The type name from sys.types.</param>
    /// <param name="maxLength">sys.columns.max_length: the size in bytes, or -1 for (max).</param>
    /// <param name="precision">sys.columns.precision.</param>
    /// <param name="scale">sys.columns.scale.</param>
    /// <param name="isIdentity">Whether the column is an IDENTITY column.</param>
    internal static string FormatDataType(string typeName, short maxLength, byte precision, byte scale, bool isIdentity)
    {
        string dataType = typeName;
        switch (typeName.ToUpperInvariant())
        {
            case "CHAR":
            case "VARCHAR":
            case "BINARY":
            case "VARBINARY":
                dataType += maxLength == -1 ? "(max)" : "(" + maxLength.ToString(CultureInfo.InvariantCulture) + ")";
                break;
            case "NCHAR":
            case "NVARCHAR":
                // max_length is in bytes, and each Unicode character takes two
                dataType += maxLength == -1 ? "(max)" : "(" + (maxLength / 2).ToString(CultureInfo.InvariantCulture) + ")";
                break;
            case "DECIMAL":
            case "NUMERIC":
                dataType += "(" + precision.ToString(CultureInfo.InvariantCulture) + "," + scale.ToString(CultureInfo.InvariantCulture) + ")";
                break;
            case "DATETIME2":
            case "DATETIMEOFFSET":
            case "TIME":
                dataType += "(" + scale.ToString(CultureInfo.InvariantCulture) + ")";
                break;
            case "FLOAT":
                if (precision != 53)
                {
                    dataType += "(" + precision.ToString(CultureInfo.InvariantCulture) + ")";
                }
                break;
        }

        if (isIdentity)
        {
            dataType += " identity";
        }

        return dataType;
    }

    private static bool IsIdentity(DBColumn column) => column.DataType.EndsWith("identity", StringComparison.OrdinalIgnoreCase);

    private void CreateTable(DBTable table)
    {
        LogCreatingTable(table.Name);

        var sql = new StringBuilder();
        sql.Append("CREATE TABLE [").Append(table.Name).AppendLine("] (");
        var definitions = new List<string>();
        foreach (DBColumn column in table.Columns)
        {
            definitions.Add("\t[" + column.Name + "] " + column.DataType + (IsIdentity(column) ? "(1,1)" : "") + " " + column.Options);
        }

        if (!string.IsNullOrEmpty(table.PrimaryKey.Name))
        {
            definitions.Add("\tCONSTRAINT [" + table.PrimaryKey.Name + "] PRIMARY KEY CLUSTERED (" + Environment.NewLine
                + string.Join("," + Environment.NewLine, table.PrimaryKey.Columns.Select(c => "\t\t[" + c + "] ASC"))
                + Environment.NewLine + "\t)");
        }
        sql.AppendLine(string.Join("," + Environment.NewLine, definitions)).AppendLine(");");

        Execute(sql.ToString());

        foreach (DBDefaultRecord record in table.DefaultRecords)
        {
            InsertDefaultRecord(table, record);
        }
    }

    private void InsertDefaultRecord(DBTable table, DBDefaultRecord record)
    {
        LogInsertingDefaultRecord(table.Name);

        var values = record.OrderedValues.ToList();
        string sql = "INSERT INTO [" + table.Name + "](" + Environment.NewLine
            + string.Join("," + Environment.NewLine, values.Select(kvp => "\t[" + kvp.Key + "]"))
            + Environment.NewLine + ") VALUES(" + Environment.NewLine
            + string.Join("," + Environment.NewLine, values.Select(kvp => "\t@" + kvp.Key))
            + Environment.NewLine + ");" + Environment.NewLine;

        using SqlCommand command = Command(sql);
        foreach (KeyValuePair<string, object?> kvp in values)
        {
            command.Parameters.AddWithValue("@" + kvp.Key, kvp.Value ?? DBNull.Value);
        }
        command.ExecuteNonQuery();
    }

    private void CreateColumn(DBTable table, DBColumn column)
    {
        LogCreating("column", column.Name, table.Name);

        Execute("ALTER TABLE [" + table.Name + "] ADD " + Environment.NewLine
            + "\t[" + column.Name + "] " + column.DataType + (IsIdentity(column) ? "(1,1)" : "") + " " + column.Options + " ;" + Environment.NewLine);
    }

    private void CreateIndex(DBTable table, DBIndex index)
    {
        LogCreating("index", index.Name, table.Name);

        Execute("CREATE" + (index.Unique ? " UNIQUE" : "") + (index.Clustered ? " CLUSTERED" : "")
            + " INDEX [" + index.Name + "] ON [" + table.Name + "] (" + Environment.NewLine
            + string.Join("," + Environment.NewLine, index.Columns.Select(c => "\t[" + c.Name + "]" + (c.Descending ? " DESC" : "")))
            + ") ;" + Environment.NewLine);
    }

    private void CreateForeignKey(DBTable table, DBForeignKey foreignKey)
    {
        LogCreating("foreign key", foreignKey.Name, table.Name);

        Execute("ALTER TABLE [" + table.Name + "] WITH CHECK ADD CONSTRAINT [" + foreignKey.Name + "]" + Environment.NewLine
            + "\tFOREIGN KEY([" + foreignKey.Column + "])" + Environment.NewLine
            + "\t\tREFERENCES [" + foreignKey.ReferencedTable + "] ([" + foreignKey.ReferencedColumn + "]) ;" + Environment.NewLine);
    }

    private void Execute(string sql)
    {
        using SqlCommand command = Command(sql);
        command.ExecuteNonQuery();
    }

    // Identifiers come from the schema file or the database itself, and values are always parameters
    private SqlCommand Command(string sql) => new(sql, Connection);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reading database tables")]
    private partial void LogReadingTables();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Found {Count} tables")]
    private partial void LogFoundTables(int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reading the structure of table '{Table}'")]
    private partial void LogReadingTable(string table);

    [LoggerMessage(Level = LogLevel.Information, Message = "Writing the schema as {Format} to '{File}'")]
    private partial void LogWritingSchemaFile(DBSchemaFormat format, string file);

    [LoggerMessage(Level = LogLevel.Information, Message = "Table '{Table}' is not in the database")]
    private partial void LogMissingTable(string table);

    [LoggerMessage(Level = LogLevel.Information, Message = "Table '{Table}' has no {Kind} '{Name}'")]
    private partial void LogMissingObject(string table, string kind, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Creating table '{Table}'")]
    private partial void LogCreatingTable(string table);

    [LoggerMessage(Level = LogLevel.Information, Message = "Creating {Kind} '{Name}' in table '{Table}'")]
    private partial void LogCreating(string kind, string name, string table);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inserting a default record into table '{Table}'")]
    private partial void LogInsertingDefaultRecord(string table);
}
