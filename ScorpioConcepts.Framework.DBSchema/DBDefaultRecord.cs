using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ScorpioConcepts.Framework.DBSchema;

/// <summary>
/// A row to insert when its table is created, as column names and values.
/// </summary>
public sealed class DBDefaultRecord
{
    private readonly Dictionary<string, object?> _values = new(SqlName.Comparer);
    private readonly List<string> _order = new();

    internal DBDefaultRecord()
    {
        Values = new ReadOnlyDictionary<string, object?>(_values);
        Columns = _order.AsReadOnly();
    }

    /// <summary>The column values, in the order they were added. A null value is stored as SQL <c>NULL</c>.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; }

    /// <summary>The column names, in the order they were added.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>Adds a column value to the record.</summary>
    /// <param name="column">The column name.</param>
    /// <param name="value">The value. Null and <see cref="DBNull"/> both mean SQL <c>NULL</c>.</param>
    /// <returns>This record, so calls can be chained.</returns>
    /// <exception cref="ArgumentException">The record already has a value for <paramref name="column"/>.</exception>
    public DBDefaultRecord AddValue(string column, object? value)
    {
        if (column is null) throw new ArgumentNullException(nameof(column));
        if (_values.ContainsKey(column))
        {
            throw new ArgumentException("Column '" + column + "' already has a value assigned", nameof(column));
        }

        _values.Add(column, value);
        _order.Add(column);
        return this;
    }

    /// <summary>The values in column order.</summary>
    internal IEnumerable<KeyValuePair<string, object?>> OrderedValues => _order.Select(c => new KeyValuePair<string, object?>(c, _values[c]));

    /// <summary>Shows the first two values, and how many more there are.</summary>
    public override string ToString()
    {
        if (_order.Count == 0)
        {
            return "DBDefaultRecord: No Data";
        }

        var sb = new StringBuilder("DBDefaultRecord: ");
        sb.Append(string.Join(",", OrderedValues.Take(2).Select(kvp => kvp.Key + "=" + Format(kvp.Value))));
        if (_order.Count > 2)
        {
            sb.Append(", ").Append((_order.Count - 2).ToString(CultureInfo.InvariantCulture)).Append(" other...");
        }

        return sb.ToString();
    }

    private static string Format(object? value) =>
        value is null || value is DBNull ? "NULL" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}
