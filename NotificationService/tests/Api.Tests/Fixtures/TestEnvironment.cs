using System.Text.RegularExpressions;
using Npgsql;

namespace Dprental.NotificationService.Api.Tests.Fixtures;

public static class TestEnvironment
{
    internal const string DefaultPostgresConnectionString = "Host=127.0.0.1;Port=20992;Username=notificationservice;Password=postgres;Database=";
    internal const string DefaultKafkaBootstrapServers = "127.0.0.1:29092";
    internal const string DefaultRedisConnectionString = "127.0.0.1:26379";

    internal static string ResolvePostgresConnectionString(Func<string, string?> getEnv)
    {
        var conn = getEnv("TEST_POSTGRES_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(conn)) return conn;

        conn = getEnv("NOTIFICATION_DB_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(conn)) return conn;

        return DefaultPostgresConnectionString;
    }

    internal static string ResolveKafkaBootstrapServers(Func<string, string?> getEnv)
    {
        var servers = getEnv("TEST_KAFKA_BOOTSTRAP_SERVERS");
        return string.IsNullOrWhiteSpace(servers) ? DefaultKafkaBootstrapServers : servers;
    }

    internal static string ResolveRedisConnectionString(Func<string, string?> getEnv)
    {
        var conn = getEnv("TEST_REDIS_CONNECTION_STRING");
        return string.IsNullOrWhiteSpace(conn) ? DefaultRedisConnectionString : conn;
    }

    public static string PostgreSqlConnectionString => ResolvePostgresConnectionString(Environment.GetEnvironmentVariable);

    public static string KafkaBootstrapServers => ResolveKafkaBootstrapServers(Environment.GetEnvironmentVariable);

    public static string RedisConnectionString => ResolveRedisConnectionString(Environment.GetEnvironmentVariable);

    public static string GetPostgreSqlConnectionString(string databaseName)
    {
        var raw = PostgreSqlConnectionString;
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(raw)
            {
                Database = databaseName
            };
            return builder.ConnectionString;
        }
        catch
        {
            if (raw.EndsWith("Database=", StringComparison.OrdinalIgnoreCase))
            {
                return $"{raw}{databaseName};";
            }
            return $"{raw.TrimEnd(';')};Database={databaseName};";
        }
    }

    public static async Task EnsurePostgreSqlAvailableAsync(string? connectionString = null, CancellationToken cancellationToken = default)
    {
        var connStr = connectionString ?? GetPostgreSqlConnectionString("postgres");
        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand("SELECT 1;", conn);
            await cmd.ExecuteScalarAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"PostgreSQL is unreachable. Unable to connect to PostgreSQL maintenance database using connection string '{connStr}'. " +
                "Ensure PostgreSQL service is running and accessible (e.g. Host=127.0.0.1;Port=20992). " +
                $"Diagnostic: {ex.Message}", ex);
        }
    }

    public static async Task RecreateDatabaseAsync(string dbName, CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(dbName, "^[a-zA-Z0-9_]+$"))
        {
            throw new ArgumentException($"Invalid database name: {dbName}", nameof(dbName));
        }

        await EnsurePostgreSqlAvailableAsync(cancellationToken: cancellationToken);

        NpgsqlConnection.ClearAllPools();

        var maintenanceConnStr = GetPostgreSqlConnectionString("postgres");
        await using var conn = new NpgsqlConnection(maintenanceConnStr);
        await conn.OpenAsync(cancellationToken);

        var terminateSql = $@"
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = '{dbName}' AND pid <> pg_backend_pid();";
        await using (var termCmd = new NpgsqlCommand(terminateSql, conn))
        {
            await termCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var dropSql = $"DROP DATABASE IF EXISTS \"{dbName}\";";
        await using (var dropCmd = new NpgsqlCommand(dropSql, conn))
        {
            await dropCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var builder = new NpgsqlConnectionStringBuilder(maintenanceConnStr);
        var owner = string.IsNullOrWhiteSpace(builder.Username) ? "notificationservice" : builder.Username;
        var createSql = $"CREATE DATABASE \"{dbName}\" OWNER \"{owner}\";";
        await using (var createCmd = new NpgsqlCommand(createSql, conn))
        {
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public static async Task DropDatabaseAsync(string dbName, CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(dbName, "^[a-zA-Z0-9_]+$"))
        {
            return;
        }

        try
        {
            NpgsqlConnection.ClearAllPools();

            var maintenanceConnStr = GetPostgreSqlConnectionString("postgres");
            await using var conn = new NpgsqlConnection(maintenanceConnStr);
            await conn.OpenAsync(cancellationToken);

            var terminateSql = $@"
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = '{dbName}' AND pid <> pg_backend_pid();";
            await using (var termCmd = new NpgsqlCommand(terminateSql, conn))
            {
                await termCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var dropSql = $"DROP DATABASE IF EXISTS \"{dbName}\";";
            await using (var dropCmd = new NpgsqlCommand(dropSql, conn))
            {
                await dropCmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch
        {
            // Suppress teardown errors so test assertions are preserved
        }
    }
}
