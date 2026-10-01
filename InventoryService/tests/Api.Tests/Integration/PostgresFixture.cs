using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

/// <summary>Creates and removes only a uniquely named database owned by this test run.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string _databaseName = $"inventory_tests_{Guid.NewGuid():N}";
    private string _adminConnectionString = string.Empty;
    public string? TargetMigration { get; set; }
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") ??
            "Host=127.0.0.1;Port=5433;Database=inventoryservice;Username=inventoryservice;Password=inventory_test_secret_123!");
        settings.Database = "postgres";
        _adminConnectionString = settings.ConnectionString;
        settings.Database = _databaseName;
        ConnectionString = settings.ConnectionString;
        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", admin);
        await create.ExecuteNonQueryAsync();
        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>().UseNpgsql(ConnectionString).Options;
            await using var db = new InventoryServiceDbContext(options);
            await db.GetService<IMigrator>().MigrateAsync(TargetMigration);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
        using var pool = new NpgsqlConnection(ConnectionString);
        NpgsqlConnection.ClearPool(pool);
    }
}
