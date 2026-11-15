using System.Text.Json;

namespace ScorpioConcepts.Framework.DBSchema.Tests;

/// <summary>
/// Tests for reading and writing the JSON schema format, without a database.
/// </summary>
public sealed class SchemaJsonTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"dbschema-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_file);

    private DBSchema Read(string json)
    {
        File.WriteAllText(_file, json);
        return DBSchemaJson.Read(_file);
    }

    private static DBSchema ShopSchema()
    {
        var schema = new DBSchema();
        var customers = schema.AddTable("Customers");
        customers.AddColumn("Id", "int identity", "NOT NULL");
        customers.AddColumn("Name", "varchar(50)", "NOT NULL DEFAULT ('')");
        customers.AddColumn("Email", "nvarchar(100)", "NULL");
        customers.PrimaryKey.Name = "PK_Customers";
        customers.PrimaryKey.AddKeyColumn("Id");
        var index = customers.AddIndex("IX_Customers_Name_Email", clustered: false, unique: true);
        index.AddIndexColumn("Name", descending: false);
        index.AddIndexColumn("Email", descending: true);

        var orders = schema.AddTable("Orders");
        orders.AddColumn("Id", "int", "NOT NULL");
        orders.AddColumn("CustomerId", "int", "NOT NULL");
        orders.PrimaryKey.Name = "PK_Orders";
        orders.PrimaryKey.AddKeyColumn("Id");
        orders.AddForeignKey("FK_Orders_Customers", "CustomerId", "Customers", "Id");
        return schema;
    }

    [Fact]
    public void Reads_hand_written_schema()
    {
        var schema = Read("""
            {
              // Comments and trailing commas are allowed in hand-written files
              "tables": [
                {
                  "name": "Customers",
                  "columns": [
                    { "name": "Id", "dataType": "int identity", "options": "NOT NULL" },
                    { "name": "Email", "dataType": "varchar(100)" },
                  ],
                  "primaryKey": { "name": "PK_Customers", "columns": [ "Id" ] },
                  "indexes": [
                    { "name": "IX_Email", "unique": true, "columns": [ "Email" ] },
                    { "name": "IX_Email_Desc", "clustered": false, "columns": [ { "name": "Email", "descending": true } ] }
                  ]
                },
                {
                  "name": "Orders",
                  "columns": [ { "name": "CustomerId", "dataType": "int", "options": "NOT NULL" } ],
                  "foreignKeys": [
                    { "name": "FK_Orders_Customers", "column": "CustomerId", "referencedTable": "Customers", "referencedColumn": "Id" }
                  ]
                }
              ]
            }
            """);

        schema.Tables.Keys.ShouldBe(new[] { "Customers", "Orders" });

        var customers = schema.Tables["Customers"];
        customers.Columns.Select(c => (c.Name, c.DataType, c.Options)).ShouldBe(new[]
        {
            ("Id", "int identity", "NOT NULL"),
            ("Email", "varchar(100)", ""),
        });
        customers.PrimaryKey.Name.ShouldBe("PK_Customers");
        customers.PrimaryKey.KeyColumns.ShouldBe(new[] { "Id" });
        customers.Indexes.Select(i => (i.Name, i.Clustered, i.Unique, string.Join(",", i.Columns))).ShouldBe(new[]
        {
            ("IX_Email", false, true, "[Email, False]"),
            ("IX_Email_Desc", false, false, "[Email, True]"),
        });

        var fkey = schema.Tables["Orders"].ForeignKeys.ShouldHaveSingleItem();
        (fkey.Name, fkey.Column, fkey.PKeyTable, fkey.PKeyColumn).ShouldBe(("FK_Orders_Customers", "CustomerId", "Customers", "Id"));
    }

    [Fact]
    public void Property_names_are_not_case_sensitive_and_unknown_properties_are_ignored()
    {
        var schema = Read("""
            {
              "$comment": "anything",
              "Tables": [
                { "Name": "A", "Description": "ignored", "COLUMNS": [ { "NAME": "Id", "DataType": "int", "Collation": "ignored" } ] }
              ]
            }
            """);

        schema.Tables["A"].Columns.ShouldHaveSingleItem().DataType.ShouldBe("int");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "tables": [] }""")]
    [InlineData("""{ "tables": null }""")]
    public void Reads_empty_schema(string json)
    {
        Read(json).Tables.ShouldBeEmpty();
    }

    [Fact]
    public void Table_with_only_a_name_is_empty()
    {
        var table = Read("""{ "tables": [ { "name": "A" } ] }""").Tables["A"];

        table.Columns.ShouldBeEmpty();
        table.PrimaryKey.KeyColumns.ShouldBeEmpty();
        table.Indexes.ShouldBeEmpty();
        table.ForeignKeys.ShouldBeEmpty();
        table.DefaultRecords.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{ "tables": [ { "columns": [] } ] }""", "\"name\" is missing for a table")]
    [InlineData("""{ "tables": [ { "name": "A", "columns": [ { "name": "Id" } ] } ] }""", "\"dataType\" is missing for column 'Id' of table 'A'")]
    [InlineData("""{ "tables": [ { "name": "A", "columns": "Id int" } ] }""", "\"columns\" of table 'A' must be an array")]
    [InlineData("""{ "tables": [ { "name": "A", "indexes": [ { "name": "IX", "unique": "yes" } ] } ] }""", "\"unique\" of index 'IX' of table 'A' must be true or false")]
    [InlineData("""{ "tables": [ { "name": "A", "foreignKeys": [ { "name": "FK", "column": "B" } ] } ] }""", "\"referencedTable\" is missing for foreign key 'FK' of table 'A'")]
    [InlineData("""{ "tables": [ { "name": "A", "defaultRecords": [ { "Id": [1] } ] } ] }""", "column 'Id' in a default record of table 'A'")]
    [InlineData("""[ { "name": "A" } ]""", "must contain an object")]
    [InlineData("""{ "tables": [ "A" ] }""", "Each entry in \"tables\" must be a JSON object")]
    public void Invalid_schema_explains_what_is_wrong(string json, string expected)
    {
        Should.Throw<FormatException>(() => Read(json)).Message.ShouldContain(expected);
    }

    [Fact]
    public void Rejects_duplicate_table_names()
    {
        Should.Throw<ArgumentException>(() => Read("""{ "tables": [ { "name": "A" }, { "name": "A" } ] }"""));
    }

    [Fact]
    public void Invalid_json_throws_and_releases_the_file()
    {
        Should.Throw<JsonException>(() => Read("""{ "tables": [ { "name": "A" """));

        Should.NotThrow(() => File.Delete(_file));
    }

    [Fact]
    public void Written_schema_reads_back_identically()
    {
        var schema = ShopSchema();

        DBSchemaJson.Write(schema, _file, new DBDefaultRecords());
        var read = DBSchemaJson.Read(_file);

        SchemaAssert.SameStructure(read, schema);
    }

    [Fact]
    public void Written_file_is_readable_json_with_the_documented_shape()
    {
        DBSchemaJson.Write(ShopSchema(), _file, new DBDefaultRecords());

        string text = File.ReadAllText(_file);
        text.ShouldContain("\"options\": \"NOT NULL DEFAULT ('')\"", customMessage: "SQL quotes should not be escaped");
        text.ShouldContain(Environment.NewLine + "  \"tables\": [", customMessage: "the file should be indented");

        using var json = JsonDocument.Parse(text);
        var orders = json.RootElement.GetProperty("tables")[1];
        orders.GetProperty("name").GetString().ShouldBe("Orders");
        orders.GetProperty("primaryKey").GetProperty("columns")[0].GetString().ShouldBe("Id");
        var fkey = orders.GetProperty("foreignKeys")[0];
        fkey.GetProperty("referencedTable").GetString().ShouldBe("Customers");
        fkey.GetProperty("referencedColumn").GetString().ShouldBe("Id");
        orders.TryGetProperty("indexes", out _).ShouldBeFalse("empty sections are left out");
        orders.TryGetProperty("defaultRecords", out _).ShouldBeFalse("empty sections are left out");
    }

    [Fact]
    public void Table_without_primary_key_writes_no_primary_key()
    {
        var schema = new DBSchema();
        schema.AddTable("Log").AddColumn("Message", "varchar(100)", "NULL");

        DBSchemaJson.Write(schema, _file, new DBDefaultRecords());

        File.ReadAllText(_file).ShouldNotContain("primaryKey");
        DBSchemaJson.Read(_file).Tables["Log"].PrimaryKey.KeyColumns.ShouldBeEmpty();
    }

    [Fact]
    public void Default_record_values_keep_their_types()
    {
        var schema = new DBSchema();
        schema.AddTable("Settings").AddColumn("Id", "int", "NOT NULL");
        var defaults = new DBDefaultRecords();
        var record = defaults.AddRecord("Settings");
        record.AddValue("Int", 42);
        record.AddValue("Long", 5_000_000_000L);
        record.AddValue("Decimal", 12.50m);
        record.AddValue("Bool", true);
        record.AddValue("Text", "<needs & escaping> \"quoted\"");
        record.AddValue("Null", null!);
        record.AddValue("DBNull", DBNull.Value);
        record.AddValue("Date", new DateTime(2026, 11, 15, 8, 30, 0));
        record.AddValue("Guid", Guid.Parse("8184a1a7-2536-4da3-8408-5624ab412f4c"));
        defaults.AddRecord("NotInSchema").AddValue("Id", 1);

        DBSchemaJson.Write(schema, _file, defaults);
        var read = DBSchemaJson.Read(_file);

        read.Tables.Keys.ShouldBe(new[] { "Settings" });
        read.Tables["Settings"].DefaultRecords.ShouldHaveSingleItem().Values.ShouldBe(new Dictionary<string, object>
        {
            ["Int"] = 42,
            ["Long"] = 5_000_000_000L,
            ["Decimal"] = 12.50m,
            ["Bool"] = true,
            ["Text"] = "<needs & escaping> \"quoted\"",
            ["Null"] = DBNull.Value,
            ["DBNull"] = DBNull.Value,
            ["Date"] = "2026-11-15T08:30:00",
            ["Guid"] = "8184a1a7-2536-4da3-8408-5624ab412f4c",
        });
    }

    [Fact]
    public void Write_records_progress_in_DebugLog()
    {
        DBSchemaJson.Write(ShopSchema(), _file, new DBDefaultRecords());

        DBSchema.DebugLog.ShouldContain("DB Schema JSON generated for table 'Orders'");
    }
}

internal static class SchemaAssert
{
    /// <summary>Compare two schemas table by table, ignoring default records.</summary>
    public static void SameStructure(DBSchema actual, DBSchema expected)
    {
        actual.Tables.Keys.ShouldBe(expected.Tables.Keys);
        foreach (var e in expected.Tables.Values)
        {
            var a = actual.Tables[e.Name];
            a.Columns.Select(c => (c.Name, c.DataType, c.Options)).ShouldBe(e.Columns.Select(c => (c.Name, c.DataType, c.Options)));
            a.PrimaryKey.Name.ShouldBe(e.PrimaryKey.Name);
            a.PrimaryKey.KeyColumns.ShouldBe(e.PrimaryKey.KeyColumns);
            a.Indexes.Select(i => (i.Name, i.Clustered, i.Unique, string.Join(",", i.Columns)))
                .ShouldBe(e.Indexes.Select(i => (i.Name, i.Clustered, i.Unique, string.Join(",", i.Columns))));
            a.ForeignKeys.Select(f => (f.Name, f.Column, f.PKeyTable, f.PKeyColumn))
                .ShouldBe(e.ForeignKeys.Select(f => (f.Name, f.Column, f.PKeyTable, f.PKeyColumn)));
        }
    }
}
