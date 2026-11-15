namespace ScorpioConcepts.Framework.DBSchema.Tests;

/// <summary>
/// Tests for reading and writing the schema XML format, without a database.
/// </summary>
public class SchemaXmlTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"dbschema-{Guid.NewGuid():N}.xml");

    public void Dispose()
    {
        // Tolerate a locked file; see Parse_failure_releases_the_file.
        try { File.Delete(_file); } catch (IOException) { }
    }

    [Fact]
    public void Reads_hand_written_schema_xml()
    {
        File.WriteAllText(_file, """
            <tables>
              <table name="Customers">
                <column name="Id" datatype="int identity" options="NOT NULL" />
                <column name="Name" datatype="varchar(50)" options="NOT NULL DEFAULT ('')"></column>
                <primary_key name="PK_Customers">
                  <column name="Id" />
                </primary_key>
                <index name="IX_Customers_Name" clustered="False" unique="True">
                  <column name="Name" desc="True" />
                </index>
              </table>
              <table name="Orders">
                <column name="Id" datatype="int" options="NOT NULL" />
                <column name="CustomerId" datatype="int" options="NOT NULL" />
                <foreign_key name="FK_Orders_Customers" column="CustomerId" pk_table="Customers" pk_column="Id" />
                <default_record>
                  <columnValue column="Id">1</columnValue>
                  <columnValue column="CustomerId"><![CDATA[1]]></columnValue>
                </default_record>
              </table>
            </tables>
            """);

        var schema = DBSchema.populateXMLSchema(_file);

        schema.Tables.Keys.ShouldBe(new[] { "Customers", "Orders" });

        var customers = schema.Tables["Customers"];
        customers.Columns.Select(c => (c.Name, c.DataType, c.Options)).ShouldBe(new[]
        {
            ("Id", "int identity", "NOT NULL"),
            ("Name", "varchar(50)", "NOT NULL DEFAULT ('')"),
        });
        customers.PrimaryKey.Name.ShouldBe("PK_Customers");
        customers.PrimaryKey.KeyColumns.ShouldBe(new[] { "Id" });
        var index = customers.Indexes.ShouldHaveSingleItem();
        index.Name.ShouldBe("IX_Customers_Name");
        index.Unique.ShouldBeTrue();
        index.Clustered.ShouldBeFalse();
        index.Columns.ShouldBe(new Dictionary<string, bool> { ["Name"] = true });

        var orders = schema.Tables["Orders"];
        var fkey = orders.ForeignKeys.ShouldHaveSingleItem();
        (fkey.Name, fkey.Column, fkey.PKeyTable, fkey.PKeyColumn)
            .ShouldBe(("FK_Orders_Customers", "CustomerId", "Customers", "Id"));
        orders.DefaultRecords.ShouldHaveSingleItem().Values
            .ShouldBe(new Dictionary<string, object> { ["Id"] = "1", ["CustomerId"] = "1" });
    }

    [Fact]
    public void Reads_empty_schema()
    {
        File.WriteAllText(_file, "<tables></tables>");

        DBSchema.populateXMLSchema(_file).Tables.ShouldBeEmpty();
    }

    [Fact]
    public void Rejects_duplicate_table_names()
    {
        File.WriteAllText(_file, """<tables><table name="A"></table><table name="A"></table></tables>""");

        Should.Throw<ArgumentException>(() => DBSchema.populateXMLSchema(_file));
    }

    [Fact(Skip = "Known bug: populateXMLSchema does not close its XmlReader when parsing fails, so the file stays locked")]
    public void Parse_failure_releases_the_file()
    {
        File.WriteAllText(_file, """<tables><table name="A"></table><table name="A"></table></tables>""");

        Should.Throw<ArgumentException>(() => DBSchema.populateXMLSchema(_file));

        Should.NotThrow(() => File.Delete(_file));
    }

    [Fact]
    public void Written_schema_reads_back_identically()
    {
        var schema = new DBSchema();
        var customers = schema.AddTable("Customers");
        customers.AddColumn("Id", "int identity", "NOT NULL");
        customers.AddColumn("Name", "varchar(50)", "NULL");
        customers.PrimaryKey.Name = "PK_Customers";
        customers.PrimaryKey.AddKeyColumn("Id");
        var index = customers.AddIndex("IX_Customers_Name", clustered: false, unique: true);
        index.AddIndexColumn("Name", descending: true);

        var orders = schema.AddTable("Orders");
        orders.AddColumn("Id", "int", "NOT NULL");
        orders.AddColumn("CustomerId", "int", "NOT NULL");
        orders.PrimaryKey.Name = "PK_Orders";
        orders.PrimaryKey.AddKeyColumn("Id");
        orders.AddForeignKey("FK_Orders_Customers", "CustomerId", "Customers", "Id");

        DBSchema.WriteSchemaXML(schema, _file, new DBDefaultRecords());
        var read = DBSchema.populateXMLSchema(_file);

        read.Tables.Keys.ShouldBe(new[] { "Customers", "Orders" });
        foreach (var expected in schema.Tables.Values)
        {
            var actual = read.Tables[expected.Name];
            actual.Columns.Select(c => (c.Name, c.DataType, c.Options))
                .ShouldBe(expected.Columns.Select(c => (c.Name, c.DataType, c.Options)));
            actual.PrimaryKey.Name.ShouldBe(expected.PrimaryKey.Name);
            actual.PrimaryKey.KeyColumns.ShouldBe(expected.PrimaryKey.KeyColumns);
            actual.Indexes.Select(i => (i.Name, i.Clustered, i.Unique, string.Join(",", i.Columns)))
                .ShouldBe(expected.Indexes.Select(i => (i.Name, i.Clustered, i.Unique, string.Join(",", i.Columns))));
            actual.ForeignKeys.Select(f => (f.Name, f.Column, f.PKeyTable, f.PKeyColumn))
                .ShouldBe(expected.ForeignKeys.Select(f => (f.Name, f.Column, f.PKeyTable, f.PKeyColumn)));
        }
    }

    [Fact]
    public void Table_without_primary_key_writes_no_primary_key_element()
    {
        var schema = new DBSchema();
        schema.AddTable("Log").AddColumn("Message", "varchar(100)", "NULL");

        DBSchema.WriteSchemaXML(schema, _file, new DBDefaultRecords());

        File.ReadAllText(_file).ShouldNotContain("primary_key");
        DBSchema.populateXMLSchema(_file).Tables["Log"].PrimaryKey.KeyColumns.ShouldBeEmpty();
    }

    [Fact]
    public void Default_records_are_written_and_read_back_as_text()
    {
        var schema = new DBSchema();
        var settings = schema.AddTable("Settings");
        settings.AddColumn("Id", "int", "NOT NULL");
        settings.AddColumn("Value", "varchar(100)", "NULL");

        var defaults = new DBDefaultRecords();
        var record = defaults.AddRecord("Settings");
        record.AddValue("Id", 42);
        record.AddValue("Value", "<needs & escaping>");
        defaults.AddRecord("NotInSchema").AddValue("Id", 1);

        DBSchema.WriteSchemaXML(schema, _file, defaults);
        var read = DBSchema.populateXMLSchema(_file);

        // Values are not typed in the XML, so everything comes back as a string.
        read.Tables["Settings"].DefaultRecords.ShouldHaveSingleItem().Values
            .ShouldBe(new Dictionary<string, object> { ["Id"] = "42", ["Value"] = "<needs & escaping>" });
        read.Tables.ContainsKey("NotInSchema").ShouldBeFalse();
    }

    [Fact]
    public void WriteSchemaXML_records_progress_in_DebugLog()
    {
        var schema = new DBSchema();
        schema.AddTable("Customers").AddColumn("Id", "int", "NOT NULL");

        DBSchema.WriteSchemaXML(schema, _file, new DBDefaultRecords());

        DBSchema.DebugLog.ShouldContain(line => line.Contains("Customers"));
    }
}

public class DBSchemaModelTests
{
    [Fact]
    public void AddTable_rejects_duplicate_table()
    {
        var schema = new DBSchema();
        schema.AddTable("Customers");

        Should.Throw<ArgumentException>(() => schema.AddTable("Customers"))
            .ParamName.ShouldBe("tableName");
    }
}
