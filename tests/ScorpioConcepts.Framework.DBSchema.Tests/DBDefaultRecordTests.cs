namespace ScorpioConcepts.Framework.DBSchema.Tests;

public class DBDefaultRecordTests
{
    [Fact]
    public void AddValue_stores_column_values()
    {
        var record = new DBDefaultRecord();

        record.AddValue("Id", 1);
        record.AddValue("Name", "Admin");

        record.Values.ShouldBe(new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "Admin" });
    }

    [Fact]
    public void AddValue_rejects_duplicate_column()
    {
        var record = new DBDefaultRecord();
        record.AddValue("Id", 1);

        Should.Throw<ArgumentException>(() => record.AddValue("Id", 2))
            .ParamName.ShouldBe("column");
    }

    [Fact]
    public void ToString_without_values()
    {
        new DBDefaultRecord().ToString().ShouldBe("DBDefaultRecord: No Data");
    }

    [Fact]
    public void ToString_with_one_value()
    {
        var record = new DBDefaultRecord();
        record.AddValue("Id", 1);

        record.ToString().ShouldBe("DBDefaultRecord: Id=1");
    }

    [Fact]
    public void ToString_with_two_values()
    {
        var record = new DBDefaultRecord();
        record.AddValue("Id", 1);
        record.AddValue("Name", "Admin");

        record.ToString().ShouldBe("DBDefaultRecord: Id=1,Name=Admin");
    }

    [Fact]
    public void ToString_with_more_than_two_values_summarises_the_rest()
    {
        var record = new DBDefaultRecord();
        record.AddValue("Id", 1);
        record.AddValue("Name", "Admin");
        record.AddValue("Active", true);
        record.AddValue("Level", 9);

        record.ToString().ShouldBe("DBDefaultRecord: Id=1,Name=Admin, 2 other...");
    }
}

public class DBDefaultRecordsTests
{
    [Fact]
    public void AddRecord_creates_list_for_new_table()
    {
        var records = new DBDefaultRecords();

        var record = records.AddRecord("Users");

        records.Tables.ShouldBe(new[] { "Users" });
        records["Users"].ShouldHaveSingleItem().ShouldBeSameAs(record);
    }

    [Fact]
    public void AddRecord_appends_to_existing_table()
    {
        var records = new DBDefaultRecords();

        var first = records.AddRecord("Users");
        var second = records.AddRecord("Users");
        records.AddRecord("Roles");

        records["Users"].ShouldBe(new[] { first, second });
        records["Roles"].Count.ShouldBe(1);
    }
}
