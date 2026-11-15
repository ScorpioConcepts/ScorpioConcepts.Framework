namespace ScorpioConcepts.Framework.DBSchema.IntegrationTests;

public class CheckDatabaseSchemaTests(SqlServerFixture fixture) : DatabaseTestBase(fixture)
{
    // Orders comes before Customers on purpose: foreign keys must only be added once every table exists.
    private const string ShopSchema = """
        <tables>
          <table name="Orders">
            <column name="Id" datatype="int identity" options="NOT NULL" />
            <column name="CustomerId" datatype="int" options="NOT NULL" />
            <column name="PlacedOn" datatype="datetime" options="NOT NULL DEFAULT (getdate())" />
            <primary_key name="PK_Orders">
              <column name="Id" />
            </primary_key>
            <index name="IX_Orders_PlacedOn" clustered="False" unique="False">
              <column name="PlacedOn" desc="True" />
            </index>
            <foreign_key name="FK_Orders_Customers" column="CustomerId" pk_table="Customers" pk_column="Id" />
          </table>
          <table name="Customers">
            <column name="Id" datatype="int identity" options="NOT NULL" />
            <column name="Name" datatype="varchar(50)" options="NOT NULL" />
            <column name="Email" datatype="varchar(100)" options="NULL" />
            <primary_key name="PK_Customers">
              <column name="Id" />
            </primary_key>
            <index name="IX_Customers_Email" clustered="False" unique="True">
              <column name="Email" desc="False" />
            </index>
          </table>
        </tables>
        """;

    [Fact]
    public void Creates_missing_tables_with_columns_keys_and_indexes()
    {
        Manager.CheckDatabaseSchema(WriteSchema(ShopSchema));

        Tables().ShouldBe(new[] { "Customers", "Orders" });

        Columns("Customers").ShouldBe(new[] { "Id int NOT NULL", "Name varchar(50) NOT NULL", "Email varchar(100) NULL" });
        IsIdentity("Customers", "Id").ShouldBeTrue();
        PrimaryKey("Customers").ShouldBe("PK_Customers: Id");
        Indexes("Customers").ShouldBe(new[] { "IX_Customers_Email UNIQUE: Email" });

        Columns("Orders").ShouldBe(new[] { "Id int NOT NULL", "CustomerId int NOT NULL", "PlacedOn datetime NOT NULL" });
        ColumnDefault("Orders", "PlacedOn").ShouldBe("(getdate())");
        PrimaryKey("Orders").ShouldBe("PK_Orders: Id");
        Indexes("Orders").ShouldBe(new[] { "IX_Orders_PlacedOn: PlacedOn DESC" });
        ForeignKeys("Orders").ShouldBe(new[] { "FK_Orders_Customers: CustomerId -> Customers.Id" });
    }

    [Fact]
    public void Composite_primary_key_keeps_column_order()
    {
        Manager.CheckDatabaseSchema(WriteSchema("""
            <tables>
              <table name="OrderLines">
                <column name="OrderId" datatype="int" options="NOT NULL" />
                <column name="LineNo" datatype="int" options="NOT NULL" />
                <primary_key name="PK_OrderLines">
                  <column name="OrderId" />
                  <column name="LineNo" />
                </primary_key>
              </table>
            </tables>
            """));

        PrimaryKey("OrderLines").ShouldBe("PK_OrderLines: OrderId,LineNo");
    }

    [Fact]
    public void Creates_table_without_primary_key()
    {
        Manager.CheckDatabaseSchema(WriteSchema("""
            <tables>
              <table name="AuditLog">
                <column name="Message" datatype="varchar(200)" options="NULL" />
              </table>
            </tables>
            """));

        Columns("AuditLog").ShouldBe(new[] { "Message varchar(200) NULL" });
        PrimaryKey("AuditLog").ShouldBeNull();
    }

    [Fact]
    public void Creates_clustered_index_on_table_without_primary_key()
    {
        Manager.CheckDatabaseSchema(WriteSchema("""
            <tables>
              <table name="Readings">
                <column name="TakenAt" datatype="datetime" options="NOT NULL" />
                <column name="Value" datatype="float" options="NULL" />
                <index name="CIX_Readings_TakenAt" clustered="True" unique="False">
                  <column name="TakenAt" desc="False" />
                </index>
              </table>
            </tables>
            """));

        Indexes("Readings").ShouldBe(new[] { "CIX_Readings_TakenAt CLUSTERED: TakenAt" });
    }

