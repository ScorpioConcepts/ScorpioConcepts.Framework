namespace ScorpioConcepts.Framework.DBSchema.IntegrationTests;

public class DbmlSchemaTests(SqlServerFixture fixture) : DatabaseTestBase(fixture)
{
    private const string ShopDatabase = """
        CREATE TABLE Customers (
            Id int IDENTITY(1000,5) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
            Name nvarchar(50) NOT NULL CONSTRAINT DF_Customers_Name DEFAULT (N'Unknown'),
            Code varchar(10) NOT NULL DEFAULT ('NEW'),
            Email varchar(100) NULL,
            Balance decimal(10,2) NOT NULL DEFAULT ((0)),
            Joined datetime2(3) NOT NULL DEFAULT (getdate())
        );
        CREATE UNIQUE INDEX IX_Customers_Email ON Customers (Email);
        CREATE TABLE Orders (
            Id int NOT NULL,
            Line int NOT NULL,
            CustomerId int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES Customers (Id),
            PlacedOn datetime2(3) NOT NULL,
            Qty int NOT NULL,
            Price decimal(10,2) NOT NULL,
            Total AS ([Qty]*[Price]) PERSISTED,
            CONSTRAINT PK_Orders PRIMARY KEY NONCLUSTERED (Id, Line)
        );
        CREATE CLUSTERED INDEX CIX_Orders_PlacedOn ON Orders (PlacedOn DESC);
        CREATE TABLE Shipments (
            OrderId int NOT NULL,
            OrderLine int NOT NULL,
            CONSTRAINT FK_Shipments_Orders FOREIGN KEY (OrderId, OrderLine) REFERENCES Orders (Id, Line)
        );
        """;

    [Fact]
    public async Task Dbml_schema_recreates_the_same_structure_in_an_empty_database()
    {
        Execute(ShopDatabase);
        string dbml = TempFile();
        Manager.GenerateSchema(dbml, format: DBSchemaFormat.Dbml);
        string original = TempFile();
        Manager.GenerateSchema(original);

        await using var copy = await CreateOtherDatabaseAsync();
        var copyManager = new DBSchemaManager(copy);
        copyManager.CheckDatabaseSchema(dbml);

        string copied = TempFile();
        copyManager.GenerateSchema(copied);
        File.ReadAllText(copied).ShouldBe(File.ReadAllText(original));
    }

    [Fact]
    public void Generated_dbml_uses_plain_defaults_named_relationships_and_custom_properties()
    {
        Execute(ShopDatabase);
        string file = TempFile();

        Manager.GenerateSchema(file, format: DBSchemaFormat.Dbml);

        string dbml = File.ReadAllText(file);
        dbml.ShouldContain("Id int [pk, increment, identity_seed: 1000, identity_increment: 5, not null]");
        dbml.ShouldContain("Code varchar(10) [not null, default: 'NEW']");
        dbml.ShouldContain("Name nvarchar(50) [not null, default: `N'Unknown'`, default_name: 'DF_Customers_Name']");
        dbml.ShouldContain("Balance decimal(10,2) [not null, default: 0]");
        dbml.ShouldContain("Joined datetime2(3) [not null, default: `getdate()`]");
        dbml.ShouldContain("Total \"AS ([Qty]*[Price])\" [persisted: true]");
        dbml.ShouldContain("Table Orders [clustered_index: 'CIX_Orders_PlacedOn'] {");
        dbml.ShouldContain("(PlacedOn [desc]) [name: 'CIX_Orders_PlacedOn']");
        dbml.ShouldContain("Ref FK_Shipments_Orders: Shipments.(OrderId, OrderLine) > Orders.(Id, Line)");
    }

