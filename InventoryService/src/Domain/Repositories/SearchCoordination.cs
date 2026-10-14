using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>Session locks live outside transactions; callers must keep the owning connection open.</summary>
public sealed class SearchCoordination : IAsyncDisposable
{
    public const long WriterKey = 0x494E565F53524348;
    public const long RunKey = WriterKey + 1;
    public const long MutationBarrierKey = WriterKey + 2;
    private readonly DbConnection _connection;
    private readonly long _key;

    private SearchCoordination(DbConnection connection, long key)
    { _connection = connection; _key = key; }

    public static async Task<SearchCoordination?> TryAcquireAsync(
        InventoryServiceDbContext db, long key, CancellationToken ct)
    {
        if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(@key)";
        var parameter = command.CreateParameter(); parameter.ParameterName = "key"; parameter.Value = key;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync(ct) is true ? new SearchCoordination(connection, key) : null;
    }

    public async ValueTask DisposeAsync()
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_unlock(@key)";
        var parameter = command.CreateParameter(); parameter.ParameterName = "key"; parameter.Value = _key;
        command.Parameters.Add(parameter);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await command.ExecuteScalarAsync(deadline.Token);
    }
}
