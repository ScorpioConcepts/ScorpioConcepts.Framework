using Microsoft.Data.SqlClient;

namespace ScorpioConcepts.Framework.DBSchema.IntegrationTests;

/// <summary>
/// Gives each test its own empty database, points DBSchema at it, and provides helpers
/// for inspecting the resulting structure.
/// </summary>
public abstract class DatabaseTestBase(SqlServerFixture fixture) : IAsyncLifetime
{
    private readonly List<string> _files = new();

    protected SqlConnection Connection { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Connection = await fixture.CreateDatabaseAsync();
        DBSchema.InitializeDB(Connection);
    }

    public async ValueTask DisposeAsync()
    {
        if (Connection is not null) await Connection.DisposeAsync();
        foreach (var file in _files)
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    protected Task<SqlConnection> CreateOtherDatabaseAsync() => fixture.CreateDatabaseAsync();

    protected string TempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dbschema-{Guid.NewGuid():N}.xml");
        _files.Add(path);
        return path;
    }

    protected string WriteSchema(string xml)
    {
        string path = TempFile();
        File.WriteAllText(path, xml);
        return path;
    }

    protected static string FindRepoFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not find " + relativePath + " above " + AppContext.BaseDirectory);
    }

    protected void Execute(string sql) => Execute(Connection, sql);

    protected static void Execute(SqlConnection connection, string sql)
    {
        using var command = new SqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    protected T Scalar<T>(string sql)
    {
        using var command = new SqlCommand(sql, Connection);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    protected List<T> Query<T>(string sql, Func<SqlDataReader, T> map)
    {
        using var command = new SqlCommand(sql, Connection);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    protected string[] Tables() =>
        Query("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME", r => r.GetString(0)).ToArray();

    /// <summary>Columns of a table as "name type[(length)] NULL|NOT NULL" strings, in column order.</summary>
    protected string[] Columns(string table) =>
        Query($"""
            SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{table}' ORDER BY ORDINAL_POSITION
            """,
            r => $"{r.GetString(0)} {r.GetString(1)}{(r.IsDBNull(2) ? "" : $"({r.GetInt32(2)})")} {(r.GetString(3) == "YES" ? "NULL" : "NOT NULL")}")
        .ToArray();

    protected bool IsIdentity(string table, string column) =>
        Scalar<int>($"SELECT COLUMNPROPERTY(OBJECT_ID('{table}'), '{column}', 'IsIdentity')") == 1;

    protected string? ColumnDefault(string table, string column) =>
        Query($"SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'",
            r => r.IsDBNull(0) ? null : r.GetString(0)).Single();

    /// <summary>The primary key of a table as "name: col1,col2".</summary>
    protected string? PrimaryKey(string table) =>
        Query($"""
            SELECT kc.name, STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.key_constraints kc
            JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE kc.type = 'PK' AND kc.parent_object_id = OBJECT_ID('{table}')
            GROUP BY kc.name
            """, r => $"{r.GetString(0)}: {r.GetString(1)}").SingleOrDefault();

    /// <summary>Non-primary-key indexes of a table as "name [UNIQUE] [CLUSTERED]: col1[ DESC],col2".</summary>
    protected string[] Indexes(string table) =>
        Query($"""
            SELECT i.name, i.is_unique, i.type_desc,
                   STRING_AGG(c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('{table}') AND i.is_primary_key = 0
            GROUP BY i.name, i.is_unique, i.type_desc
            ORDER BY i.name
            """,
            r => $"{r.GetString(0)}{(r.GetBoolean(1) ? " UNIQUE" : "")}{(r.GetString(2) == "CLUSTERED" ? " CLUSTERED" : "")}: {r.GetString(3)}")
        .ToArray();

    /// <summary>Foreign keys of a table as "name: column -> table.column".</summary>
    protected string[] ForeignKeys(string table) =>
        Query($"""
            SELECT fk.name, pc.name, OBJECT_NAME(fk.referenced_object_id), rc.name
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE fk.parent_object_id = OBJECT_ID('{table}')
            ORDER BY fk.name
            """, r => $"{r.GetString(0)}: {r.GetString(1)} -> {r.GetString(2)}.{r.GetString(3)}")
        .ToArray();
}
