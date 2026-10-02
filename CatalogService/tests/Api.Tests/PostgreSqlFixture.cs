using System.Diagnostics;
using Npgsql;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

[CollectionDefinition(Name)]
public sealed class PostgreSqlTestGroup : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSql";
}

/// <summary>Khởi động PostgreSQL và chuẩn bị database test sạch sẽ trước các lớp test dùng chung cổng 5436.</summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    public const string ConnectionString = "Host=localhost;Port=5436;Database=catalogservice_test;Username=postgres;Password=postgres;Include Error Detail=true";
    private const string MasterConnectionString = "Host=localhost;Port=5436;Database=postgres;Username=postgres;Password=postgres;Include Error Detail=true";

    public async Task InitializeAsync()
    {
        if (!await CanConnectAsync())
        {
            ProcessStartInfo psi = new("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in new[] { "run", "--rm", "-d", "--name", "catalogservice-pg-test", "-e", "POSTGRES_PASSWORD", "-e", "POSTGRES_DB", "-p", "5436:5432", "postgres:17-alpine" })
            {
                psi.ArgumentList.Add(argument);
            }
            psi.Environment["POSTGRES_PASSWORD"] = "postgres";
            psi.Environment["POSTGRES_DB"] = "catalogservice_test";

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Không thể khởi động Docker để chạy PostgreSQL integration tests.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                string error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"Không thể khởi động PostgreSQL test container: {error}");
            }

            bool connected = false;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                if (await CanConnectAsync())
                {
                    connected = true;
                    break;
                }
                await Task.Delay(500);
            }

            if (!connected)
            {
                throw new TimeoutException("PostgreSQL test container chưa sẵn sàng sau 30 giây.");
            }
        }

        // Tạo mới database test sạch mỗi lần chạy test suite, chỉ dùng catalog.__EFMigrationsHistory
        await ResetTestDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<bool> CanConnectAsync()
    {
        try
        {
            await using NpgsqlConnection connection = new(MasterConnectionString);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
            await connection.OpenAsync(timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task ResetTestDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(MasterConnectionString);
        await connection.OpenAsync();

        await using var termCmd = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'catalogservice_test' AND pid <> pg_backend_pid();",
            connection);
        await termCmd.ExecuteNonQueryAsync();

        await using var dropCmd = new NpgsqlCommand(
            "DROP DATABASE IF EXISTS \"catalogservice_test\" WITH (FORCE);",
            connection);
        await dropCmd.ExecuteNonQueryAsync();

        await using var createCmd = new NpgsqlCommand(
            "CREATE DATABASE \"catalogservice_test\";",
            connection);
        await createCmd.ExecuteNonQueryAsync();
    }
}
