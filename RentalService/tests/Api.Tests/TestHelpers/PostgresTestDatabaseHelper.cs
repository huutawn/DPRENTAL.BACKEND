using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Repositories;
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
        ?? ReadLocalConnectionString();

    private static string ReadLocalConnectionString()
    {
        // Resolve from the test assembly so Test Explorer and Git hooks use the same .env.
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "RentalService.sln"))) continue;

            string envPath = Path.Combine(directory.FullName, ".env");
            if (!File.Exists(envPath)) break;

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in File.ReadLines(envPath))
            {
                string entry = line.Trim();
                if (entry.Length == 0 || entry.StartsWith('#')) continue;
                int separator = entry.IndexOf('=');
                if (separator < 1) continue;
                string value = entry[(separator + 1)..].Trim();
                if (value.Length >= 2 &&
                    ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                    value = value[1..^1];
                values[entry[..separator].Trim()] = value;
            }

            string Required(string key) => values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new InvalidOperationException($"Set {key} in the repository .env before running PostgreSQL tests.");

            return new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = int.Parse(Required("POSTGRES_PORT"), System.Globalization.CultureInfo.InvariantCulture),
                // Test fixtures create isolated databases; only use postgres for administration.
                Database = "postgres",
                Username = Required("POSTGRES_USER"),
                Password = Required("POSTGRES_PASSWORD")
            }.ConnectionString;
        }

        throw new InvalidOperationException("Set POSTGRESQL_TEST_CONNECTION_STRING or create the repository .env and start docker compose up -d postgres before running PostgreSQL tests.");
    }

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
