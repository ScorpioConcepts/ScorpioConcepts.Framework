using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace ScorpioConcepts.Framework.DBSchema;

/// <summary>
/// The structure of one database table: its columns, primary key, indexes, foreign keys and default records.
/// </summary>
public sealed class DBTable
{
    private readonly List<DBColumn> _columns = new();
    private readonly List<DBIndex> _indexes = new();
    private readonly List<DBForeignKey> _foreignKeys = new();
    private readonly List<DBDefaultRecord> _defaultRecords = new();

    internal DBTable(string name)
    {
        Name = name;
        Columns = _columns.AsReadOnly();
        Indexes = _indexes.AsReadOnly();
        ForeignKeys = _foreignKeys.AsReadOnly();
        DefaultRecords = _defaultRecords.AsReadOnly();
    }

    /// <summary>The table name.</summary>
    public string Name { get; }

    /// <summary>The columns, in table order.</summary>
    public IReadOnlyList<DBColumn> Columns { get; }

    /// <summary>The primary key. Its <see cref="DBPrimaryKey.Name"/> is null when the table has none.</summary>
    public DBPrimaryKey PrimaryKey { get; } = new();

    /// <summary>The indexes, not counting the primary key.</summary>
    public IReadOnlyList<DBIndex> Indexes { get; }

    /// <summary>The foreign keys.</summary>
    public IReadOnlyList<DBForeignKey> ForeignKeys { get; }

    /// <summary>The rows inserted when the table is created.</summary>
    public IReadOnlyList<DBDefaultRecord> DefaultRecords { get; }

    internal DBColumn AddColumn(string name, string dataType, string options)
    {
        var column = new DBColumn(name, dataType, options);
        _columns.Add(column);
        return column;
    }

    internal DBIndex AddIndex(string name, bool clustered, bool unique)
    {
        var index = new DBIndex(name, clustered, unique);
        _indexes.Add(index);
        return index;
    }

    internal DBForeignKey AddForeignKey(string name, string referencedTable)
    {
        var foreignKey = new DBForeignKey(name, referencedTable);
        _foreignKeys.Add(foreignKey);
        return foreignKey;
    }

    internal DBForeignKey AddForeignKey(string name, string column, string referencedTable, string referencedColumn)
    {
        DBForeignKey foreignKey = AddForeignKey(name, referencedTable);
        foreignKey.AddColumn(column, referencedColumn);
        return foreignKey;
    }

    internal DBDefaultRecord AddDefaultRecord()
    {
        var record = new DBDefaultRecord();
        _defaultRecords.Add(record);
        return record;
    }

    internal void AddDefaultRecord(DBDefaultRecord record) => _defaultRecords.Add(record);

    /// <summary>Finds the column with the given name. Like SQL Server, the name is not case sensitive.</summary>
    /// <returns>The column, or null if the table has no such column.</returns>
    public DBColumn? FindColumn(string columnName) => _columns.Find(c => SqlName.Equal(c.Name, columnName));

    /// <summary>Finds the index with the given name. Like SQL Server, the name is not case sensitive.</summary>
    /// <returns>The index, or null if the table has no such index.</returns>
    public DBIndex? FindIndex(string indexName) => _indexes.Find(i => SqlName.Equal(i.Name, indexName));

    /// <summary>Finds the foreign key with the given name. Like SQL Server, the name is not case sensitive.</summary>
    /// <returns>The foreign key, or null if the table has no such foreign key.</returns>
    public DBForeignKey? FindForeignKey(string foreignKeyName) => _foreignKeys.Find(k => SqlName.Equal(k.Name, foreignKeyName));

    /// <inheritdoc/>
    public override string ToString() =>
        "DBTable Name=" + Name + ", Columns=" + _columns.Count.ToString(CultureInfo.InvariantCulture) + ", " + PrimaryKey;
}

/// <summary>A table column.</summary>
public sealed class DBColumn
{
    internal DBColumn(string name, string dataType, string options)
    {
        Name = name;
        DataType = dataType;
        Options = options;
    }

    /// <summary>The column name.</summary>
    public string Name { get; }

    /// <summary>
    /// The data type as written in <c>CREATE TABLE</c>, such as <c>nvarchar(50)</c>, <c>int identity</c> or
    /// <c>int identity(100,5)</c>. For a computed column it is the expression, such as <c>AS ([Qty]*[Price])</c>.
    /// </summary>
    public string DataType { get; }

    /// <summary>
    /// The rest of the column definition, such as <c>NOT NULL DEFAULT ('')</c>,
    /// <c>NOT NULL CONSTRAINT [DF_Name] DEFAULT ('')</c> or, for a computed column, <c>PERSISTED</c>. Empty if
    /// there is none.
    /// </summary>
    public string Options { get; }

