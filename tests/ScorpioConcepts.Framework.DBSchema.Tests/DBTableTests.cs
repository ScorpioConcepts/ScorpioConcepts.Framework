namespace ScorpioConcepts.Framework.DBSchema.Tests;

public class DBTableTests
{
    [Fact]
    public void New_table_starts_empty()
    {
        var table = new DBTable("Customers");

        table.Name.ShouldBe("Customers");
        table.Columns.ShouldBeEmpty();
        table.Indexes.ShouldBeEmpty();
        table.ForeignKeys.ShouldBeEmpty();
        table.DefaultRecords.ShouldBeEmpty();
        table.PrimaryKey.ShouldNotBeNull();
        table.PrimaryKey.Name.ShouldBeNull();
        table.PrimaryKey.Columns.ShouldBeEmpty();
    }

    [Fact]
    public void AddColumn_adds_column_that_FindColumn_returns()
    {
        var table = new DBTable("Customers");

        table.AddColumn("Id", "int identity", "NOT NULL");
        table.AddColumn("Name", "varchar(50)", "NULL");

        table.Columns.Count.ShouldBe(2);
        var column = table.FindColumn("Name");
        column.ShouldNotBeNull();
        column.DataType.ShouldBe("varchar(50)");
        column.Options.ShouldBe("NULL");
    }

    [Fact]
    public void FindColumn_returns_null_for_unknown_column()
    {
        var table = new DBTable("Customers");
        table.AddColumn("Id", "int", "NOT NULL");

        table.FindColumn("Missing").ShouldBeNull();
    }

    [Fact]
    public void AddIndex_returns_index_that_FindIndex_returns()
    {
        var table = new DBTable("Customers");

        var index = table.AddIndex("IX_Name", clustered: false, unique: true);
        index.AddColumn("LastName", descending: false);
        index.AddColumn("FirstName", descending: true);

        var found = table.FindIndex("IX_Name");
        found.ShouldBeSameAs(index);
        found!.Unique.ShouldBeTrue();
        found.Clustered.ShouldBeFalse();
        found.Columns.Select(c => (c.Name, c.Descending)).ShouldBe(new[] { ("LastName", false), ("FirstName", true) });
    }

    [Fact]
    public void FindIndex_returns_null_for_unknown_index()
    {
        new DBTable("Customers").FindIndex("IX_Missing").ShouldBeNull();
    }

    [Fact]
    public void AddIndexColumn_rejects_duplicate_column()
    {
        var index = new DBTable("Customers").AddIndex("IX_Name", false, false);
        index.AddColumn("Name", false);

        Should.Throw<ArgumentException>(() => index.AddColumn("Name", true))
            .ParamName.ShouldBe("columnName");
    }

    [Fact]
    public void PrimaryKey_AddKeyColumn_keeps_column_order()
    {
        var table = new DBTable("OrderLines");

        table.PrimaryKey.AddColumn("OrderId");
        table.PrimaryKey.AddColumn("LineNo");

        table.PrimaryKey.Columns.ShouldBe(new[] { "OrderId", "LineNo" });
    }

    [Fact]
    public void PrimaryKey_AddKeyColumn_rejects_duplicate_column()
    {
        var table = new DBTable("Customers");
        table.PrimaryKey.AddColumn("Id");

        Should.Throw<ArgumentException>(() => table.PrimaryKey.AddColumn("Id"))
            .ParamName.ShouldBe("columnName");
    }

    [Fact]
    public void AddForeignKey_adds_key_that_FindForeignKey_returns()
    {
        var table = new DBTable("Orders");

        table.AddForeignKey("FK_Orders_Customers", "CustomerId", "Customers", "Id");

        var fkey = table.FindForeignKey("FK_Orders_Customers");
        fkey.ShouldNotBeNull();
        fkey.Column.ShouldBe("CustomerId");
        fkey.ReferencedTable.ShouldBe("Customers");
        fkey.ReferencedColumn.ShouldBe("Id");
    }

    [Fact]
    public void FindForeignKey_returns_null_for_unknown_key()
    {
        new DBTable("Orders").FindForeignKey("FK_Missing").ShouldBeNull();
    }

    [Fact]
    public void AddDefaultRecord_adds_empty_record_to_table()
    {
        var table = new DBTable("Settings");

        var record = table.AddDefaultRecord();

        table.DefaultRecords.ShouldHaveSingleItem().ShouldBeSameAs(record);
        record.Values.ShouldBeEmpty();
    }

    [Fact]
    public void ToString_methods_describe_the_objects()
    {
        var table = new DBTable("Orders");
        table.AddColumn("Id", "int", "NOT NULL");
        table.PrimaryKey.Name = "PK_Orders";
        table.PrimaryKey.AddColumn("Id");
        var index = table.AddIndex("IX_Date", false, false);
        index.AddColumn("Date", false);
        table.AddForeignKey("FK_Orders_Customers", "CustomerId", "Customers", "Id");

        table.ToString().ShouldBe("DBTable Name=Orders, Columns=1, DBPrimaryKey Name=PK_Orders, Columns=1");
        table.Columns[0].ToString().ShouldBe("DBColumn Name='Id', DataType='int', Options='NOT NULL'");
        index.ToString().ShouldBe("DBIndex Name=IX_Date, Columns=1");
        table.ForeignKeys[0].ToString().ShouldBe("DBForeignKey Name=FK_Orders_Customers, Column=CustomerId, ReferencedTable=Customers, ReferencedColumn=Id");
    }
}
