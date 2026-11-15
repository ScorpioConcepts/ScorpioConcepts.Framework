namespace ScorpioConcepts.Framework.DBSchema.IntegrationTests;

public class DbmlSchemaTests(SqlServerFixture fixture) : DatabaseTestBase(fixture)
{
    private const string ShopDatabase = """
        CREATE TABLE Customers (
            Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
            Name nvarchar(50) NOT NULL CONSTRAINT DF_Customers_Name DEFAULT (N'Unknown'),
            Code varchar(10) NOT NULL CONSTRAINT DF_Customers_Code DEFAULT ('NEW'),
            Email varchar(100) NULL,
            Balance decimal(10,2) NOT NULL CONSTRAINT DF_Customers_Balance DEFAULT ((0)),
            Joined datetime2(3) NOT NULL CONSTRAINT DF_Customers_Joined DEFAULT (getdate())
        );
        CREATE UNIQUE INDEX IX_Customers_Email ON Customers (Email);
        CREATE TABLE Orders (
            Id int NOT NULL,
            Line int NOT NULL,
            CustomerId int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES Customers (Id),
            PlacedOn datetime2(3) NOT NULL,
            CONSTRAINT PK_Orders PRIMARY KEY (Id, Line)
        );
        CREATE INDEX IX_Orders_PlacedOn ON Orders (PlacedOn DESC);
        CREATE TABLE Shipments (
            OrderId int NOT NULL,
            OrderLine int NOT NULL,
            CustomerId int NULL CONSTRAINT FK_Shipments_Customers REFERENCES Customers (Id)
        );
        CREATE CLUSTERED INDEX CIX_Shipments ON Shipments (OrderId, OrderLine);
        """;

    [Fact]
    public async Task Dbml_schema_recreates_the_same_structure_in_an_empty_database()
    {
        Execute(ShopDatabase);
        string dbml = TempFile();
        DBSchema.GenerateSchema(dbml, new DBDefaultRecords(), DBSchemaFormat.Dbml);
        string original = TempFile();
        DBSchema.GenerateSchema(original);

        await using var copy = await CreateOtherDatabaseAsync();
        DBSchema.InitializeDB(copy);
        DBSchema.CheckDatabaseSchema(dbml);

        string copied = TempFile();
        DBSchema.GenerateSchema(copied);
        File.ReadAllText(copied).ShouldBe(File.ReadAllText(original));
    }

    [Fact]
    public void Generated_dbml_uses_plain_defaults_and_named_relationships()
    {
        Execute(ShopDatabase);
        string file = TempFile();

        DBSchema.GenerateSchema(file, new DBDefaultRecords(), DBSchemaFormat.Dbml);

        string dbml = File.ReadAllText(file);
        dbml.ShouldContain("Id int [pk, increment, not null]");
        dbml.ShouldContain("Code varchar(10) [not null, default: 'NEW']");
        dbml.ShouldContain("Name nvarchar(50) [not null, default: `N'Unknown'`]");
        dbml.ShouldContain("Balance decimal(10,2) [not null, default: 0]");
        dbml.ShouldContain("Joined datetime2(3) [not null, default: `getdate()`]");
        dbml.ShouldContain("(PlacedOn [desc]) [name: 'IX_Orders_PlacedOn']");
        dbml.ShouldContain("Table Shipments [clustered_index: 'CIX_Shipments'] {");
        dbml.ShouldContain("(OrderId, OrderLine) [name: 'CIX_Shipments']");
        dbml.ShouldContain("Ref FK_Shipments_Customers: Shipments.CustomerId > Customers.Id");
    }

    [Fact]
    public void Creates_tables_keys_indexes_and_records_from_hand_written_dbml()
    {
        DBSchema.CheckDatabaseSchema(WriteSchema("""
            Project shop {
              database_type: 'SQL Server'
            }

            Table Customers as C {
              Id int [pk, increment]
              Email varchar(100) [unique]
              Active bit [not null, default: true]
              Credit decimal(10,2) [default: 12.5]
              Note: 'Notes are ignored'
            }

            Table Orders {
              Id int
              Line int
              CustomerId int [not null, ref: > C.Id]
              PlacedOn datetime2(3) [not null, default: `sysdatetime()`]

              indexes {
                (Id, Line) [pk]
                (CustomerId, PlacedOn [desc]) [name: 'IX_Orders']
              }
            }

            Table Shipments [clustered_index: 'IX_Shipments_OrderId_OrderLine'] {
              OrderId int
              OrderLine int
              CustomerId int

              indexes {
                (OrderId, OrderLine)
              }
            }

            Ref ShipTo: Shipments.CustomerId > Customers.Id

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
        ColumnDefault("Orders", "PlacedOn").ShouldBe("(sysdatetime())");
        PrimaryKey("Customers").ShouldBe("PK_Customers: Id");
        PrimaryKey("Orders").ShouldBe("PK_Orders: Id,Line");
        Indexes("Customers").ShouldBe(new[] { "IX_Customers_Email UNIQUE: Email" });
        Indexes("Orders").ShouldBe(new[] { "IX_Orders: CustomerId,PlacedOn DESC" });
        Indexes("Shipments").ShouldBe(new[] { "IX_Shipments_OrderId_OrderLine CLUSTERED: OrderId,OrderLine" });
        ForeignKeys("Orders").ShouldBe(new[] { "FK_Orders_CustomerId: CustomerId -> Customers.Id" });
        ForeignKeys("Shipments").ShouldBe(new[] { "ShipTo: CustomerId -> Customers.Id" });

        // `DEFAULT` leaves Credit to its default; an empty field and null both insert NULL
        Query("SELECT Email, Active, Credit FROM Customers ORDER BY Id", r => (r.GetString(0), r.GetBoolean(1), r.IsDBNull(2) ? (decimal?)null : r.GetDecimal(2)))
            .ShouldBe(new (string, bool, decimal?)[]
            {
                ("walk-in@example.com", true, 12.50m),
                ("shop@example.com", true, null),
                ("online@example.com", true, null),
            });
    }

    [Fact]
    public void Example_schema_in_the_README_works()
    {
        string text = File.ReadAllText(FindRepoFile("ScorpioConcepts.Framework.DBSchema/README.md"));
        int start = text.IndexOf("```dbml", StringComparison.Ordinal) + "```dbml".Length;
        string example = text[start..text.IndexOf("```", start, StringComparison.Ordinal)];

        DBSchema.CheckDatabaseSchema(WriteSchema(example));

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

        DBSchema.CheckDatabaseSchema(WriteSchema("""
            Table Customers {
              Id int [not null]
              Active bit [not null, default: 1]
              Email varchar(100) [unique]
            }
            """), DBSchemaFormat.Dbml);

        Columns("Customers").ShouldBe(new[] { "Id int NOT NULL", "Active bit NOT NULL", "Email varchar(100) NULL" });
        Indexes("Customers").ShouldBe(new[] { "IX_Customers_Email UNIQUE: Email" });
        Scalar<bool>("SELECT Active FROM Customers WHERE Id = 1").ShouldBeTrue();
        DBSchema.DebugLog.ShouldContain("Getting DBSchema as per DBML file...");
    }
}