    /// <summary>Whether this is a computed column, whose <see cref="DataType"/> starts with <c>AS</c>.</summary>
    public bool IsComputed =>
        DataType.StartsWith("AS ", StringComparison.OrdinalIgnoreCase) || DataType.StartsWith("AS(", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override string ToString() => "DBColumn Name='" + Name + "', DataType='" + DataType + "', Options='" + Options + "'";
}

/// <summary>A table's primary key.</summary>
public sealed class DBPrimaryKey
{
    private readonly List<string> _columns = new();

    internal DBPrimaryKey()
    {
        Columns = _columns.AsReadOnly();
    }

    /// <summary>The constraint name, or null when the table has no primary key.</summary>
    public string? Name { get; internal set; }

    /// <summary>
    /// Whether the primary key is the table's clustered index. True unless the schema says otherwise, which is
    /// also SQL Server's default.
    /// </summary>
    public bool Clustered { get; internal set; } = true;

    /// <summary>The key columns, in key order.</summary>
    public IReadOnlyList<string> Columns { get; }

    internal void AddColumn(string columnName)
    {
        if (_columns.Exists(c => SqlName.Equal(c, columnName)))
        {
            throw new ArgumentException("Column '" + columnName + "' is already in the primary key", nameof(columnName));
        }

        _columns.Add(columnName);
    }

    /// <inheritdoc/>
    public override string ToString() => "DBPrimaryKey Name=" + Name + ", Columns=" + _columns.Count.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A table index, other than the primary key.</summary>
public sealed class DBIndex
{
    private readonly List<DBIndexColumn> _columns = new();

    internal DBIndex(string name, bool clustered, bool unique)
    {
        Name = name;
        Clustered = clustered;
        Unique = unique;
        Columns = _columns.AsReadOnly();
    }

    /// <summary>The index name.</summary>
    public string Name { get; }

    /// <summary>Whether this is a <c>UNIQUE</c> index.</summary>
    public bool Unique { get; }

    /// <summary>Whether this is a <c>CLUSTERED</c> index.</summary>
    public bool Clustered { get; }

    /// <summary>The key columns, in key order.</summary>
    public IReadOnlyList<DBIndexColumn> Columns { get; }

    internal void AddColumn(string columnName, bool descending)
    {
        if (_columns.Exists(c => SqlName.Equal(c.Name, columnName)))
        {
            throw new ArgumentException("Column '" + columnName + "' is already in index '" + Name + "'", nameof(columnName));
        }

        _columns.Add(new DBIndexColumn(columnName, descending));
    }

    /// <inheritdoc/>
    public override string ToString() => "DBIndex Name=" + Name + ", Columns=" + _columns.Count.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One key column of an index.</summary>
public sealed class DBIndexColumn
{
    internal DBIndexColumn(string name, bool descending)
    {
        Name = name;
        Descending = descending;
    }

    /// <summary>The column name.</summary>
    public string Name { get; }

    /// <summary>Whether the column is sorted in descending order.</summary>
    public bool Descending { get; }

    /// <inheritdoc/>
    public override string ToString() => Descending ? Name + " DESC" : Name;
}

/// <summary>A foreign key from one or more columns of a table to the matching columns of another table.</summary>
public sealed class DBForeignKey
{
    private readonly List<string> _columns = new();
    private readonly List<string> _referencedColumns = new();

    internal DBForeignKey(string name, string referencedTable)
    {
        Name = name;
        ReferencedTable = referencedTable;
        Columns = _columns.AsReadOnly();
        ReferencedColumns = _referencedColumns.AsReadOnly();
    }

    /// <summary>The constraint name.</summary>
    public string Name { get; }

    /// <summary>The columns in this table, in key order.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>The table the key refers to.</summary>
    public string ReferencedTable { get; }

    /// <summary>The columns in <see cref="ReferencedTable"/>, matching <see cref="Columns"/> one for one.</summary>
    public IReadOnlyList<string> ReferencedColumns { get; }

    internal void AddColumn(string column, string referencedColumn)
    {
        if (_columns.Exists(c => SqlName.Equal(c, column)))
        {
            throw new ArgumentException("Column '" + column + "' is already in foreign key '" + Name + "'", nameof(column));
        }

        _columns.Add(column);
        _referencedColumns.Add(referencedColumn);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        "DBForeignKey Name=" + Name + ", Columns=" + string.Join(",", _columns) + ", ReferencedTable=" + ReferencedTable
        + ", ReferencedColumns=" + string.Join(",", _referencedColumns);
}
