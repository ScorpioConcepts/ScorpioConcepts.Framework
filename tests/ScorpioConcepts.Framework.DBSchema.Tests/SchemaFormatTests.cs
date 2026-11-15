using System.Text;

namespace ScorpioConcepts.Framework.DBSchema.Tests;

/// <summary>
/// Tests for choosing between the JSON and XML formats, and converting between them.
/// </summary>
public sealed class SchemaFormatTests : IDisposable
{
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var file in _files) File.Delete(file);
    }

    private string TempFile(string extension = ".tmp")
    {
        string path = Path.Combine(Path.GetTempPath(), $"dbschema-{Guid.NewGuid():N}{extension}");
        _files.Add(path);
        return path;
    }

    private string WriteFile(string content, Encoding? encoding = null, string extension = ".tmp")
    {
        string path = TempFile(extension);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    [Theory]
    [InlineData("""{ "tables": [] }""", DBSchemaFormat.Json)]
    [InlineData("  \r\n\t{}", DBSchemaFormat.Json)]
    [InlineData("<tables></tables>", DBSchemaFormat.Xml)]
    [InlineData("""<?xml version="1.0" encoding="utf-8"?><tables />""", DBSchemaFormat.Xml)]
    [InlineData("\n\n  <tables />", DBSchemaFormat.Xml)]
    [InlineData("// JSON files may start with a comment\n{}", DBSchemaFormat.Json)]
    [InlineData("/* so may */ { \"tables\": [] }", DBSchemaFormat.Json)]
    [InlineData("Table A {\n  Id int\n}", DBSchemaFormat.Dbml)]
    [InlineData("  table A {}", DBSchemaFormat.Dbml)]
    [InlineData("// Shop schema\n/* v2 */\nRef: A.b > C.d", DBSchemaFormat.Dbml)]
    [InlineData("Project shop {}", DBSchemaFormat.Dbml)]
    [InlineData("---\ntitle: Shop\n---\nTable A {}", DBSchemaFormat.Dbml)]
    public void Detects_format_from_content(string content, DBSchemaFormat expected)
    {
        DBSchema.DetectFormat(WriteFile(content)).ShouldBe(expected);
    }

    [Fact]
    public void Detects_format_after_a_byte_order_mark()
    {
        DBSchema.DetectFormat(WriteFile("{}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))).ShouldBe(DBSchemaFormat.Json);
        DBSchema.DetectFormat(WriteFile("<tables />", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))).ShouldBe(DBSchemaFormat.Xml);
    }

    [Fact]
    public void Content_wins_over_a_misleading_file_extension()
    {
        DBSchema.DetectFormat(WriteFile("<tables />", extension: ".json")).ShouldBe(DBSchemaFormat.Xml);
        DBSchema.DetectFormat(WriteFile("{}", extension: ".xml")).ShouldBe(DBSchemaFormat.Json);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tables: []")]
    [InlineData("// only a comment")]
    [InlineData("Tables A {}")]
    public void Rejects_files_that_are_neither_format(string content)
    {
        string path = WriteFile(content);

        Should.Throw<FormatException>(() => DBSchema.DetectFormat(path)).Message.ShouldContain(path);
    }

    [Fact]
    public void ConvertSchema_turns_xml_into_json_with_default_records()
    {
        string xml = WriteFile("""
            <tables>
              <table name="Settings">
                <column name="Id" datatype="int" options="NOT NULL" />
                <column name="Name" datatype="varchar(50)" options="NOT NULL" />
                <primary_key name="PK_Settings"><column name="Id" /></primary_key>
                <index name="IX_Settings_Name" clustered="False" unique="True"><column name="Name" desc="False" /></index>
                <default_record>
                  <columnValue column="Id">1</columnValue>
                  <columnValue column="Name"><![CDATA[Theme]]></columnValue>
                </default_record>
              </table>
            </tables>
            """);
        string json = TempFile(".json");

        DBSchema.ConvertSchema(xml, json, DBSchemaFormat.Json);

        DBSchema.DetectFormat(json).ShouldBe(DBSchemaFormat.Json);
        var converted = DBSchemaJson.Read(json);
        SchemaAssert.SameStructure(converted, DBSchemaXml.Read(xml));
        // XML values are untyped, so they arrive in JSON as strings
        converted.Tables["Settings"].DefaultRecords.ShouldHaveSingleItem().Values
            .ShouldBe(new Dictionary<string, object> { ["Id"] = "1", ["Name"] = "Theme" });
    }

    [Fact]
    public void ConvertSchema_turns_json_into_xml()
    {
        string json = WriteFile("""
            {
              "tables": [
                {
                  "name": "Orders",
                  "columns": [ { "name": "Id", "dataType": "int", "options": "NOT NULL" }, { "name": "CustomerId", "dataType": "int", "options": "NULL" } ],
                  "foreignKeys": [ { "name": "FK_Orders_Customers", "column": "CustomerId", "referencedTable": "Customers", "referencedColumn": "Id" } ],
                  "defaultRecords": [ { "Id": 1, "CustomerId": null } ]
                }
              ]
            }
            """);
        string xml = TempFile(".xml");

        DBSchema.ConvertSchema(json, xml, DBSchemaFormat.Xml);

        var converted = DBSchemaXml.Read(xml);
        SchemaAssert.SameStructure(converted, DBSchemaJson.Read(json));
        // XML cannot hold null, so the null column is left out of the record
        converted.Tables["Orders"].DefaultRecords.ShouldHaveSingleItem().Values
            .ShouldBe(new Dictionary<string, object> { ["Id"] = "1" });
    }

    [Fact]
    public void ConvertSchema_turns_json_into_dbml_and_back()
    {
        string json = WriteFile("""
            {
              "tables": [
                {
                  "name": "Customers",
                  "columns": [
                    { "name": "Id", "dataType": "int identity", "options": "NOT NULL" },
                    { "name": "Name", "dataType": "nvarchar(50)", "options": "NOT NULL DEFAULT ('')" }
                  ],
                  "primaryKey": { "name": "PK_Customers", "columns": [ "Id" ] },
                  "indexes": [ { "name": "IX_Customers_Name", "unique": true, "columns": [ "Name" ] } ],
                  "defaultRecords": [ { "Id": 1, "Name": "Walk-in" } ]
                },
                {
                  "name": "Orders",
                  "columns": [ { "name": "Id", "dataType": "int", "options": "NOT NULL" }, { "name": "CustomerId", "dataType": "int", "options": "NULL" } ],
                  "foreignKeys": [ { "name": "FK_Orders_Customers", "column": "CustomerId", "referencedTable": "Customers", "referencedColumn": "Id" } ]
                }
              ]
            }
            """, extension: ".json");
        string dbml = TempFile(".dbml");
        string back = TempFile(".json");

        DBSchema.ConvertSchema(json, dbml, DBSchemaFormat.Dbml);
        DBSchema.ConvertSchema(dbml, back, DBSchemaFormat.Json);

        DBSchema.DetectFormat(dbml).ShouldBe(DBSchemaFormat.Dbml);
        var original = DBSchemaJson.Read(json);
        SchemaAssert.SameStructure(DBSchemaDbml.Read(dbml), original);
        var roundTripped = DBSchemaJson.Read(back);
        SchemaAssert.SameStructure(roundTripped, original);
        roundTripped.Tables["Customers"].DefaultRecords.ShouldHaveSingleItem().Values
            .ShouldBe(new Dictionary<string, object> { ["Id"] = 1, ["Name"] = "Walk-in" });
    }

    [Fact]
    public void Xml_writer_skips_null_default_values()
    {
        var schema = new DBSchema();
        schema.AddTable("Settings").AddColumn("Id", "int", "NOT NULL");
        var defaults = new DBDefaultRecords();
        var record = defaults.AddRecord("Settings");
        record.AddValue("Id", 1);
        record.AddValue("Missing", null!);
        record.AddValue("AlsoMissing", DBNull.Value);
        string xml = TempFile(".xml");

        DBSchemaXml.Write(schema, xml, defaults);

        DBSchemaXml.Read(xml).Tables["Settings"].DefaultRecords.ShouldHaveSingleItem().Values.Keys.ShouldBe(new[] { "Id" });
    }
}
