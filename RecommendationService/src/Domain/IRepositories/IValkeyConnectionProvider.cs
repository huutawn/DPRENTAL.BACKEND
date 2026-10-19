using StackExchange.Redis;

namespace Dprental.RecommendationService.Domain.IRepositories;

public interface IValkeyConnectionProvider : IAsyncDisposable, IDisposable
{
    Task<IConnectionMultiplexer?> GetMultiplexerAsync(CancellationToken cancellationToken);

    bool IsConnected { get; }
}
