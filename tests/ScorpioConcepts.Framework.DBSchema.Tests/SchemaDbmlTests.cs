namespace ScorpioConcepts.Framework.DBSchema.Tests;

/// <summary>
/// Tests for reading and writing the DBML schema format, without a database.
/// </summary>
public sealed class SchemaDbmlTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"dbschema-{Guid.NewGuid():N}.dbml");

    public void Dispose() => File.Delete(_file);

    private DBSchema Read(string dbml)
    {
        File.WriteAllText(_file, dbml);
        return DBSchemaDbml.Read(_file);
    }

    private string Write(DBSchema schema, DBDefaultRecords? defaults = null)
    {
        DBSchemaDbml.Write(schema, _file, defaults ?? new DBDefaultRecords());
        return File.ReadAllText(_file);
    }

    private static string[] Columns(DBTable table) => table.Columns.Select(c => $"{c.Name} | {c.DataType} | {c.Options}").ToArray();

    private static string[] Indexes(DBTable table) =>
        table.Indexes.Select(i => $"{i.Name}{(i.Unique ? " UNIQUE" : "")}{(i.Clustered ? " CLUSTERED" : "")}: "
            + string.Join(",", i.Columns.Select(c => c.Key + (c.Value ? " DESC" : "")))).ToArray();

    private static string[] ForeignKeys(DBTable table) =>
        table.ForeignKeys.Select(f => $"{f.Name}: {f.Column} -> {f.PKeyTable}.{f.PKeyColumn}").ToArray();

    private static DBSchema ShopSchema()
    {
        var schema = new DBSchema();
        var customers = schema.AddTable("Customers");
        customers.AddColumn("Id", "int identity", "NOT NULL");
        customers.AddColumn("Name", "varchar(50)", "NOT NULL DEFAULT ('')");
        customers.AddColumn("Email", "nvarchar(100)", "NULL");
        customers.AddColumn("Balance", "decimal(10,2)", "NOT NULL DEFAULT ((0))");
        customers.AddColumn("Joined", "datetime2(3)", "NOT NULL DEFAULT (getdate())");
        customers.PrimaryKey.Name = "PK_Customers";
        customers.PrimaryKey.AddKeyColumn("Id");
        var index = customers.AddIndex("IX_Customers_Name_Email", clustered: false, unique: true);
        index.AddIndexColumn("Name", descending: false);
        index.AddIndexColumn("Email", descending: true);

        var orders = schema.AddTable("Orders");
        orders.AddColumn("Id", "int", "NOT NULL");
        orders.AddColumn("Line", "int", "NOT NULL");
        orders.AddColumn("CustomerId", "int", "NOT NULL");
        orders.PrimaryKey.Name = "PK_Orders_Custom";
        orders.PrimaryKey.AddKeyColumn("Id");
        orders.PrimaryKey.AddKeyColumn("Line");
        orders.AddIndex("CIX_Orders_CustomerId", clustered: true, unique: false).AddIndexColumn("CustomerId", descending: false);
        orders.AddForeignKey("FK_Orders_Customers", "CustomerId", "Customers", "Id");

        var lines = schema.AddTable("OrderNotes");
        lines.AddColumn("OrderId", "int", "NOT NULL");
        lines.AddColumn("OrderLine", "int", "NOT NULL");
        lines.AddForeignKey("FK_OrderNotes_Orders", "OrderId", "Orders", "Id");
        lines.AddForeignKey("FK_OrderNotes_Orders", "OrderLine", "Orders", "Line");
        return schema;
    }

    [Fact]
    public void Writes_idiomatic_dbml()
    {
        Write(ShopSchema()).ReplaceLineEndings("\n").ShouldBe("""
            Table Customers {
              Id int [pk, increment, not null]
              Name varchar(50) [not null, default: '']
              Email nvarchar(100)
              Balance decimal(10,2) [not null, default: 0]
              Joined datetime2(3) [not null, default: `getdate()`]

              indexes {
                (Name, Email [desc]) [name: 'IX_Customers_Name_Email', unique]
              }
            }

            Table Orders [clustered_index: 'CIX_Orders_CustomerId'] {
              Id int [not null]
              Line int [not null]
              CustomerId int [not null]

              indexes {
                (Id, Line) [pk, name: 'PK_Orders_Custom']
                (CustomerId) [name: 'CIX_Orders_CustomerId']
              }
            }

            Table OrderNotes {
              OrderId int [not null]
              OrderLine int [not null]
            }

            Ref FK_Orders_Customers: Orders.CustomerId > Customers.Id
            Ref FK_OrderNotes_Orders: OrderNotes.(OrderId, OrderLine) > Orders.(Id, Line)

            """);
    }

    [Fact]
    public void Written_schema_reads_back_identically()
    {
        var schema = ShopSchema();

        Write(schema);

        SchemaAssert.SameStructure(DBSchemaDbml.Read(_file), schema);
    }

    [Fact]
    public void Reads_hand_written_schema()
    {
        var schema = Read("""
            // Comments are allowed anywhere
            Project shop {
              database_type: 'SQL Server'
              Note: 'Ignored, it only describes the project'
            }

            Table dbo.Customers as C [headerColor: #3498DB, note: 'customers'] {
              Id int [pk, increment]
              Email "nvarchar(100)" [unique, note: 'login']
              Name varchar(50) [not null, default: 'none', owner: "sales"]
              Note: '''
                Table notes
                are ignored
              '''
            }

            /* Orders get placed
               by customers */
            Table Orders [clustered_index: 'IX_ByCustomer'] {
              Id int [primary key]
              CustomerId int [ref: > C.Id]
              PlacedOn datetime2(3) [not null, default: `getdate()`]

              indexes {
                PlacedOn
                (CustomerId, PlacedOn [desc]) [name: 'IX_ByCustomer', unique, type: btree]
              }
            }

            TableGroup shop {
              Customers
              Orders
            }

            Ref: Orders.Id < Shipments.OrderId [delete: no action, color: #79AD51]
            Ref ShipsTo {
              Shipments.CustomerId > public.Customers.Id
            }

            Table Shipments {
              OrderId int
              CustomerId int
            }
            """);

        schema.Tables.Keys.ShouldBe(new[] { "Customers", "Orders", "Shipments" });

        var customers = schema.Tables["Customers"];
        Columns(customers).ShouldBe(new[]
        {
            "Id | int identity | NOT NULL",
            "Email | nvarchar(100) | NULL",
            "Name | varchar(50) | NOT NULL DEFAULT ('none')",
        });
        customers.PrimaryKey.Name.ShouldBe("PK_Customers");
        customers.PrimaryKey.KeyColumns.ShouldBe(new[] { "Id" });
        Indexes(customers).ShouldBe(new[] { "IX_Customers_Email UNIQUE: Email" });

        var orders = schema.Tables["Orders"];
        Columns(orders).ShouldBe(new[]
        {
            "Id | int | NOT NULL",
            "CustomerId | int | NULL",
            "PlacedOn | datetime2(3) | NOT NULL DEFAULT (getdate())",
        });
        Indexes(orders).ShouldBe(new[]
        {
            "IX_Orders_PlacedOn: PlacedOn",
            "IX_ByCustomer UNIQUE CLUSTERED: CustomerId,PlacedOn DESC",
        });
        ForeignKeys(orders).ShouldBe(new[] { "FK_Orders_CustomerId: CustomerId -> Customers.Id" });

        ForeignKeys(schema.Tables["Shipments"]).ShouldBe(new[]
        {
            "FK_Shipments_OrderId: OrderId -> Orders.Id",
            "ShipsTo: CustomerId -> Customers.Id",
        });
    }

    [Fact]
    public void Keywords_and_names_are_not_case_sensitive_where_dbml_says_so()
    {
        var table = Read("""
            TABLE A {
              Id INT [PK, NOT NULL]
            }
            """).Tables["A"];

        Columns(table).ShouldBe(new[] { "Id | INT | NOT NULL" });
        table.PrimaryKey.KeyColumns.ShouldBe(new[] { "Id" });
    }

    [Fact]
    public void Composite_keys_and_foreign_keys()
    {
        var schema = Read("""
            Table Periods {
              MerchantId int
              Country char(2)
              indexes {
                (MerchantId, Country) [pk]
              }
            }
            Table Sales {
              Id int [pk]
              MerchantId int
              Country char(2)
            }
            Ref: Sales.(MerchantId, Country) > Periods.(MerchantId, Country)
            """);

        var periods = schema.Tables["Periods"];
        periods.PrimaryKey.Name.ShouldBe("PK_Periods");
        periods.PrimaryKey.KeyColumns.ShouldBe(new[] { "MerchantId", "Country" });
        Columns(periods).ShouldAllBe(c => c.EndsWith("| NOT NULL"), "primary key columns cannot be NULL");
        ForeignKeys(schema.Tables["Sales"]).ShouldBe(new[]
        {
            "FK_Sales_MerchantId_Country: MerchantId -> Periods.MerchantId",
            "FK_Sales_MerchantId_Country: Country -> Periods.Country",
        });
    }

    [Theory]
    [InlineData("Ref: B.AId > A.Id", "B", "AId -> A.Id")]
    [InlineData("Ref: A.Id < B.AId", "B", "AId -> A.Id")]
    [InlineData("Ref: A.Id - B.AId", "B", "AId -> A.Id")]
    [InlineData("Ref: B.AId >? A.Id", "B", "AId -> A.Id")]
    [InlineData("Ref: A.Id ?< B.AId", "B", "AId -> A.Id")]
    [InlineData("Ref: B.AId > A.Id [inactive]", "B", "AId -> A.Id")]
    public void Relationship_direction_decides_which_table_holds_the_foreign_key(string reference, string table, string expected)
    {
        var schema = Read($$"""
            Table A {
              Id int [pk]
            }
            Table B {
              AId int
            }
            {{reference}}
            """);

        var fkey = schema.Tables[table].ForeignKeys.ShouldHaveSingleItem();
        $"{fkey.Column} -> {fkey.PKeyTable}.{fkey.PKeyColumn}".ShouldBe(expected);
        schema.Tables["A"].ForeignKeys.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("[ref: > A.Id]", "B")]
    [InlineData("[ref: - A.Id]", "B")]
    public void Inline_ref_puts_the_foreign_key_on_the_column_with_the_ref(string settings, string table)
    {
        var schema = Read($$"""
            Table A {
              Id int [pk]
            }
            Table B {
              AId int {{settings}}
            }
            """);

        ForeignKeys(schema.Tables[table]).ShouldBe(new[] { "FK_B_AId: AId -> A.Id" });
    }

    [Fact]
    public void Inline_ref_pointing_back_puts_the_foreign_key_on_the_other_table()
    {
        var schema = Read("""
            Table A {
              Id int [pk, ref: < B.AId]
            }
            Table B {
              AId int
            }
            """);

        ForeignKeys(schema.Tables["B"]).ShouldBe(new[] { "FK_B_AId: AId -> A.Id" });
    }

    [Fact]
    public void Foreign_key_can_reference_a_table_that_is_not_in_the_file()
    {
        var schema = Read("""
            Table Orders {
              CustomerId int [ref: > Customers.Id]
            }
            """);

        ForeignKeys(schema.Tables["Orders"]).ShouldBe(new[] { "FK_Orders_CustomerId: CustomerId -> Customers.Id" });
    }

    [Theory]
    [InlineData("0", "NOT NULL DEFAULT ((0))")]
    [InlineData("-1", "NOT NULL DEFAULT ((-1))")]
    [InlineData("12.5", "NOT NULL DEFAULT ((12.5))")]
    [InlineData("''", "NOT NULL DEFAULT ('')")]
    [InlineData("'it\\'s'", "NOT NULL DEFAULT ('it''s')")]
    [InlineData("true", "NOT NULL DEFAULT ((1))")]
    [InlineData("false", "NOT NULL DEFAULT ((0))")]
    [InlineData("null", "NOT NULL DEFAULT (NULL)")]
    [InlineData("`getdate()`", "NOT NULL DEFAULT (getdate())")]
    [InlineData("`N'naïve'`", "NOT NULL DEFAULT (N'naïve')")]
    public void Defaults_get_the_parentheses_sql_server_reports(string dbml, string expected)
    {
        var table = Read($$"""
            Table A {
              Value int [not null, default: {{dbml}}]
            }
            """).Tables["A"];

        table.Columns.ShouldHaveSingleItem().Options.ShouldBe(expected);
    }

    [Theory]
    [InlineData("NOT NULL DEFAULT ((0))", "[not null, default: 0]")]
    [InlineData("NOT NULL DEFAULT ((-1))", "[not null, default: -1]")]
    [InlineData("NULL DEFAULT ((1.5))", "[default: 1.5]")]
    [InlineData("NOT NULL DEFAULT ('')", "[not null, default: '']")]
    [InlineData("NOT NULL DEFAULT ('it''s')", "[not null, default: 'it\\'s']")]
    [InlineData("NOT NULL DEFAULT (getdate())", "[not null, default: `getdate()`]")]
    [InlineData("NOT NULL DEFAULT (N'abc')", "[not null, default: `N'abc'`]")]
    [InlineData("NOT NULL DEFAULT ((1)+(2))", "[not null, default: `(1)+(2)`]")]
    [InlineData("NULL DEFAULT (NULL)", "[default: null]")]
    [InlineData("NOT NULL", "[not null]")]
    public void Defaults_from_the_database_are_written_as_plain_values_and_read_back_unchanged(string options, string settings)
    {
        var schema = new DBSchema();
        schema.AddTable("A").AddColumn("Value", "int", options);

        Write(schema).ShouldContain("  Value int " + settings + Environment.NewLine);
        DBSchemaDbml.Read(_file).Tables["A"].Columns.ShouldHaveSingleItem().Options.ShouldBe(options);
    }

    [Theory]
    [InlineData("DEFAULT 0 NOT NULL", "NOT NULL DEFAULT ((0))")]
    [InlineData("not null default 'x'", "NOT NULL DEFAULT ('x')")]
    [InlineData("DEFAULT NULL", "NULL DEFAULT (NULL)")]
    [InlineData("", "NULL")]
    public void Hand_written_options_are_written_in_the_usual_form(string options, string readBack)
    {
        var schema = new DBSchema();
        schema.AddTable("A").AddColumn("Value", "int", options);

        Write(schema);

        DBSchemaDbml.Read(_file).Tables["A"].Columns.ShouldHaveSingleItem().Options.ShouldBe(readBack);
    }

    [Fact]
    public void Options_dbml_cannot_express_are_refused_when_writing()
    {
        var schema = new DBSchema();
        schema.AddTable("A").AddColumn("Name", "varchar(10)", "COLLATE Latin1_General_CI_AS NULL");

        Should.Throw<NotSupportedException>(() => Write(schema)).Message.ShouldContain("column 'Name' in table 'A'");
    }

    [Fact]
    public void Names_and_types_that_need_quotes_are_quoted()
    {
        var schema = new DBSchema();
        var table = schema.AddTable("Order Lines");
        table.AddColumn("Note", "varchar(10)", "NULL");
        table.AddColumn("indexes", "int", "NULL");
        table.AddColumn("Say \"hi\"", "decimal(10, 2)", "NULL");
        table.AddColumn("2fa", "double precision", "NULL");
        table.PrimaryKey.Name = "PK_Order Lines";
        table.PrimaryKey.AddKeyColumn("indexes");

        string dbml = Write(schema);

        dbml.ShouldContain("Table \"Order Lines\" {");
        dbml.ShouldContain("  \"Note\" varchar(10)");
        dbml.ShouldContain("  \"indexes\" int [pk, null]");
        dbml.ShouldContain("  \"Say \\\"hi\\\"\" \"decimal(10, 2)\"");
        dbml.ShouldContain("  \"2fa\" \"double precision\"");
        SchemaAssert.SameStructure(DBSchemaDbml.Read(_file), schema);
    }

    [Fact]
    public void Columns_named_like_keywords_are_still_columns()
    {
        var table = Read("""
            Table A {
              note varchar(10)
              indexes int
              records int
              Note: 'this one is a note'
            }
            """).Tables["A"];

        table.Columns.Select(c => c.Name).ShouldBe(new[] { "note", "indexes", "records" });
    }

    [Fact]
    public void Default_records_inside_and_outside_tables()
    {
        var schema = Read("""
            Table Settings {
              Id int [pk]
              Name nvarchar(50)
              Enabled bit
              Limit decimal(10,2)

              records {
                1, 'Theme', true, 12.5
                2, 'Beta', false, null
                3, 'Empty is null', true,
                5, 'Default limit', `DEFAULT`, ` default `
                -4, '''
                  Multi-line
                    text
                  ''', 0, 5000000000
              }
            }

            records Other(Id, Name) {
              1, 'It\'s here'
            }

            records Other(Id) [example] {
              99
            }

            Table Other {
              Id int
              Name varchar(20)
            }
            """);

        schema.Tables["Settings"].DefaultRecords.Select(r => r.Values).ShouldBe(new[]
        {
            new Dictionary<string, object> { ["Id"] = 1, ["Name"] = "Theme", ["Enabled"] = true, ["Limit"] = 12.5m },
            new Dictionary<string, object> { ["Id"] = 2, ["Name"] = "Beta", ["Enabled"] = false, ["Limit"] = DBNull.Value },
            new Dictionary<string, object> { ["Id"] = 3, ["Name"] = "Empty is null", ["Enabled"] = true, ["Limit"] = DBNull.Value },
            new Dictionary<string, object> { ["Id"] = 5, ["Name"] = "Default limit" },
            new Dictionary<string, object> { ["Id"] = -4, ["Name"] = "Multi-line\n  text\n", ["Enabled"] = 0, ["Limit"] = 5_000_000_000L },
        });
        schema.Tables["Other"].DefaultRecords.ShouldHaveSingleItem().Values
            .ShouldBe(new Dictionary<string, object> { ["Id"] = 1, ["Name"] = "It's here" });
    }

    [Fact]
    public void Default_record_values_keep_their_types()
    {
        var schema = new DBSchema();
        var settings = schema.AddTable("Settings");
        foreach (var column in new[] { "Int", "Long", "Decimal", "Double", "Bool", "Text", "Null", "DBNull", "Date", "Guid" })
        {
            settings.AddColumn(column, "sql_variant", "NULL");
        }
        var defaults = new DBDefaultRecords();
        var record = defaults.AddRecord("Settings");
        record.AddValue("Int", 42);
        record.AddValue("Long", 5_000_000_000L);
        record.AddValue("Decimal", 12.50m);
        record.AddValue("Double", 0.1);
        record.AddValue("Bool", true);
        record.AddValue("Text", "It's a \"line\"\nand\\more");
        record.AddValue("Null", null!);
        record.AddValue("DBNull", DBNull.Value);
        record.AddValue("Date", new DateTime(2026, 11, 15, 8, 30, 0));
        record.AddValue("Guid", Guid.Parse("8184a1a7-2536-4da3-8408-5624ab412f4c"));
        defaults.AddRecord("Settings").AddValue("Int", 7);
        defaults.AddRecord("NotInSchema").AddValue("Id", 1);

        string dbml = Write(schema, defaults);
        var read = DBSchemaDbml.Read(_file);

        dbml.ShouldContain("    7, `DEFAULT`, `DEFAULT`,");
        read.Tables.Keys.ShouldBe(new[] { "Settings" });
        read.Tables["Settings"].DefaultRecords.Select(r => r.Values).ShouldBe(new[]
        {
            new Dictionary<string, object>
            {
                ["Int"] = 42,
                ["Long"] = 5_000_000_000L,
                ["Decimal"] = 12.50m,
                ["Double"] = 0.1m,
                ["Bool"] = true,
                ["Text"] = "It's a \"line\"\nand\\more",
                ["Null"] = DBNull.Value,
                ["DBNull"] = DBNull.Value,
                ["Date"] = "2026-11-15T08:30:00",
                ["Guid"] = "8184a1a7-2536-4da3-8408-5624ab412f4c",
            },
            // The second record only has Int; its other fields are written as `DEFAULT`, so they stay out of the record
            new Dictionary<string, object> { ["Int"] = 7 },
        });
    }

    [Fact]
    public void Clustered_index_can_name_the_primary_key_or_a_generated_index_name()
    {
        var schema = Read("""
            Table A [clustered_index: 'PK_A'] {
              Id int [pk]
            }
            Table B [clustered_index: 'IX_B_Code'] {
              Code int
              indexes {
                Code
              }
            }
            """);

        schema.Tables["A"].Indexes.ShouldBeEmpty();
        Indexes(schema.Tables["B"]).ShouldBe(new[] { "IX_B_Code CLUSTERED: Code" });
    }

    [Fact]
    public void More_than_one_clustered_index_is_refused_when_writing()
    {
        var schema = new DBSchema();
        var table = schema.AddTable("A");
        table.AddColumn("Id", "int", "NULL");
        table.AddIndex("CIX_1", clustered: true, unique: false).AddIndexColumn("Id", descending: false);
        table.AddIndex("CIX_2", clustered: true, unique: false).AddIndexColumn("Id", descending: true);

        Should.Throw<NotSupportedException>(() => Write(schema)).Message.ShouldContain("more than one clustered index");
    }

    [Fact]
    public void Front_matter_is_ignored()
    {
        Read("""
            ---
            title: Shop
            ---
            Table A {
              Id int
            }
            """).Tables.Keys.ShouldBe(new[] { "A" });
    }

    [Theory]
    [InlineData("enum status {\n  active\n}", "line 1, column 1: Enums are not supported")]
    [InlineData("TablePartial base {\n  Id int\n}", "TablePartial is not supported")]
    [InlineData("use * from './base'", "Importing other DBML files is not supported")]
    [InlineData("Table A {\n  ~base\n}", "line 2, column 3: TablePartial is not supported")]
    [InlineData("Table A {\n  Id int\n  checks {\n    `Id > 0`\n  }\n}", "line 3, column 3: Check constraints are not supported")]
    [InlineData("Table A {\n  Id int [check: `Id > 0`]\n}", "Check constraints are not supported, but column 'Id' of table 'A' has one")]
    [InlineData("Table sales.A {\n  Id int\n}", "'sales.A' is in another schema")]
    [InlineData("Table A {\n  Id int\n}\nTable B {\n  Id int\n}\nRef: A.Id <> B.Id", "line 7, column 6: Many-to-many relationships (<>) are not supported")]
    [InlineData("Table A {\n  Id int\n}\nTable B {\n  AId int [ref: > A.Id]\n}\nRef: B.AId > A.Id [delete: cascade]", "ON DELETE CASCADE is not supported")]
    [InlineData("Table A {\n  Id int\n  indexes {\n    (`Id * 2`)\n  }\n}", "Indexes on expressions are not supported")]
    [InlineData("Table A {\n  Id int\n  indexes {\n    Id [type: hash]\n  }\n}", "Index type 'hash' is not supported")]
    [InlineData("Table A {\n  Id int\n  records {\n    `1 + 1`\n  }\n}", "Expressions such as `1 + 1` cannot be inserted")]
    [InlineData("Table A {\n  Id int\n  records {\n    1, 2\n  }\n}", "expected 1 values, found 2")]
    [InlineData("records A(Id) {\n  1\n}", "The records are for table 'A', which is not defined")]
    [InlineData("records A {\n  1\n}\nTable A {\n  Id int\n}", "Records outside a table must list their columns")]
    [InlineData("Table A {\n  Id int [pk]\n  Code int [pk]\n}", "use indexes { (Id, Code) [pk] } for a composite key")]
    [InlineData("Table A {\n  Id int [pk]\n  indexes {\n    Id [pk]\n  }\n}", "Table 'A' has more than one primary key")]
    [InlineData("Table A {\n  Id int\n  indexes {\n    (Id [desc]) [pk]\n  }\n}", "A primary key cannot have a descending column")]
    [InlineData("Table A {\n  Id int\n  indexes {\n    Missing\n  }\n}", "uses column 'Missing', which the table does not have")]
    [InlineData("Table A {\n  Id int\n}\nTable B {\n  AId int [ref: > A.Nope]\n}", "Table 'A' has no column called 'Nope'")]
    [InlineData("Table A {\n  Id int\n}\nRef: Missing.AId > A.Id", "The foreign key is on table 'Missing', which is not defined")]
    [InlineData("Table A {\n  Id int\n  Id int\n}", "more than one column called 'Id'")]
    [InlineData("Table A {\n  Id int\n}\nTable A {\n  Id int\n}", "line 4, column 7: Table 'A' is defined more than once")]
    [InlineData("Table A {\n  Id\n}", "line 2, column 5: Column 'Id' needs a type, found end of line")]
    [InlineData("Table A {\n  Id int [not null\n}", "Expected ',' or ']' in the settings, found '}'")]
    [InlineData("Table A {\n  Id int [not null, : 1]\n}", "line 2, column 21: Expected a setting name, found ':'")]
    [InlineData("Table A {\n  Id int\n", "Table 'A' is never closed")]
    [InlineData("Table A {\n  Name varchar [default: 'x\n}", "line 2, column 26: The text that starts here is never closed")]
    [InlineData("Table A {\n  Id int [default: Status.active]\n}", "The default of column 'Id' of table 'A' must be")]
    [InlineData("Server api {\n}", "'Server' is not supported")]
    [InlineData("Table A {\n  Id int\n  indexes {\n    Id [clustered: true]\n  }\n}", "DBML does not allow 'clustered' on an index; name the index in the table settings instead, as in Table A [clustered_index: 'IX_name']")]
    [InlineData("Table A [clustered_index: 'IX_Missing'] {\n  Id int\n}", "Table 'A' has clustered_index 'IX_Missing', but no index with that name")]
    [InlineData("Table A {\n  Id int @\n}", "line 2, column 10: Unexpected character '@'")]
    public void Invalid_or_unsupported_dbml_explains_what_is_wrong(string dbml, string expected)
    {
        Should.Throw<FormatException>(() => Read(dbml)).Message.ShouldContain(expected);
    }

    [Fact]
    public void Write_records_progress_in_DebugLog()
    {
        Write(ShopSchema());

        DBSchema.DebugLog.ShouldContain("DB Schema DBML generated for table 'Orders'");
    }
}
