#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dprental.RentalService.Domain.Repositories;

public sealed class PostgresOrderLockManager : IOrderLockManager
{
    private readonly string? _connectionString;
    private readonly ILogger<PostgresOrderLockManager> _logger;

    public PostgresOrderLockManager(
        IConfiguration configuration,
        ILogger<PostgresOrderLockManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionString = configuration.GetConnectionString("Postgres");
    }

    public static long ComputeLockKey(Guid id)
    {
        byte[] bytes = id.ToByteArray();
        long p1 = BitConverter.ToInt64(bytes, 0);
        long p2 = BitConverter.ToInt64(bytes, 8);
        return p1 ^ p2;
    }

    public async Task<IAsyncDisposable> AcquireOrderLockAsync(
        Guid orderId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("Thiếu cấu hình ConnectionStrings:Postgres cho PostgreSQL session advisory lock.");
        }

        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);
        long lockKey = ComputeLockKey(orderId);

        NpgsqlConnection conn = new(_connectionString);
        try
        {
            await conn.OpenAsync(cancellationToken);

            DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(effectiveTimeout);
            bool acquired = false;

            while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT pg_try_advisory_lock(@key);";
                cmd.Parameters.AddWithValue("key", lockKey);

                object? result = await cmd.ExecuteScalarAsync(cancellationToken);
                if (result is bool b && b)
                {
                    acquired = true;
                    break;
                }

                await Task.Delay(50, cancellationToken);
            }

            if (!acquired)
            {
                await conn.DisposeAsync();
                throw new TimeoutException($"Không thể thu thập PostgreSQL advisory lock cho RentalOrder '{orderId}' sau {effectiveTimeout.TotalSeconds}s.");
            }

            return new PostgresLockReleaser(conn, lockKey, _logger);
        }
        catch (Exception ex)
        {
            await conn.DisposeAsync();
            if (ex is not TimeoutException)
            {
                _logger.LogError(ex, "Lỗi kết nối PostgreSQL advisory lock cho Order {OrderId}", orderId);
            }
            throw;
        }
    }

    private sealed class PostgresLockReleaser : IAsyncDisposable
    {
        private readonly NpgsqlConnection _conn;
        private readonly long _lockKey;
        private readonly ILogger _logger;
        private int _disposed;

        public PostgresLockReleaser(NpgsqlConnection conn, long lockKey, ILogger logger)
        {
            _conn = conn;
            _lockKey = lockKey;
            _logger = logger;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    if (_conn.State == System.Data.ConnectionState.Open)
                    {
                        await using var cmd = _conn.CreateCommand();
                        cmd.CommandText = "SELECT pg_advisory_unlock(@key);";
                        cmd.Parameters.AddWithValue("key", _lockKey);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi giải phóng pg_advisory_unlock cho key {LockKey}", _lockKey);
                }
                finally
                {
                    await _conn.DisposeAsync();
                }
            }
        }
    }
}
