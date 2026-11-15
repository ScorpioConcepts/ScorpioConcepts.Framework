using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ScorpioConcepts.Framework.DBSchema;

/// <summary>
/// Default records to include when writing a schema file, grouped by table.
/// </summary>
public sealed class DBDefaultRecords
{
    private readonly Dictionary<string, List<DBDefaultRecord>> _records = new(SqlName.Comparer);
    private readonly List<string> _tables = new();

    /// <summary>Creates an empty set of default records.</summary>
    public DBDefaultRecords()
    {
        Tables = _tables.AsReadOnly();
    }

    /// <summary>The tables that have default records, in the order they were first added.</summary>
    public IReadOnlyList<string> Tables { get; }

    /// <summary>The default records for <paramref name="table"/>, or an empty list if it has none.</summary>
    public IReadOnlyList<DBDefaultRecord> this[string table] =>
        _records.TryGetValue(table, out List<DBDefaultRecord>? records) ? records.AsReadOnly() : Array.Empty<DBDefaultRecord>();

    /// <summary>Adds a new, empty default record for <paramref name="table"/>.</summary>
    /// <param name="table">The table the record belongs to.</param>
    /// <returns>The new record, to add values to with <see cref="DBDefaultRecord.AddValue"/>.</returns>
    public DBDefaultRecord AddRecord(string table)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        if (!_records.TryGetValue(table, out List<DBDefaultRecord>? records))
        {
            records = new List<DBDefaultRecord>();
            _records.Add(table, records);
            _tables.Add(table);
        }

        var record = new DBDefaultRecord();
        records.Add(record);
        return record;
    }
}
