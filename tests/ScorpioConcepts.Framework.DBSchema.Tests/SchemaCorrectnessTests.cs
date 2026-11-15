namespace ScorpioConcepts.Framework.DBSchema.Tests;

/// <summary>
/// The file format and model side of the correctness fixes: composite foreign keys (#7), case-insensitive names
/// (#8), the clustered primary key flag (#10), identity seeds (#13) and identifier quoting (#5).
/// </summary>
public sealed class SchemaCorrectnessTests : IDisposable
{
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var file in _files) File.Delete(file);
    }

    private string Write(string content, string extension = ".json")
    {
        string path = Path.Combine(Path.GetTempPath(), $"dbschema-{Guid.NewGuid():N}{extension}");
        _files.Add(path);
        File.WriteAllText(path, content);
        return path;
    }

    private string TempFile(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"dbschema-{Guid.NewGuid():N}{extension}");
        _files.Add(path);
        return path;
    }

    private static DBSchemaDefinition CompositeSchema()
    {
        var schema = new DBSchemaDefinition();
        var lines = schema.AddTable("OrderLines");
        lines.AddColumn("OrderId", "int", "NOT NULL");
        lines.AddColumn("LineNo", "int", "NOT NULL");
        lines.PrimaryKey.Name = "PK_OrderLines";
        lines.PrimaryKey.Clustered = false;
        lines.PrimaryKey.AddColumn("OrderId");
        lines.PrimaryKey.AddColumn("LineNo");

        var deliveries = schema.AddTable("Deliveries");
        deliveries.AddColumn("OrderId", "int", "NOT NULL");
        deliveries.AddColumn("LineNo", "int", "NOT NULL");
        var key = deliveries.AddForeignKey("FK_Deliveries_OrderLines", "OrderLines");
        key.AddColumn("OrderId", "OrderId");
        key.AddColumn("LineNo", "LineNo");
        deliveries.AddForeignKey("FK_Single", "OrderId", "OrderLines", "OrderId");
        return schema;
    }

    [Theory]
    [InlineData(DBSchemaFormat.Json, ".json")]
    [InlineData(DBSchemaFormat.Xml, ".xml")]
    public void Composite_foreign_keys_and_nonclustered_primary_keys_round_trip(DBSchemaFormat format, string extension)
    {
        string file = TempFile(extension);

        CompositeSchema().Save(file, format);
        var read = DBSchemaDefinition.Load(file);

        read.Tables["OrderLines"].PrimaryKey.Clustered.ShouldBeFalse();
        var keys = read.Tables["Deliveries"].ForeignKeys;
        keys[0].Columns.ShouldBe(new[] { "OrderId", "LineNo" });
        keys[0].ReferencedColumns.ShouldBe(new[] { "OrderId", "LineNo" });
        keys[0].ReferencedTable.ShouldBe("OrderLines");
        keys[1].Columns.ShouldBe(new[] { "OrderId" });
    }

    [Fact]
    public void Single_column_keys_keep_the_original_file_shape()
    {
        string json = TempFile(".json");
        string xml = TempFile(".xml");
        var schema = CompositeSchema();

        schema.Save(json);
        schema.Save(xml, DBSchemaFormat.Xml);

        string jsonText = File.ReadAllText(json);
        jsonText.ShouldContain("\"column\": \"OrderId\"");
        jsonText.ShouldContain("\"referencedColumns\": [");
        string xmlText = File.ReadAllText(xml);
        xmlText.ShouldContain("""<foreign_key name="FK_Single" column="OrderId" pk_table="OrderLines" pk_column="OrderId" />""");
        xmlText.ShouldContain("""<column name="LineNo" pk_column="LineNo" />""");
    }

    [Fact]
    public void Clustered_is_only_written_for_a_nonclustered_primary_key()
    {
        var schema = new DBSchemaDefinition();
        var table = schema.AddTable("Plain");
        table.AddColumn("Id", "int", "NOT NULL");
        table.PrimaryKey.Name = "PK_Plain";
        table.PrimaryKey.AddColumn("Id");
        string json = TempFile(".json");
        string xml = TempFile(".xml");

        schema.Save(json);
        schema.Save(xml, DBSchemaFormat.Xml);

        // Files for clustered keys, the usual case, look exactly as they did before
        File.ReadAllText(json).ShouldNotContain("\"clustered\"");
        File.ReadAllText(xml).ShouldNotContain("clustered");
        DBSchemaDefinition.Load(json).Tables["Plain"].PrimaryKey.Clustered.ShouldBeTrue();
        DBSchemaDefinition.Load(xml).Tables["Plain"].PrimaryKey.Clustered.ShouldBeTrue();
    }

    [Fact]
    public void Reads_composite_foreign_keys_from_hand_written_files()
    {
        var json = DBSchemaDefinition.Load(Write("""
            { "tables": [ { "name": "D", "foreignKeys": [
                { "name": "FK", "columns": [ "A", "B" ], "referencedTable": "P", "referencedColumns": [ "X", "Y" ] } ] } ] }
            """));
        var xml = DBSchemaDefinition.Load(Write("""
            <tables><table name="D">
              <foreign_key name="FK" pk_table="P"><column name="A" pk_column="X" /><column name="B" pk_column="Y" /></foreign_key>
            </table></tables>
            """, ".xml"));

        foreach (var schema in new[] { json, xml })
        {
            var key = schema.Tables["D"].ForeignKeys.ShouldHaveSingleItem();
            key.Columns.ShouldBe(new[] { "A", "B" });
            key.ReferencedColumns.ShouldBe(new[] { "X", "Y" });
        }
    }

    [Theory]
    [InlineData("""{ "tables": [ { "name": "D", "foreignKeys": [ { "name": "FK", "columns": [ "A", "B" ], "referencedTable": "P", "referencedColumns": [ "X" ] } ] } ] }""")]
    [InlineData("""{ "tables": [ { "name": "D", "foreignKeys": [ { "name": "FK", "columns": [], "referencedTable": "P", "referencedColumns": [] } ] } ] }""")]
    public void Foreign_key_columns_must_match_one_for_one(string json)
    {
        Should.Throw<FormatException>(() => DBSchemaDefinition.Load(Write(json))).Message.ShouldContain("same number of columns");
    }

    [Fact]
    public void A_foreign_key_without_columns_is_rejected_in_xml()
    {
        Should.Throw<FormatException>(() => DBSchemaDefinition.Load(Write("""<tables><table name="D"><foreign_key name="FK" pk_table="P" /></table></tables>""", ".xml")))
            .Message.ShouldContain("has no columns");
    }

    [Fact]
    public void Names_are_not_case_sensitive()
    {
        var schema = CompositeSchema();
        var table = schema.Tables["orderlines"];

        table.Name.ShouldBe("OrderLines");
        table.FindColumn("ORDERID").ShouldNotBeNull();
        schema.Tables["DELIVERIES"].FindForeignKey("fk_single").ShouldNotBeNull();
        Should.Throw<ArgumentException>(() => schema.AddTable("ORDERLINES"));
        Should.Throw<ArgumentException>(() => table.PrimaryKey.AddColumn("orderid"));

        var records = new DBDefaultRecords();
        records.AddRecord("Settings").AddValue("Id", 1);
        records["SETTINGS"].ShouldHaveSingleItem();
        Should.Throw<ArgumentException>(() => records["settings"][0].AddValue("ID", 2));
    }

    [Theory]
    [InlineData("Plain", "[Plain]")]
    [InlineData("Odd]Name", "[Odd]]Name]")]
    [InlineData("]]", "[]]]]]")]
    [InlineData("x]; DROP TABLE Users; --", "[x]]; DROP TABLE Users; --]")]
    public void Quote_doubles_closing_brackets(string name, string expected)
    {
        SqlName.Quote(name).ShouldBe(expected);
        SqlName.Table(name).ShouldBe("[dbo]." + expected);
    }

    [Theory]
    [InlineData(null, null, "int identity")]
    [InlineData("1", "1", "int identity")]
    [InlineData("100", "5", "int identity(100,5)")]
    [InlineData("-1", "-1", "int identity(-1,-1)")]
    public void Identity_seed_and_increment_are_written_unless_they_are_the_default(string? seed, string? increment, string expected)
    {
        DBSchemaManager.FormatDataType("int", 4, 10, 0, isIdentity: true, seed, increment).ShouldBe(expected);
    }

    [Theory]
    [InlineData("AS ([Qty]*[Price])", true)]
    [InlineData("as([Qty]*[Price])", true)]
    [InlineData("int", false)]
    [InlineData("ASCII_TYPE", false)]
    public void Computed_columns_are_recognised_by_their_expression(string dataType, bool computed)
    {
        new DBTable("T").AddColumn("C", dataType, "").IsComputed.ShouldBe(computed);
    }
}
