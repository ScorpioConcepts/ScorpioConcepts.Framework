using System.Xml.Linq;

namespace ScorpioConcepts.Framework.DBSchema.IntegrationTests;

public class GenerateSchemaXmlTests(SqlServerFixture fixture) : DatabaseTestBase(fixture)
{
    private const string ShopDatabase = """
        CREATE TABLE Customers (
            Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
            Name varchar(50) NOT NULL CONSTRAINT DF_Customers_Name DEFAULT (''),
            Email varchar(100) NULL
        );
        CREATE UNIQUE INDEX IX_Customers_Email ON Customers (Email);
        CREATE TABLE Orders (
            Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
            CustomerId int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES Customers (Id),
            PlacedOn datetime NOT NULL
        );
        CREATE INDEX IX_Orders_PlacedOn ON Orders (PlacedOn DESC);
        """;

    private XElement Generate(DBDefaultRecords? defaults = null)
    {
        string file = TempFile();
        if (defaults is null) Manager.GenerateSchema(file, format: DBSchemaFormat.Xml);
        else Manager.GenerateSchema(file, defaults, DBSchemaFormat.Xml);
        return XElement.Load(file);
    }

    [Fact]
    public void Empty_database_produces_empty_schema()
    {
        var xml = Generate();

        xml.Name.LocalName.ShouldBe("tables");
        xml.Elements().ShouldBeEmpty();
    }

    [Fact]
    public void Describes_tables_columns_keys_and_indexes()
    {
        Execute(ShopDatabase);

        var tables = Generate().Elements("table").ToDictionary(t => (string)t.Attribute("name")!);

        tables.Keys.OrderBy(k => k).ShouldBe(new[] { "Customers", "Orders" });

        var customers = tables["Customers"];
        customers.Elements("column").Select(c => $"{c.Attribute("name")!.Value} | {c.Attribute("datatype")!.Value} | {c.Attribute("options")!.Value}")
            .ShouldBe(new[]
            {
                "Id | int identity | NOT NULL",
                "Name | varchar(50) | NOT NULL DEFAULT ('')",
                "Email | varchar(100) | NULL",
            });
        var pk = customers.Element("primary_key")!;
        pk.Attribute("name")!.Value.ShouldBe("PK_Customers");
        pk.Elements("column").Select(c => c.Attribute("name")!.Value).ShouldBe(new[] { "Id" });
        var index = customers.Elements("index").ShouldHaveSingleItem();
        index.Attribute("name")!.Value.ShouldBe("IX_Customers_Email");
        index.Attribute("unique")!.Value.ShouldBe("True");
        index.Attribute("clustered")!.Value.ShouldBe("False");

        var orders = tables["Orders"];
        var desc = orders.Elements("index").ShouldHaveSingleItem().Element("column")!;
        desc.Attribute("name")!.Value.ShouldBe("PlacedOn");
        desc.Attribute("desc")!.Value.ShouldBe("True");
        var fk = orders.Elements("foreign_key").ShouldHaveSingleItem();
        fk.Attributes().Select(a => $"{a.Name}={a.Value}")
            .ShouldBe(new[] { "name=FK_Orders_Customers", "column=CustomerId", "pk_table=Customers", "pk_column=Id" });
    }

    [Fact]
    public void Includes_default_records_for_matching_tables()
    {
        Execute("CREATE TABLE Settings (Id int NOT NULL, Name varchar(50) NOT NULL);");
        var defaults = new DBDefaultRecords();
        var record = defaults.AddRecord("Settings");
        record.AddValue("Id", 1);
        record.AddValue("Name", "Theme");

        var settings = Generate(defaults).Elements("table").Single();

        settings.Elements("default_record").ShouldHaveSingleItem().Elements("columnValue")
            .Select(v => $"{v.Attribute("column")!.Value}={v.Value}")
            .ShouldBe(new[] { "Id=1", "Name=Theme" });
    }

    [Fact]
    public async Task Generated_schema_recreates_the_same_structure_in_an_empty_database()
    {
        Execute(ShopDatabase);
        string file = TempFile();
        Manager.GenerateSchema(file, format: DBSchemaFormat.Xml);
        string original = File.ReadAllText(file);

        await using var copy = await CreateOtherDatabaseAsync();
        var copyManager = new DBSchemaManager(copy);
        copyManager.CheckDatabaseSchema(file);

        string copyFile = TempFile();
        copyManager.GenerateSchema(copyFile, format: DBSchemaFormat.Xml);
        File.ReadAllText(copyFile).ShouldBe(original);
    }

    private const string AllSizedTypes = """
        CREATE TABLE Products (
            Code char(10) NOT NULL,
            Name nvarchar(50) NOT NULL,
            Initial nchar(1) NULL,
            Notes varchar(max) NULL,
            Body nvarchar(max) NULL,
            Hash binary(32) NULL,
            Thumbnail varbinary(max) NULL,
            Price decimal(10,2) NOT NULL,
            Weight numeric(8,3) NULL,
            Ratio float(24) NULL,
            Score float NULL,
            CreatedAt datetime2(3) NOT NULL,
            OpensAt time(0) NULL,
            SeenAt datetimeoffset NULL
        );
        """;

    [Fact]
    public void Generated_schema_keeps_sizes_of_all_data_types()
    {
        Execute(AllSizedTypes);

        var columns = Generate().Element("table")!.Elements("column")
            .Select(c => c.Attribute("datatype")!.Value);

        columns.ShouldBe(new[]
        {
            "char(10)", "nvarchar(50)", "nchar(1)", "varchar(max)", "nvarchar(max)", "binary(32)", "varbinary(max)",
            "decimal(10,2)", "numeric(8,3)", "real", "float", "datetime2(3)", "time(0)", "datetimeoffset(7)",
        });
    }

    [Fact]
    public async Task Sized_types_survive_a_round_trip()
    {
        Execute(AllSizedTypes);
        string file = TempFile();
        Manager.GenerateSchema(file, format: DBSchemaFormat.Xml);

        await using var copy = await CreateOtherDatabaseAsync();
        var copyManager = new DBSchemaManager(copy);
        copyManager.CheckDatabaseSchema(file);

        string copyFile = TempFile();
        copyManager.GenerateSchema(copyFile, format: DBSchemaFormat.Xml);
        File.ReadAllText(copyFile).ShouldBe(File.ReadAllText(file));
    }

    [Fact]
    public void Index_columns_keep_their_key_order_and_skip_included_columns()
    {
        Execute("""
            CREATE TABLE People (Id int NOT NULL, FirstName varchar(50) NOT NULL, LastName varchar(50) NOT NULL, Email varchar(100) NULL);
            CREATE INDEX IX_People_Name ON People (LastName, FirstName DESC) INCLUDE (Email);
            """);

        var index = Generate().Element("table")!.Elements("index").ShouldHaveSingleItem();

        index.Elements("column").Select(c => $"{c.Attribute("name")!.Value} desc={c.Attribute("desc")!.Value}")
            .ShouldBe(new[] { "LastName desc=False", "FirstName desc=True" });
    }
}