    [Fact]
    public void Inserts_default_records_into_new_tables()
    {
        Manager.CheckDatabaseSchema(WriteSchema("""
            <tables>
              <table name="Settings">
                <column name="Id" datatype="int" options="NOT NULL" />
                <column name="Name" datatype="varchar(50)" options="NOT NULL" />
                <column name="Value" datatype="varchar(200)" options="NULL" />
                <primary_key name="PK_Settings"><column name="Id" /></primary_key>
                <default_record>
                  <columnValue column="Id">1</columnValue>
                  <columnValue column="Name"><![CDATA[Theme]]></columnValue>
                  <columnValue column="Value"><![CDATA[Dark & <bold>]]></columnValue>
                </default_record>
                <default_record>
                  <columnValue column="Id">2</columnValue>
                  <columnValue column="Name"><![CDATA[Language]]></columnValue>
                </default_record>
              </table>
            </tables>
            """));

        Query("SELECT Id, Name, Value FROM Settings ORDER BY Id", r => (r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)))
            .ShouldBe(new (int, string, string?)[] { (1, "Theme", "Dark & <bold>"), (2, "Language", null) });
    }

    [Fact]
    public void Running_again_changes_nothing_and_does_not_reinsert_default_records()
    {
        string schema = WriteSchema("""
            <tables>
              <table name="Settings">
                <column name="Id" datatype="int" options="NOT NULL" />
                <primary_key name="PK_Settings"><column name="Id" /></primary_key>
                <index name="IX_Settings_Id" clustered="False" unique="False"><column name="Id" desc="False" /></index>
                <default_record><columnValue column="Id">1</columnValue></default_record>
              </table>
            </tables>
            """);
        Manager.CheckDatabaseSchema(schema);
        Log.Clear();

        Manager.CheckDatabaseSchema(schema);

        Scalar<int>("SELECT COUNT(*) FROM Settings").ShouldBe(1);
        Indexes("Settings").ShouldBe(new[] { "IX_Settings_Id: Id" });
        Log.Messages.ShouldNotContain(line => line.StartsWith("Creating"));
    }

    [Fact]
    public void Adds_missing_columns_to_existing_table_and_keeps_data()
    {
        Execute("""
            CREATE TABLE Customers (Id int NOT NULL CONSTRAINT PK_Customers PRIMARY KEY, Name varchar(50) NOT NULL);
            INSERT INTO Customers (Id, Name) VALUES (1, 'Ada');
            """);

        Manager.CheckDatabaseSchema(WriteSchema("""
            <tables>
              <table name="Customers">
                <column name="Id" datatype="int" options="NOT NULL" />
                <column name="Name" datatype="varchar(50)" options="NOT NULL" />
                <column name="Email" datatype="varchar(100)" options="NULL" />
                <column name="Active" datatype="bit" options="NOT NULL DEFAULT ((1))" />
                <primary_key name="PK_Customers"><column name="Id" /></primary_key>
              </table>
            </tables>
            """));

        Columns("Customers").ShouldBe(new[] { "Id int NOT NULL", "Name varchar(50) NOT NULL", "Email varchar(100) NULL", "Active bit NOT NULL" });
        Query("SELECT Id, Name, Email, Active FROM Customers", r => (r.GetInt32(0), r.GetString(1), r.IsDBNull(2), r.GetBoolean(3)))
            .ShouldBe(new[] { (1, "Ada", true, true) });
    }

    [Fact]
    public void Adds_missing_indexes_and_foreign_keys_to_existing_tables()
    {
        Execute("""
            CREATE TABLE Customers (Id int NOT NULL CONSTRAINT PK_Customers PRIMARY KEY, Name varchar(50) NOT NULL);
            CREATE TABLE Orders (Id int NOT NULL CONSTRAINT PK_Orders PRIMARY KEY, CustomerId int NOT NULL);
            """);

        Manager.CheckDatabaseSchema(WriteSchema("""
            <tables>
              <table name="Customers">
                <column name="Id" datatype="int" options="NOT NULL" />
                <column name="Name" datatype="varchar(50)" options="NOT NULL" />
                <index name="IX_Customers_Name" clustered="False" unique="False"><column name="Name" desc="False" /></index>
              </table>
              <table name="Orders">
                <column name="Id" datatype="int" options="NOT NULL" />
                <column name="CustomerId" datatype="int" options="NOT NULL" />
                <foreign_key name="FK_Orders_Customers" column="CustomerId" pk_table="Customers" pk_column="Id" />
              </table>
            </tables>
            """));

        Indexes("Customers").ShouldBe(new[] { "IX_Customers_Name: Name" });
        ForeignKeys("Orders").ShouldBe(new[] { "FK_Orders_Customers: CustomerId -> Customers.Id" });
    }

    [Fact]
    public void Leaves_tables_and_columns_that_are_not_in_the_schema()
    {
        Execute("""
            CREATE TABLE Customers (Id int NOT NULL, Legacy varchar(10) NULL);
            CREATE TABLE Unrelated (Id int NOT NULL);
            """);

        Manager.CheckDatabaseSchema(WriteSchema("""
            <tables>
              <table name="Customers">
                <column name="Id" datatype="int" options="NOT NULL" />
              </table>
            </tables>
            """));

        Tables().ShouldBe(new[] { "Customers", "Unrelated" });
        Columns("Customers").ShouldBe(new[] { "Id int NOT NULL", "Legacy varchar(10) NULL" });
    }

    [Fact]
    public void Logs_what_it_did()
    {
        Manager.CheckDatabaseSchema(WriteSchema(ShopSchema));

        Log.Messages.ShouldContain("Creating table 'Customers'");
        Log.Messages.ShouldContain("Creating table 'Orders'");
        Log.Messages.ShouldContain("Creating foreign key 'FK_Orders_Customers' in table 'Orders'");
    }
}
