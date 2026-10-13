using Dprental.PaymentService.Domain.Repository;
using Dprental.PaymentService.Domain.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.PaymentService.Api.Tests.TestHelpers;

public static class PostgresTestDatabaseHelper
{
    public static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRESQL_TEST_CONNECTION_STRING")
        ?? Environment.GetEnvironmentVariable("PAYMENT_TEST_POSTGRES_CONNECTION")
        ?? ReadLocalConnectionString();

    private static string ReadLocalConnectionString()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "PaymentService.sln"))) continue;

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
                {
                    value = value[1..^1];
                }
                values[entry[..separator].Trim()] = value;
            }

            string Required(string key) => values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new InvalidOperationException($"Set {key} in the repository .env before running PostgreSQL tests.");

            return new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = int.Parse(Required("POSTGRES_PORT"), System.Globalization.CultureInfo.InvariantCulture),
                Database = "postgres",
                Username = Required("POSTGRES_USER"),
                Password = Required("POSTGRES_PASSWORD")
            }.ConnectionString;
        }

        return "Host=127.0.0.1;Port=26280;Database=postgres;Username=paymentservice;Password=postgres";
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

    public static async Task EnsurePostgreSqlAvailableAsync(string? baseConnectionString = null)
    {
        if (!await IsPostgreSqlAvailableAsync(baseConnectionString))
        {
            throw new InvalidOperationException(
                "PostgreSQL 17 integration test database is not available. Start `docker compose up -d postgres` or set POSTGRESQL_TEST_CONNECTION_STRING.");
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
        await EnsurePostgreSqlAvailableAsync(baseConn);
        string dbName = $"payment_test_{Guid.NewGuid():N}";

        await using var conn = new NpgsqlConnection(baseConn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
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

    public static DbContextOptions<PaymentServiceDbContext> CreateOptions(string connString)
    {
        return new DbContextOptionsBuilder<PaymentServiceDbContext>()
            .UseNpgsql(connString)
            .Options;
    }
}
