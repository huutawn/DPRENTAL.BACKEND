using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Api.Tests.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.RentalService.Api.Tests.TestHelpers;

public static class PostgresTestDatabaseHelper
{
    public static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRESQL_TEST_CONNECTION_STRING")
        ?? Environment.GetEnvironmentVariable("RENTAL_TEST_POSTGRES_CONNECTION")
        ?? "Host=127.0.0.1;Port=5433;Database=postgres;Username=inventoryservice;Password=inventory_test_secret_123!;Include Error Detail=true";

    public static async Task<bool> IsPostgreSqlAvailableAsync(string? baseConnectionString = null)
    {
        try
        {
            await using var conn = new NpgsqlConnection(baseConnectionString ?? BaseConnectionString);
            await conn.OpenAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string BuildDatabaseConnectionString(string baseConnString, string dbName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseConnString);
        ArgumentException.ThrowIfNullOrWhiteSpace(dbName);

        var builder = new NpgsqlConnectionStringBuilder(baseConnString)
        {
            Database = dbName
        };
        return builder.ConnectionString;
    }

    public static async Task<(string ConnectionString, string DatabaseName)> CreateTestDatabaseAsync(string? baseConnectionString = null)
    {
        string baseConn = baseConnectionString ?? BaseConnectionString;
        string dbName = $"rental_stats_{Guid.NewGuid():N}";

        await using var conn = new NpgsqlConnection(baseConn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // Database belongs to the authenticated database user without hardcoding an OWNER role
        cmd.CommandText = $"CREATE DATABASE \"{dbName}\";";
        await cmd.ExecuteNonQueryAsync();

        string connString = BuildDatabaseConnectionString(baseConn, dbName);
        return (connString, dbName);
    }

    public static async Task DropTestDatabaseAsync(string dbName, string? baseConnectionString = null)
    {
        string baseConn = baseConnectionString ?? BaseConnectionString;
        await using var conn = new NpgsqlConnection(baseConn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE);";
        await cmd.ExecuteNonQueryAsync();
    }

    public static DbContextOptions<RentalServiceDbContext> CreateOptions(string connString)
    {
        return new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseNpgsql(connString)
            .Options;
    }

    public static RentalOrder CreateOrderWithItem(
        Guid orderId,
        Guid itemId,
        Guid supplierId,
        int quantity,
        DateTimeOffset createdAt)
    {
        var assignments = Enumerable.Range(0, quantity)
            .Select(_ => new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))
            .ToList();

        var itemDto = new InitialRentalItemDto(
            itemId,
            Guid.NewGuid(),
            supplierId,
            quantity,
            new DateOnly(2026, 10, 10),
            new DateOnly(2026, 10, 15),
            "Camera Equipment",
            "Professional Camera Kit",
            "District 1, HCMC",
            200_000m,
            500_000m,
            $"group-{itemId:N}",
            assignments,
            TestListingSnapshots.Default);

        return RentalOrder.Create(
            orderId,
            $"ORD-{orderId.ToString("N")[..8]}",
            Guid.NewGuid(),
            "VND",
            "Pickup",
            "Customer A",
            "0909123456",
            "123 Nguyen Hue",
            "Call before delivery",
            "bank-ref-001",
            "v1.0",
            "terms-doc-001",
            "sha256-hash-001",
            $"op-create-{orderId:N}",
            createdAt,
            [itemDto]).Value;
    }
}
