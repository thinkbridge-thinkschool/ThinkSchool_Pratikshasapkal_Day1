using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Quotes.Tests.Integration;

/// <summary>
/// xUnit collection fixture: starts one SQL Server 2022 container before any tests run
/// and stops it after all tests finish. Shared across all test classes in [Collection("SqlServer")].
/// Docker is required locally.
/// </summary>
[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerContainerFixture> { }

public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    /// <summary>
    /// Returns a connection string targeting a specific database on the running container.
    /// The database does not need to exist yet — EF's Migrate() will create it.
    /// </summary>
    public string GetConnectionString(string databaseName)
    {
        var csb = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = databaseName
        };
        return csb.ConnectionString;
    }

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
