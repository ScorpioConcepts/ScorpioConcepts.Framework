using System.Text.Json;

namespace ScorpioConcepts.Framework.DBSchema.IntegrationTests;

public class JsonSchemaTests(SqlServerFixture fixture) : DatabaseTestBase(fixture)
{
    private const string ShopDatabase = """
        CREATE TABLE Customers (
            Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
            Name nvarchar(50) NOT NULL CONSTRAINT DF_Customers_Name DEFAULT (''),
            Email varchar(100) NULL,
            Balance decimal(10,2) NOT NULL CONSTRAINT DF_Customers_Balance DEFAULT ((0))
        );
        CREATE UNIQUE INDEX IX_Customers_Email ON Customers (Email);
        CREATE TABLE Orders (
            Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
            CustomerId int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES Customers (Id),
            PlacedOn datetime2(3) NOT NULL
        );
        CREATE INDEX IX_Orders_PlacedOn ON Orders (PlacedOn DESC);
        """;

    [Fact]
    public void GenerateSchema_writes_json_by_default()
    {
        Execute(ShopDatabase);
        string file = TempFile();

        DBSchema.GenerateSchema(file);

        using var json = JsonDocument.Parse(File.ReadAllText(file));
        var tables = json.RootElement.GetProperty("tables").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);
        tables.Keys.OrderBy(k => k).ShouldBe(new[] { "Customers", "Orders" });
        tables["Customers"].GetProperty("columns").EnumerateArray()
            .Select(c => $"{c.GetProperty("name").GetString()} | {c.GetProperty("dataType").GetString()} | {c.GetProperty("options").GetString()}")
            .ShouldBe(new[]
            {
                "Id | int identity | NOT NULL",
                "Name | nvarchar(50) | NOT NULL DEFAULT ('')",
                "Email | varchar(100) | NULL",
                "Balance | decimal(10,2) | NOT NULL DEFAULT ((0))",
            });
        tables["Orders"].GetProperty("foreignKeys")[0].GetProperty("referencedTable").GetString().ShouldBe("Customers");
    }

    [Fact]
    public async Task Json_schema_recreates_the_same_structure_in_an_empty_database()
    {
        Execute(ShopDatabase);
        string file = TempFile();
        DBSchema.GenerateSchema(file);

        await using var copy = await CreateOtherDatabaseAsync();
        DBSchema.InitializeDB(copy);
        DBSchema.CheckDatabaseSchema(file);

        string copyFile = TempFile();
        DBSchema.GenerateSchema(copyFile);
        File.ReadAllText(copyFile).ShouldBe(File.ReadAllText(file));
    }

    [Fact]
    public void Creates_tables_and_typed_default_records_from_json()
    {
        DBSchema.CheckDatabaseSchema(WriteSchema("""
            {
              "tables": [
                {
                  "name": "Settings",
                  "columns": [
                    { "name": "Id", "dataType": "int", "options": "NOT NULL" },
                    { "name": "Name", "dataType": "nvarchar(50)", "options": "NOT NULL" },
                    { "name": "Enabled", "dataType": "bit", "options": "NOT NULL" },
                    { "name": "Limit", "dataType": "decimal(10,2)", "options": "NULL" },
                    { "name": "Note", "dataType": "nvarchar(max)", "options": "NULL" }
                  ],
                  "primaryKey": { "name": "PK_Settings", "columns": [ "Id" ] },
                  "indexes": [ { "name": "IX_Settings_Name", "unique": true, "columns": [ "Name" ] } ],
                  "defaultRecords": [
                    { "Id": 1, "Name": "Theme", "Enabled": true, "Limit": 12.5, "Note": "Dark & <bold>" },
                    { "Id": 2, "Name": "Beta", "Enabled": false, "Limit": null }
                  ]
                }
              ]
            }
            """));

        Columns("Settings").ShouldBe(new[]
        {
            "Id int NOT NULL", "Name nvarchar(50) NOT NULL", "Enabled bit NOT NULL", "Limit decimal NULL", "Note nvarchar(-1) NULL",
        });
        PrimaryKey("Settings").ShouldBe("PK_Settings: Id");
        Indexes("Settings").ShouldBe(new[] { "IX_Settings_Name UNIQUE: Name" });
        Query("SELECT Id, Name, Enabled, Limit, Note FROM Settings ORDER BY Id",
                r => (r.GetInt32(0), r.GetString(1), r.GetBoolean(2), r.IsDBNull(3) ? (decimal?)null : r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetString(4)))
            .ShouldBe(new (int, string, bool, decimal?, string?)[]
            {
                (1, "Theme", true, 12.50m, "Dark & <bold>"),
                (2, "Beta", false, null, null),
            });
    }

    [Fact]
    public void Example_schema_in_the_README_works()
    {
        string readme = FindRepoFile("ScorpioConcepts.Framework.DBSchema/README.md");
        string text = File.ReadAllText(readme);
        int start = text.IndexOf("```json", StringComparison.Ordinal) + "```json".Length;
        string example = text[start..text.IndexOf("```", start, StringComparison.Ordinal)];

        DBSchema.CheckDatabaseSchema(WriteSchema(example));

        Tables().ShouldBe(new[] { "Customers", "Orders" });
        ForeignKeys("Orders").ShouldBe(new[] { "FK_Orders_Customers: CustomerId -> Customers.Id" });
        Query("SELECT Id, Name, Email FROM Customers", r => (r.GetInt32(0), r.GetString(1), r.IsDBNull(2)))
            .ShouldBe(new[] { (1, "Walk-in customer", true) });
    }

    [Fact]
    public void Adds_missing_columns_from_json_to_an_existing_table()
    {
        Execute("CREATE TABLE Customers (Id int NOT NULL); INSERT INTO Customers (Id) VALUES (1);");

        DBSchema.CheckDatabaseSchema(WriteSchema("""
            { "tables": [ { "name": "Customers", "columns": [
                { "name": "Id", "dataType": "int", "options": "NOT NULL" },
                { "name": "Active", "dataType": "bit", "options": "NOT NULL DEFAULT ((1))" }
            ] } ] }
            """));

        Columns("Customers").ShouldBe(new[] { "Id int NOT NULL", "Active bit NOT NULL" });
        Scalar<bool>("SELECT Active FROM Customers WHERE Id = 1").ShouldBeTrue();
    }

    [Fact]
    public void GenerateSchema_can_still_write_xml()
    {
        Execute(ShopDatabase);
        string viaFormat = TempFile();
        string viaXmlMethod = TempFile();

        DBSchema.GenerateSchema(viaFormat, new DBDefaultRecords(), DBSchemaFormat.Xml);
        DBSchema.GenerateSchemaXML(viaXmlMethod);

        File.ReadAllText(viaFormat).ShouldStartWith("<?xml");
        File.ReadAllText(viaFormat).ShouldBe(File.ReadAllText(viaXmlMethod));
    }

    [Fact]
    public void CheckDatabaseSchema_accepts_an_explicit_format()
    {
        string file = WriteSchema("""{ "tables": [ { "name": "A", "columns": [ { "name": "Id", "dataType": "int", "options": "NULL" } ] } ] }""");

        DBSchema.CheckDatabaseSchema(file, DBSchemaFormat.Json);

        Tables().ShouldBe(new[] { "A" });
        DBSchema.DebugLog.ShouldContain("Getting DBSchema as per JSON file...");
    }

    [Fact]
    public async Task Converted_xml_schema_builds_the_same_database_as_the_original()
    {
        Execute(ShopDatabase);
        string xml = TempFile();
        DBSchema.GenerateSchemaXML(xml);
        string json = TempFile();
        DBSchema.ConvertSchema(xml, json, DBSchemaFormat.Json);

        await using var fromXml = await CreateOtherDatabaseAsync();
        DBSchema.InitializeDB(fromXml);
        DBSchema.CheckDatabaseSchema(xml);
        string fromXmlResult = TempFile();
        DBSchema.GenerateSchema(fromXmlResult);

        await using var fromJson = await CreateOtherDatabaseAsync();
        DBSchema.InitializeDB(fromJson);
        DBSchema.CheckDatabaseSchema(json);
        string fromJsonResult = TempFile();
        DBSchema.GenerateSchema(fromJsonResult);

        File.ReadAllText(fromJsonResult).ShouldBe(File.ReadAllText(fromXmlResult));
    }
}
