using System.Data;
using Microsoft.Data.SqlClient;

namespace ScorpioConcepts.Framework.DBSchema.IntegrationTests;

/// <summary>
/// The instance API (#19): each manager owns its connection and logger, so managers for different databases
/// work side by side.
/// </summary>
public class DBSchemaManagerTests(SqlServerFixture fixture) : DatabaseTestBase(fixture)
{
    private const string OneTable = """
        { "tables": [ { "name": "Things", "columns": [ { "name": "Id", "dataType": "int", "options": "NOT NULL" } ],
                        "primaryKey": { "name": "PK_Things", "columns": [ "Id" ] } } ] }
        """;

    [Fact]
    public async Task Managers_for_different_databases_do_not_interfere()
    {
        await using var other = await CreateOtherDatabaseAsync();
        var otherLog = new TestLogger();
        var otherManager = new DBSchemaManager(other, otherLog);

        // With the old static connection, the second call would have pointed both at the same database
        Manager.CheckDatabaseSchema(WriteSchema(OneTable));
        otherManager.CheckDatabaseSchema(WriteSchema("""{ "tables": [ { "name": "Others", "columns": [ { "name": "Id", "dataType": "int" } ] } ] }"""));

        Tables().ShouldBe(new[] { "Things" });
        Manager.ReadDatabaseSchema().Tables.Keys.ShouldBe(new[] { "Things" });
        otherManager.ReadDatabaseSchema().Tables.Keys.ShouldBe(new[] { "Others" });
        Log.Messages.ShouldContain("Creating table 'Things'");
        Log.Messages.ShouldNotContain("Creating table 'Others'");
        otherLog.Messages.ShouldContain("Creating table 'Others'");
    }

    [Fact]
    public async Task Managers_can_run_at_the_same_time()
    {
        var connections = new List<SqlConnection>();
        try
        {
            for (int i = 0; i < 4; i++) connections.Add(await CreateOtherDatabaseAsync());
            string schema = WriteSchema(OneTable);

            await Task.WhenAll(connections.Select(c => Task.Run(() => new DBSchemaManager(c).CheckDatabaseSchema(schema))));

            foreach (var connection in connections)
            {
                new DBSchemaManager(connection).ReadDatabaseSchema().Tables["Things"].PrimaryKey.Name.ShouldBe("PK_Things");
            }
        }
        finally
        {
            foreach (var connection in connections) await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_closed_connection_is_opened_for_the_call_and_closed_again()
    {
        using var closed = new SqlConnection(await CreateOtherDatabaseConnectionStringAsync());
        var manager = new DBSchemaManager(closed);

        manager.CheckDatabaseSchema(WriteSchema(OneTable));
        closed.State.ShouldBe(ConnectionState.Closed);

        manager.ReadDatabaseSchema().Tables.Keys.ShouldBe(new[] { "Things" });
        closed.State.ShouldBe(ConnectionState.Closed);
    }

    [Fact]
    public void An_open_connection_is_left_open()
    {
        Manager.CheckDatabaseSchema(WriteSchema(OneTable));

        Connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact]
    public void A_loaded_definition_can_be_checked_directly()
    {
        var definition = DBSchemaDefinition.Load(WriteSchema(OneTable));

        Manager.CheckDatabaseSchema(definition);

        PrimaryKey("Things").ShouldBe("PK_Things: Id");
    }

    [Fact]
    public void ReadDatabaseSchema_reads_the_structure_and_saves_like_GenerateSchema()
    {
        Manager.CheckDatabaseSchema(WriteSchema(OneTable));
        string generated = TempFile();
        string saved = TempFile();

        Manager.GenerateSchema(generated);
        DBSchemaDefinition read = Manager.ReadDatabaseSchema();
        read.Save(saved);

        var table = read.Tables["Things"];
        table.Columns.ShouldHaveSingleItem().Name.ShouldBe("Id");
        table.PrimaryKey.Columns.ShouldBe(new[] { "Id" });
        File.ReadAllText(saved).ShouldBe(File.ReadAllText(generated));
    }

    [Fact]
    public void Logs_the_schema_file_it_writes()
    {
        string file = TempFile();

        Manager.GenerateSchema(file, format: DBSchemaFormat.Xml);

        Log.Messages.ShouldContain("Writing the schema as Xml to '" + file + "'");
    }
}