    [Fact]
    public void Creates_tables_keys_indexes_and_records_from_hand_written_dbml()
    {
        Manager.CheckDatabaseSchema(WriteSchema("""
            Project shop {
              database_type: 'SQL Server'
            }

            Table Customers as C {
              Id int [pk, increment, identity_seed: 100, identity_increment: 10]
              Email varchar(100) [unique]
              Active bit [not null, default: true, default_name: 'DF_Customers_Active']
              Credit decimal(10,2) [default: 12.5]
              Note: 'Notes are ignored'
            }

            Table Orders [clustered_index: 'CIX_Orders'] {
              Id int
              Line int
              CustomerId int [not null, ref: > C.Id]
              PlacedOn datetime2(3) [not null, default: `sysdatetime()`]
              Qty int [not null, default: 1]
              Double "AS ([Qty]*2)"

              indexes {
                (Id, Line) [pk]
                (CustomerId, PlacedOn [desc]) [name: 'CIX_Orders']
              }
            }

            Table Shipments {
              OrderId int
              OrderLine int
            }

            Ref: Shipments.(OrderId, OrderLine) > Orders.(Id, Line)

            records Customers(Email, Credit) {
              'walk-in@example.com', `DEFAULT`
              'shop@example.com',
              'online@example.com', null
            }
            """));

        Tables().ShouldBe(new[] { "Customers", "Orders", "Shipments" });
        Columns("Customers").ShouldBe(new[] { "Id int NOT NULL", "Email varchar(100) NULL", "Active bit NOT NULL", "Credit decimal NULL" });
        IsIdentity("Customers", "Id").ShouldBeTrue();
        ColumnDefault("Customers", "Active").ShouldBe("((1))");
        Scalar<string>("SELECT name FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID('Customers') AND definition = '((1))'")
            .ShouldBe("DF_Customers_Active");
        ColumnDefault("Orders", "PlacedOn").ShouldBe("(sysdatetime())");
        Scalar<int>("SELECT is_computed FROM sys.columns WHERE object_id = OBJECT_ID('Orders') AND name = 'Double'").ShouldBe(1);
        PrimaryKey("Customers").ShouldBe("PK_Customers: Id");
        PrimaryKey("Orders").ShouldBe("PK_Orders: Id,Line");
        Scalar<string>("SELECT type_desc FROM sys.indexes WHERE name = 'PK_Orders'").ShouldBe("NONCLUSTERED");
        Indexes("Customers").ShouldBe(new[] { "IX_Customers_Email UNIQUE: Email" });
        Indexes("Orders").ShouldBe(new[] { "CIX_Orders CLUSTERED: CustomerId,PlacedOn DESC" });
        ForeignKeys("Orders").ShouldBe(new[] { "FK_Orders_CustomerId: CustomerId -> Customers.Id" });
        ForeignKeys("Shipments").ShouldBe(new[]
        {
            "FK_Shipments_OrderId_OrderLine: OrderId -> Orders.Id",
            "FK_Shipments_OrderId_OrderLine: OrderLine -> Orders.Line",
        });

        // `DEFAULT` leaves Credit to its default; an empty field and null both insert NULL. The identity starts at 100.
        Query("SELECT Id, Email, Active, Credit FROM Customers ORDER BY Id",
                r => (r.GetInt32(0), r.GetString(1), r.GetBoolean(2), r.IsDBNull(3) ? (decimal?)null : r.GetDecimal(3)))
            .ShouldBe(new (int, string, bool, decimal?)[]
            {
                (100, "walk-in@example.com", true, 12.50m),
                (110, "shop@example.com", true, null),
                (120, "online@example.com", true, null),
            });
    }

    [Fact]
    public void Example_schema_in_the_README_works()
    {
        string text = File.ReadAllText(FindRepoFile("ScorpioConcepts.Framework.DBSchema/README.md"));
        int start = text.IndexOf("```dbml", StringComparison.Ordinal) + "```dbml".Length;
        string example = text[start..text.IndexOf("```", start, StringComparison.Ordinal)];

        Manager.CheckDatabaseSchema(WriteSchema(example));

        Tables().ShouldBe(new[] { "Customers", "Orders" });
        Indexes("Customers").ShouldBe(new[] { "IX_Customers_Email UNIQUE: Email" });
        ForeignKeys("Orders").ShouldBe(new[] { "FK_Orders_Customers: CustomerId -> Customers.Id" });
        Query("SELECT Id, Name, Email FROM Customers", r => (r.GetInt32(0), r.GetString(1), r.IsDBNull(2)))
            .ShouldBe(new[] { (1, "Walk-in customer", true) });
    }

    [Fact]
    public void Adds_missing_columns_and_indexes_from_dbml_to_an_existing_table()
    {
        Execute("CREATE TABLE Customers (Id int NOT NULL); INSERT INTO Customers (Id) VALUES (1);");

        Manager.CheckDatabaseSchema(WriteSchema("""
            Table Customers {
              Id int [not null]
              Active bit [not null, default: 1]
              Email varchar(100) [unique]
            }
            """), DBSchemaFormat.Dbml);

        Columns("Customers").ShouldBe(new[] { "Id int NOT NULL", "Active bit NOT NULL", "Email varchar(100) NULL" });
        Indexes("Customers").ShouldBe(new[] { "IX_Customers_Email UNIQUE: Email" });
        Scalar<bool>("SELECT Active FROM Customers WHERE Id = 1").ShouldBeTrue();
        Log.Messages.ShouldContain("Creating column 'Active' in table 'Customers'");
    }
}
