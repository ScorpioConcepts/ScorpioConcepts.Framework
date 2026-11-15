namespace ScorpioConcepts.Framework.DBSchema.IntegrationTests;

/// <summary>
/// Regression tests for the problems the code review found in the generated SQL and in reading the database
/// (#5 and #7 to #13). Each one failed before its fix.
/// </summary>
public class SchemaCorrectnessTests(SqlServerFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>Generate a schema file from this test's database, then build a fresh database from it.</summary>
    private async Task<(string Original, string Copy, DBSchemaManager CopyManager, Microsoft.Data.SqlClient.SqlConnection CopyConnection)> RoundTripAsync()
    {
        string file = TempFile();
        Manager.GenerateSchema(file);

        var copy = await CreateOtherDatabaseAsync();
        var copyManager = new DBSchemaManager(copy);
        copyManager.CheckDatabaseSchema(file);

        string copyFile = TempFile();
        copyManager.GenerateSchema(copyFile);
        return (File.ReadAllText(file), File.ReadAllText(copyFile), copyManager, copy);
    }

    // #5: every identifier is quoted, so a "]" in a name can't break out of the brackets
    [Fact]
    public void Names_containing_a_closing_bracket_work()
    {
        Manager.CheckDatabaseSchema(WriteSchema("""
            { "tables": [
              { "name": "Odd]Name",
                "columns": [ { "name": "Id]x", "dataType": "int", "options": "NOT NULL" }, { "name": "Other]", "dataType": "int", "options": "NULL" } ],
                "primaryKey": { "name": "PK_Odd]", "columns": [ "Id]x" ] },
                "indexes": [ { "name": "IX_Odd]", "columns": [ "Other]" ] } ],
                "defaultRecords": [ { "Id]x": 1, "Other]": 2 } ] },
              { "name": "Child]",
                "columns": [ { "name": "Parent]", "dataType": "int", "options": "NOT NULL" } ],
                "foreignKeys": [ { "name": "FK_Child]", "column": "Parent]", "referencedTable": "Odd]Name", "referencedColumn": "Id]x" } ] }
            ] }
            """));

        Tables().ShouldBe(new[] { "Child]", "Odd]Name" });
        Scalar<int>("SELECT [Other]]] FROM [Odd]]Name]").ShouldBe(2);
        var schema = Manager.ReadDatabaseSchema();
        schema.Tables["Odd]Name"].PrimaryKey.Name.ShouldBe("PK_Odd]");
        schema.Tables["Odd]Name"].Indexes.ShouldHaveSingleItem().Name.ShouldBe("IX_Odd]");
        schema.Tables["Child]"].ForeignKeys.ShouldHaveSingleItem().Name.ShouldBe("FK_Child]");
    }

    // #7: foreign keys with more than one column
    [Fact]
    public void Creates_a_composite_foreign_key()
    {
        Manager.CheckDatabaseSchema(WriteSchema("""
            { "tables": [
              { "name": "OrderLines",
                "columns": [ { "name": "OrderId", "dataType": "int", "options": "NOT NULL" }, { "name": "LineNo", "dataType": "int", "options": "NOT NULL" } ],
                "primaryKey": { "name": "PK_OrderLines", "columns": [ "OrderId", "LineNo" ] } },
              { "name": "Deliveries",
                "columns": [ { "name": "Id", "dataType": "int", "options": "NOT NULL" }, { "name": "OrderId", "dataType": "int", "options": "NOT NULL" }, { "name": "LineNo", "dataType": "int", "options": "NOT NULL" } ],
                "foreignKeys": [ { "name": "FK_Deliveries_OrderLines", "columns": [ "OrderId", "LineNo" ], "referencedTable": "OrderLines", "referencedColumns": [ "OrderId", "LineNo" ] } ] }
            ] }
            """));

        ForeignKeys("Deliveries").ShouldBe(new[]
        {
            "FK_Deliveries_OrderLines: OrderId -> OrderLines.OrderId",
            "FK_Deliveries_OrderLines: LineNo -> OrderLines.LineNo",
        }, ignoreOrder: true);
    }

    [Fact]
    public async Task A_composite_foreign_key_survives_a_round_trip()
    {
        Execute("""
            CREATE TABLE OrderLines (OrderId int NOT NULL, [LineNo] int NOT NULL, CONSTRAINT PK_OrderLines PRIMARY KEY (OrderId, [LineNo]));
            CREATE TABLE Deliveries (OrderId int NOT NULL, [LineNo] int NOT NULL,
                CONSTRAINT FK_Deliveries_OrderLines FOREIGN KEY (OrderId, [LineNo]) REFERENCES OrderLines (OrderId, [LineNo]));
            """);

        var (original, copy, copyManager, connection) = await RoundTripAsync();
        await using var _ = connection;

        var key = copyManager.ReadDatabaseSchema().Tables["Deliveries"].ForeignKeys.ShouldHaveSingleItem();
        key.Columns.ShouldBe(new[] { "OrderId", "LineNo" });
        key.ReferencedColumns.ShouldBe(new[] { "OrderId", "LineNo" });
        copy.ShouldBe(original);
    }

    // #8: SQL Server names are not case sensitive, so a file that differs only in case changes nothing
    [Fact]
    public void Names_that_differ_only_in_case_are_the_same_object()
    {
        Execute("""
            CREATE TABLE Customers (Id int NOT NULL CONSTRAINT PK_Customers PRIMARY KEY, Name varchar(50) NULL);
            CREATE INDEX IX_Customers_Name ON Customers (Name);
            CREATE TABLE Orders (CustomerId int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES Customers (Id));
            """);

        Manager.CheckDatabaseSchema(WriteSchema("""
            { "tables": [
              { "name": "CUSTOMERS",
                "columns": [ { "name": "ID", "dataType": "int", "options": "NOT NULL" }, { "name": "name", "dataType": "varchar(50)", "options": "NULL" } ],
                "indexes": [ { "name": "ix_customers_name", "columns": [ "NAME" ] } ] },
              { "name": "orders",
                "columns": [ { "name": "customerid", "dataType": "int", "options": "NOT NULL" } ],
                "foreignKeys": [ { "name": "fk_orders_customers", "column": "CUSTOMERID", "referencedTable": "customers", "referencedColumn": "id" } ] }
            ] }
            """));

        Tables().ShouldBe(new[] { "Customers", "Orders" });
        Log.Messages.ShouldNotContain(line => line.StartsWith("Creating"));
    }

    // #9: default record values are positional parameters, so any column name works
    [Fact]
    public void Default_records_work_with_column_names_that_are_not_valid_parameter_names()
    {
        Manager.CheckDatabaseSchema(WriteSchema("""
            { "tables": [ { "name": "Settings",
                "columns": [ { "name": "Setting Name", "dataType": "varchar(50)", "options": "NOT NULL" }, { "name": "Value-1", "dataType": "int", "options": "NULL" } ],
                "defaultRecords": [ { "Setting Name": "Theme", "Value-1": 3 } ] } ] }
            """));

        Scalar<int>("SELECT [Value-1] FROM Settings WHERE [Setting Name] = 'Theme'").ShouldBe(3);
    }

    // #10: a nonclustered primary key stays nonclustered, so a clustered index can sit next to it
    [Fact]
    public void A_nonclustered_primary_key_leaves_room_for_a_clustered_index()
    {
        Manager.CheckDatabaseSchema(WriteSchema("""
            { "tables": [ { "name": "Events",
                "columns": [ { "name": "Id", "dataType": "uniqueidentifier", "options": "NOT NULL" }, { "name": "HappenedOn", "dataType": "datetime2(3)", "options": "NOT NULL" } ],
                "primaryKey": { "name": "PK_Events", "clustered": false, "columns": [ "Id" ] },
                "indexes": [ { "name": "CX_Events_HappenedOn", "clustered": true, "columns": [ "HappenedOn" ] } ] } ] }
            """));

        Scalar<string>("SELECT type_desc FROM sys.indexes WHERE name = 'PK_Events'").ShouldBe("NONCLUSTERED");
        Indexes("Events").ShouldBe(new[] { "CX_Events_HappenedOn CLUSTERED: HappenedOn" });
    }

    [Fact]
    public async Task Whether_the_primary_key_is_clustered_survives_a_round_trip()
    {
        Execute("""
            CREATE TABLE Events (Id uniqueidentifier NOT NULL CONSTRAINT PK_Events PRIMARY KEY NONCLUSTERED, HappenedOn datetime2(3) NOT NULL);
            CREATE CLUSTERED INDEX CX_Events_HappenedOn ON Events (HappenedOn);
            CREATE TABLE Plain (Id int NOT NULL CONSTRAINT PK_Plain PRIMARY KEY);
            """);

        var (original, copy, copyManager, connection) = await RoundTripAsync();
        await using var _ = connection;

        var tables = copyManager.ReadDatabaseSchema().Tables;
        tables["Events"].PrimaryKey.Clustered.ShouldBeFalse();
        tables["Plain"].PrimaryKey.Clustered.ShouldBeTrue();
        copy.ShouldBe(original);
    }

    // #11: tables are created in dbo even when the login's default schema is something else
    [Fact]
    public void Creates_tables_in_dbo_whatever_the_default_schema()
    {
        Execute("CREATE SCHEMA other");
        Execute("""
            CREATE USER schema_tester WITHOUT LOGIN WITH DEFAULT_SCHEMA = other;
            GRANT CREATE TABLE TO schema_tester;
            GRANT ALTER, INSERT, SELECT, REFERENCES ON SCHEMA::dbo TO schema_tester;
            GRANT ALTER ON SCHEMA::other TO schema_tester;
            """);

        Execute("EXECUTE AS USER = 'schema_tester'");
        try
        {
            Manager.CheckDatabaseSchema(WriteSchema("""
                { "tables": [ { "name": "Things", "columns": [ { "name": "Id", "dataType": "int", "options": "NOT NULL" } ],
                                "defaultRecords": [ { "Id": 1 } ] } ] }
                """));
        }
        finally
        {
            Execute("REVERT");
        }

        Scalar<string>("SELECT SCHEMA_NAME(schema_id) FROM sys.tables WHERE name = 'Things'").ShouldBe("dbo");
        Scalar<int>("SELECT COUNT(*) FROM dbo.Things").ShouldBe(1);
    }

    // #12: a failure part way through leaves the database as it was
    [Fact]
    public void A_failure_rolls_back_everything_the_call_did()
    {
        Should.Throw<Microsoft.Data.SqlClient.SqlException>(() => Manager.CheckDatabaseSchema(WriteSchema("""
            { "tables": [
              { "name": "Good", "columns": [ { "name": "Id", "dataType": "int", "options": "NOT NULL" } ] },
              { "name": "Bad", "columns": [ { "name": "Id", "dataType": "not_a_type", "options": "NOT NULL" } ] }
            ] }
            """)));

        Tables().ShouldBeEmpty();
    }

    // #13: computed columns stay computed, identity seeds and increments are kept, and default constraint names too
    [Fact]
    public async Task Computed_columns_identity_settings_and_default_names_survive_a_round_trip()
    {
        Execute("""
            CREATE TABLE Lines (
                Id int IDENTITY(100,5) NOT NULL,
                Qty int NOT NULL CONSTRAINT DF_Lines_Qty DEFAULT (1),
                Price decimal(10,2) NOT NULL DEFAULT (0),
                Total AS (Qty * Price),
                StoredTotal AS (Qty * Price) PERSISTED NOT NULL
            );
            """);

        var (original, copy, _, connection) = await RoundTripAsync();
        await using var copyConnection = connection;

        copy.ShouldBe(original);

        using var command = new Microsoft.Data.SqlClient.SqlCommand("""
            SELECT
                (SELECT CONVERT(int, seed_value) FROM sys.identity_columns WHERE object_id = OBJECT_ID('Lines')),
                (SELECT CONVERT(int, increment_value) FROM sys.identity_columns WHERE object_id = OBJECT_ID('Lines')),
                (SELECT COUNT(*) FROM sys.default_constraints WHERE name = 'DF_Lines_Qty'),
                (SELECT COUNT(*) FROM sys.computed_columns WHERE object_id = OBJECT_ID('Lines')),
                (SELECT COUNT(*) FROM sys.computed_columns WHERE object_id = OBJECT_ID('Lines') AND is_persisted = 1)
            """, copyConnection);
        using var reader = command.ExecuteReader();
        reader.Read().ShouldBeTrue();
        (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)).ShouldBe((100, 5, 1, 2, 1));
    }

    [Fact]
    public void A_computed_column_can_be_added_to_an_existing_table()
    {
        Execute("CREATE TABLE Lines (Qty int NOT NULL, Price decimal(10,2) NOT NULL)");

        Manager.CheckDatabaseSchema(WriteSchema("""
            { "tables": [ { "name": "Lines", "columns": [
                { "name": "Qty", "dataType": "int", "options": "NOT NULL" },
                { "name": "Price", "dataType": "decimal(10,2)", "options": "NOT NULL" },
                { "name": "Total", "dataType": "AS ([Qty]*[Price])", "options": "PERSISTED" } ] } ] }
            """));

        Scalar<int>("SELECT COUNT(*) FROM sys.computed_columns WHERE object_id = OBJECT_ID('Lines') AND is_persisted = 1").ShouldBe(1);
    }
}
