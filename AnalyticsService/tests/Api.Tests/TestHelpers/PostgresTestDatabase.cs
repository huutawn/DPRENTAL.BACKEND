using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.AnalyticsService.Api.Tests.TestHelpers;

/// <summary>
/// Tạo database PostgreSQL tạm (analytics_tests_xxx), chạy migration thật, xóa khi dispose.
/// Kết nối tới server lấy từ biến môi trường ANALYTICS_TEST_POSTGRES (user cần quyền CREATEDB).
/// Không cấu hình biến này thì <see cref="CreateAsync"/> trả null và test tự bỏ qua (giống RentalService);
/// đã cấu hình mà không kết nối được thì báo lỗi, không âm thầm pass.
/// </summary>
internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    public const string ConnectionStringVariable = "ANALYTICS_TEST_POSTGRES";

    private readonly string _serverConnectionString;
    private readonly string _databaseName;

    private PostgresTestDatabase(string serverConnectionString, string databaseName)
    {
        _serverConnectionString = serverConnectionString;
        _databaseName = databaseName;
        string connectionString = new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = databaseName }.ConnectionString;
        Options = new DbContextOptionsBuilder<AnalyticsServiceDbContext>().UseNpgsql(connectionString).Options;
    }

    public DbContextOptions<AnalyticsServiceDbContext> Options { get; }

    public static async Task<PostgresTestDatabase?> CreateAsync()
    {
        string? serverConnectionString = System.Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(serverConnectionString))
        {
            return null;
        }

        string databaseName = $"analytics_tests_{Guid.NewGuid():N}";
        await using (NpgsqlConnection connection = new(serverConnectionString))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand create = new($"CREATE DATABASE \"{databaseName}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        PostgresTestDatabase database = new(serverConnectionString, databaseName);
        await using AnalyticsServiceDbContext context = database.CreateContext();
        await context.Database.MigrateAsync(); // Chạy đúng các migration sẽ chạy trên môi trường thật
        return database;
    }

    public AnalyticsServiceDbContext CreateContext() => new(Options);

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using NpgsqlConnection connection = new(_serverConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand drop = new($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }
}
