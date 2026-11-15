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
    private SqlTransaction? _transaction;

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
    /// <remarks>
    /// Everything runs in one transaction, so if any statement fails, the database is left as it was and the
    /// exception is thrown. Names are compared without regard to case, as SQL Server does.
    /// </remarks>
    /// <param name="schema">The structure the database should have.</param>
    public void CheckDatabaseSchema(DBSchemaDefinition schema)
    {
        if (schema is null) throw new ArgumentNullException(nameof(schema));

        using (OpenConnection())
        using (SqlTransaction transaction = Connection.BeginTransaction())
        {
            _transaction = transaction;
            try
            {
                CheckInTransaction(schema);
                transaction.Commit();
            }
            finally
            {
                // Disposing an uncommitted transaction rolls it back
                _transaction = null;
            }
        }
    }

    private void CheckInTransaction(DBSchemaDefinition schema)
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

        // One query per catalog view for the whole database, rather than several per table
        LogReadingTables();
        ReadRows("""
            SELECT t.name
            FROM sys.tables t
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.is_ms_shipped = 0 AND t.name <> N'dtproperties'
            ORDER BY t.name
            """, reader => schema.AddTable(reader.GetString(0)));
        LogFoundTables(schema.Tables.Count);

        ReadRows("""
            SELECT t.name, c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
                CONVERT(nvarchar(40), ic.seed_value), CONVERT(nvarchar(40), ic.increment_value),
                dc.name, dc.is_system_named, dc.definition, cc.definition, cc.is_persisted
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.is_ms_shipped = 0
            ORDER BY t.name, c.column_id
            """, reader =>
        {
            if (!schema.Tables.TryGetValue(reader.GetString(0), out DBTable? table)) return;

            bool nullable = reader.GetBoolean(6);
            if (!reader.IsDBNull(13))
            {
                // A computed column has an expression instead of a type. NULL and NOT NULL are only allowed
                // when it is persisted.
                bool persisted = reader.GetBoolean(14);
                string options = persisted ? (nullable ? "PERSISTED" : "PERSISTED NOT NULL") : "";
                table.AddColumn(reader.GetString(1), "AS " + reader.GetString(13), options);
                return;
            }

            string dataType = FormatDataType(
                reader.GetString(2), reader.GetInt16(3), reader.GetByte(4), reader.GetByte(5), reader.GetBoolean(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9));

            string columnOptions = nullable ? "NULL" : "NOT NULL";
            if (!reader.IsDBNull(12))
            {
                // Keep a default's name unless SQL Server made it up
                if (!reader.GetBoolean(11)) columnOptions += " CONSTRAINT " + SqlName.Quote(reader.GetString(10));
                columnOptions += " DEFAULT " + reader.GetString(12);
            }

            table.AddColumn(reader.GetString(1), dataType, columnOptions);
        });

        // The primary key and the other indexes, which are both rowstore B-tree indexes (types 1 and 2)
        ReadRows("""
            SELECT t.name, i.name, i.is_primary_key, i.type_desc, i.is_unique, c.name, ic.is_descending_key
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.is_ms_shipped = 0
                AND i.type IN (1, 2) AND i.is_hypothetical = 0 AND ic.is_included_column = 0
            ORDER BY t.name, i.name, ic.key_ordinal
            """, reader =>
        {
            if (!schema.Tables.TryGetValue(reader.GetString(0), out DBTable? table)) return;

            string indexName = reader.GetString(1);
            bool clustered = reader.GetString(3) == "CLUSTERED";
            if (reader.GetBoolean(2))
            {
                table.PrimaryKey.Name = indexName;
                table.PrimaryKey.Clustered = clustered;
                table.PrimaryKey.AddColumn(reader.GetString(5));
                return;
            }

            DBIndex index = table.FindIndex(indexName) ?? table.AddIndex(indexName, clustered, reader.GetBoolean(4));
            index.AddColumn(reader.GetString(5), reader.GetBoolean(6));
        });

        ReadRows("""
            SELECT t.name, fk.name, rt.name, pc.name, rc.name
            FROM sys.foreign_keys fk
            JOIN sys.tables t ON t.object_id = fk.parent_object_id
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.is_ms_shipped = 0
            ORDER BY t.name, fk.name, fkc.constraint_column_id
            """, reader =>
        {
            if (!schema.Tables.TryGetValue(reader.GetString(0), out DBTable? table)) return;

            string keyName = reader.GetString(1);
            DBForeignKey key = table.FindForeignKey(keyName) ?? table.AddForeignKey(keyName, reader.GetString(2));
            key.AddColumn(reader.GetString(3), reader.GetString(4));
        });

        foreach (DBTable table in schema.TablesInOrder)
        {
            LogReadTable(table.Name, table.Columns.Count, table.Indexes.Count, table.ForeignKeys.Count);
        }

        return schema;
    }

    private void ReadRows(string sql, Action<SqlDataReader> readRow)
    {
        using SqlCommand command = Command(sql);
        using SqlDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            readRow(reader);
        }
    }

    /// <summary>
    /// Builds a column data type, including its size, the way it would be written in a <c>CREATE TABLE</c> statement.
    /// </summary>
    /// <param name="typeName">The type name from sys.types.</param>
    /// <param name="maxLength">sys.columns.max_length: the size in bytes, or -1 for (max).</param>
    /// <param name="precision">sys.columns.precision.</param>
    /// <param name="scale">sys.columns.scale.</param>
    /// <param name="isIdentity">Whether the column is an IDENTITY column.</param>
    /// <param name="identitySeed">The identity seed, if it is one. Written only when it isn't the default of 1.</param>
    /// <param name="identityIncrement">The identity increment, if it is one. Written only when it isn't the default of 1.</param>
    internal static string FormatDataType(string typeName, short maxLength, byte precision, byte scale, bool isIdentity,
        string? identitySeed = null, string? identityIncrement = null)
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
            string seed = identitySeed ?? "1";
            string increment = identityIncrement ?? "1";
            if (seed != "1" || increment != "1")
            {
                dataType += "(" + seed + "," + increment + ")";
            }
        }

        return dataType;
    }

    /// <summary>
    /// A column definition for <c>CREATE TABLE</c> or <c>ALTER TABLE ADD</c>. A plain <c>identity</c> gets the
    /// default seed and increment, which older SQL Server versions required to be written out.
    /// </summary>
    private static string ColumnDefinition(DBColumn column)
    {
        string dataType = column.DataType;
        if (dataType.EndsWith("identity", StringComparison.OrdinalIgnoreCase)) dataType += "(1,1)";

        string definition = SqlName.Quote(column.Name) + " " + dataType;
        return column.Options.Length == 0 ? definition : definition + " " + column.Options;
    }

    private void CreateTable(DBTable table)
    {
        LogCreatingTable(table.Name);

        var definitions = table.Columns.Select(c => "\t" + ColumnDefinition(c)).ToList();
        if (table.PrimaryKey.Columns.Count > 0)
        {
            string constraint = string.IsNullOrEmpty(table.PrimaryKey.Name) ? "" : "CONSTRAINT " + SqlName.Quote(table.PrimaryKey.Name!) + " ";
            definitions.Add("\t" + constraint + "PRIMARY KEY " + (table.PrimaryKey.Clustered ? "CLUSTERED" : "NONCLUSTERED")
                + " (" + string.Join(", ", table.PrimaryKey.Columns.Select(c => SqlName.Quote(c) + " ASC")) + ")");
        }

        Execute("CREATE TABLE " + SqlName.Table(table.Name) + " (" + Environment.NewLine
            + string.Join("," + Environment.NewLine, definitions) + Environment.NewLine + ");");

        foreach (DBDefaultRecord record in table.DefaultRecords)
        {
            InsertDefaultRecord(table, record);
        }
    }

    private void InsertDefaultRecord(DBTable table, DBDefaultRecord record)
    {
        LogInsertingDefaultRecord(table.Name);

        // Positional parameters, so a column name never has to be a valid parameter name
        var values = record.OrderedValues.ToList();
        string sql = "INSERT INTO " + SqlName.Table(table.Name) + " (" + SqlName.List(values.Select(v => v.Key)) + ")"
            + " VALUES (" + string.Join(", ", values.Select((_, i) => "@p" + i.ToString(CultureInfo.InvariantCulture))) + ");";

        using SqlCommand command = Command(sql);
        for (int i = 0; i < values.Count; i++)
        {
            command.Parameters.AddWithValue("@p" + i.ToString(CultureInfo.InvariantCulture), values[i].Value ?? DBNull.Value);
        }
        command.ExecuteNonQuery();
    }

    private void CreateColumn(DBTable table, DBColumn column)
    {
        LogCreating("column", column.Name, table.Name);

        Execute("ALTER TABLE " + SqlName.Table(table.Name) + " ADD " + ColumnDefinition(column) + ";");
    }

    private void CreateIndex(DBTable table, DBIndex index)
    {
        LogCreating("index", index.Name, table.Name);

        Execute("CREATE" + (index.Unique ? " UNIQUE" : "") + (index.Clustered ? " CLUSTERED" : " NONCLUSTERED")
            + " INDEX " + SqlName.Quote(index.Name) + " ON " + SqlName.Table(table.Name)
            + " (" + string.Join(", ", index.Columns.Select(c => SqlName.Quote(c.Name) + (c.Descending ? " DESC" : ""))) + ");");
    }

    private void CreateForeignKey(DBTable table, DBForeignKey foreignKey)
    {
        LogCreating("foreign key", foreignKey.Name, table.Name);

        Execute("ALTER TABLE " + SqlName.Table(table.Name) + " WITH CHECK ADD CONSTRAINT " + SqlName.Quote(foreignKey.Name)
            + " FOREIGN KEY (" + SqlName.List(foreignKey.Columns) + ")"
            + " REFERENCES " + SqlName.Table(foreignKey.ReferencedTable) + " (" + SqlName.List(foreignKey.ReferencedColumns) + ");");
    }

    private void Execute(string sql)
    {
        using SqlCommand command = Command(sql);
        command.ExecuteNonQuery();
    }

    // Identifiers are always quoted with SqlName, and values are always parameters. Only a column's DataType and
    // Options are written as they are, because they are SQL: a schema file is code.
    private SqlCommand Command(string sql) => new(sql, Connection) { Transaction = _transaction };

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reading database tables")]
    private partial void LogReadingTables();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Found {Count} tables")]
    private partial void LogFoundTables(int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Table '{Table}' has {Columns} columns, {Indexes} indexes and {ForeignKeys} foreign keys")]
    private partial void LogReadTable(string table, int columns, int indexes, int foreignKeys);

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
