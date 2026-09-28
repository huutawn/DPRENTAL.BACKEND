using System.Diagnostics;
using Npgsql;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

[CollectionDefinition(Name)]
public sealed class PostgreSqlTestGroup : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSql";
}

/// <summary>Khởi động PostgreSQL một lần trước các lớp test dùng chung cổng 5435.</summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private const string ConnectionString = "Host=localhost;Port=5435;Database=authservice_test;Username=postgres;Password=postgres;Include Error Detail=true";

    public async Task InitializeAsync()
    {
        if (await CanConnectAsync())
        {
            return;
        }

        ProcessStartInfo psi = new("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in new[] { "run", "--rm", "-d", "--name", "authservice-pg-test", "-e", "POSTGRES_PASSWORD", "-e", "POSTGRES_DB", "-p", "5435:5432", "postgres:17-alpine" })
        {
            psi.ArgumentList.Add(argument);
        }
        psi.Environment["POSTGRES_PASSWORD"] = "postgres";
        psi.Environment["POSTGRES_DB"] = "authservice_test";

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Không thể khởi động Docker để chạy PostgreSQL integration tests.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            string error = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Không thể khởi động PostgreSQL test container: {error}");
        }

        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (await CanConnectAsync())
            {
                return;
            }
            await Task.Delay(500);
        }

        throw new TimeoutException("PostgreSQL test container chưa sẵn sàng sau 30 giây.");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<bool> CanConnectAsync()
    {
        try
        {
            await using NpgsqlConnection connection = new(ConnectionString);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
            await connection.OpenAsync(timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }
}
