using DotNet.Testcontainers.Builders;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(ScorpioConcepts.Framework.DBSchema.IntegrationTests.SqlServerFixture))]

namespace ScorpioConcepts.Framework.DBSchema.IntegrationTests;

/// <summary>
/// Starts one SQL Server container for the whole test run. If Docker is not available,
/// the tests are skipped instead of failing.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string Image = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    private MsSqlContainer? _container;

    public string? UnavailableReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new MsSqlBuilder(Image).Build();
            await _container.StartAsync();
        }
        catch (DockerUnavailableException ex)
        {
            UnavailableReason = "Docker is not available: " + ex.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>Create an empty database for one test and return an open connection to it.</summary>
    public async Task<SqlConnection> CreateDatabaseAsync()
    {
        Assert.SkipWhen(UnavailableReason is not null, UnavailableReason ?? "");

        string name = "dbschema_" + Guid.NewGuid().ToString("N");
        await using (var master = new SqlConnection(_container!.GetConnectionString()))
        {
            await master.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{name}]", master);
            await create.ExecuteNonQueryAsync();
        }

        var builder = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = name };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
