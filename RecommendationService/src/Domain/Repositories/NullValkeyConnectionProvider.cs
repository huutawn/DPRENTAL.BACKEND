using Dprental.RecommendationService.Domain.IRepositories;
using StackExchange.Redis;

namespace Dprental.RecommendationService.Domain.Repositories;

public sealed class NullValkeyConnectionProvider : IValkeyConnectionProvider
{
    public bool IsConnected => false;

    public Task<IConnectionMultiplexer?> GetMultiplexerAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IConnectionMultiplexer?>(null);

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
