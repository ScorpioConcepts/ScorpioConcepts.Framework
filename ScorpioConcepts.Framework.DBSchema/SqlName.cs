using System;
using System.Collections.Generic;
using System.Linq;

namespace ScorpioConcepts.Framework.DBSchema;

/// <summary>Rules for SQL Server identifiers: how they compare, and how to write them safely in SQL.</summary>
internal static class SqlName
{
    /// <summary>
    /// SQL Server compares names without regard to case under the default collations, so the schema does too:
    /// a file that says "customers" means the table the database calls "Customers".
    /// </summary>
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static bool Equal(string? a, string? b) => Comparer.Equals(a, b);

    /// <summary>
    /// Writes <paramref name="name"/> as a bracketed identifier, doubling any closing bracket the way QUOTENAME
    /// does, so a name can never end the identifier early and run SQL of its own.
    /// </summary>
    public static string Quote(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        return "[" + name.Replace("]", "]]") + "]";
    }

    /// <summary>A table in the dbo schema, which is the only schema DBSchema reads and writes.</summary>
    public static string Table(string table) => "[dbo]." + Quote(table);

    /// <summary>A comma separated list of quoted names.</summary>
    public static string List(IEnumerable<string> names) => string.Join(", ", names.Select(Quote));
}
