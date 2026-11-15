using System.Collections;
using Microsoft.Data.SqlClient;

namespace ScorpioConcepts.Framework.DBSchema.Tests;

/// <summary>
/// The instance API (#19), read-only model collections (#21) and the null-safe default record (#18).
/// </summary>
public sealed class DBSchemaApiTests : IDisposable
{
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var file in _files) File.Delete(file);
    }

    private string TempFile(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"dbschema-{Guid.NewGuid():N}{extension}");
        _files.Add(path);
        return path;
    }

    private static DBSchemaDefinition SampleSchema()
    {
        var schema = new DBSchemaDefinition();
        var customers = schema.AddTable("Customers");
        customers.AddColumn("Id", "int identity", "NOT NULL");
        customers.AddColumn("Name", "varchar(50)", "NULL");
        customers.PrimaryKey.Name = "PK_Customers";
        customers.PrimaryKey.AddColumn("Id");
        customers.AddIndex("IX_Name", clustered: false, unique: false).AddColumn("Name", descending: true);
        customers.AddDefaultRecord().AddValue("Name", "Walk-in");

        var orders = schema.AddTable("Orders");
        orders.AddColumn("CustomerId", "int", "NOT NULL");
        orders.AddForeignKey("FK_Orders_Customers", "CustomerId", "Customers", "Id");
        return schema;
    }

    [Fact]
    public void Manager_rejects_a_null_connection()
    {
        Should.Throw<ArgumentNullException>(() => new DBSchemaManager(null!)).ParamName.ShouldBe("connection");
    }

    [Fact]
    public void Manager_keeps_its_own_connection()
    {
        using var first = new SqlConnection("Server=first;Database=a");
        using var second = new SqlConnection("Server=second;Database=b");

        // Two managers no longer share one static connection
        new DBSchemaManager(first).Connection.ShouldBeSameAs(first);
        new DBSchemaManager(second).Connection.ShouldBeSameAs(second);
    }

    [Fact]
    public void Manager_checks_its_arguments()
    {
        using var connection = new SqlConnection("Server=nowhere");
        var manager = new DBSchemaManager(connection);

        Should.Throw<ArgumentNullException>(() => manager.CheckDatabaseSchema((DBSchemaDefinition)null!));
        Should.Throw<ArgumentNullException>(() => manager.GenerateSchema(null!));
    }

    [Theory]
    [InlineData(DBSchemaFormat.Json, ".json")]
    [InlineData(DBSchemaFormat.Xml, ".xml")]
    public void Save_and_Load_round_trip_a_schema(DBSchemaFormat format, string extension)
    {
        string file = TempFile(extension);
        var schema = SampleSchema();

        schema.Save(file, format);
        var loaded = DBSchemaDefinition.Load(file);

        DBSchemaDefinition.DetectFormat(file).ShouldBe(format);
        SchemaAssert.SameStructure(loaded, schema);
        loaded.Tables["Customers"].DefaultRecords.ShouldHaveSingleItem().Values["Name"].ShouldBe("Walk-in");
    }

    [Fact]
    public void Save_writes_json_by_default()
    {
        string file = TempFile(".schema");

        SampleSchema().Save(file);

        DBSchemaDefinition.DetectFormat(file).ShouldBe(DBSchemaFormat.Json);
    }

    [Fact]
    public void Tables_keep_the_order_they_were_read_in()
    {
        string file = TempFile(".json");
        File.WriteAllText(file, """{ "tables": [ { "name": "Zebra" }, { "name": "Apple" }, { "name": "Mango" } ] }""");

        DBSchemaDefinition.Load(file).TablesInOrder.Select(t => t.Name).ShouldBe(new[] { "Zebra", "Apple", "Mango" });
    }

    [Fact]
    public void AddDefaultRecords_adds_records_only_to_tables_in_the_schema()
    {
        var schema = SampleSchema();
        var records = new DBDefaultRecords();
        records.AddRecord("Orders").AddValue("CustomerId", 1);
        records.AddRecord("Missing").AddValue("Id", 1);

        schema.AddDefaultRecords(records);

        schema.Tables["Orders"].DefaultRecords.ShouldHaveSingleItem().Values["CustomerId"].ShouldBe(1);
        schema.Tables.ContainsKey("Missing").ShouldBeFalse();
    }

    [Fact]
    public void Model_collections_are_read_only()
    {
        var schema = SampleSchema();
        var customers = schema.Tables["Customers"];

        // Casting back to a mutable collection must not let a caller change the schema
        foreach (var list in new IEnumerable[]
        {
            customers.Columns, customers.Indexes, customers.ForeignKeys, customers.DefaultRecords,
            customers.PrimaryKey.Columns, customers.Indexes[0].Columns, schema.TablesInOrder,
            customers.DefaultRecords[0].Columns,
        })
        {
            var mutable = list.ShouldBeAssignableTo<IList>()!;
            mutable.IsReadOnly.ShouldBeTrue();
            Should.Throw<NotSupportedException>(() => mutable.Clear());
        }

        var tables = schema.Tables.ShouldBeAssignableTo<IDictionary<string, DBTable>>()!;
        Should.Throw<NotSupportedException>(() => tables.Remove("Customers"));
        var values = customers.DefaultRecords[0].Values.ShouldBeAssignableTo<IDictionary<string, object?>>()!;
        Should.Throw<NotSupportedException>(() => values["Name"] = "changed");
    }

    [Fact]
    public void Default_record_ToString_shows_null_values()
    {
        var record = new DBDefaultRecord();
        record.AddValue("Id", 1).AddValue("Name", null).AddValue("Note", DBNull.Value);

        record.ToString().ShouldBe("DBDefaultRecord: Id=1,Name=NULL, 1 other...");
    }

    [Fact]
    public void Default_record_ToString_uses_the_invariant_culture()
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var record = new DBDefaultRecord();
            record.AddValue("Price", 12.5m);

            record.ToString().ShouldBe("DBDefaultRecord: Price=12.5");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Default_record_keeps_the_order_values_were_added_in()
    {
        var record = new DBDefaultRecord();
        record.AddValue("Zebra", 1).AddValue("Apple", 2).AddValue("Mango", 3);

        record.Columns.ShouldBe(new[] { "Zebra", "Apple", "Mango" });
    }

    [Fact]
    public void DefaultRecords_returns_an_empty_list_for_an_unknown_table()
    {
        new DBDefaultRecords()["Nothing"].ShouldBeEmpty();
    }

    [Fact]
    public void Index_column_ToString_shows_the_sort_order()
    {
        var index = new DBTable("T").AddIndex("IX", false, false);
        index.AddColumn("A", descending: false);
        index.AddColumn("B", descending: true);

        index.Columns.Select(c => c.ToString()).ShouldBe(new[] { "A", "B DESC" });
    }
}
