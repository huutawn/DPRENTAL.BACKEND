using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.Caching;

/// <summary>
/// Triển khai No-Op cho IInventoryReadCache khi cache bị tắt hoặc trong môi trường test không cần Valkey.
/// Thực thi trực tiếp factory của caller và bảo lưu toàn bộ lỗi / cancellation.
/// </summary>
public sealed class NoOpInventoryReadCache : IInventoryReadCache
{
    public static readonly NoOpInventoryReadCache Instance = new();

    public bool IsEnabled => false;

    public Task<CacheReadResult<T>> GetAsync<T>(
        string scope,
        string queryType,
        string inputHash,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CacheReadResult<T>(default, string.Empty, false));

    public Task<bool> SetAsync<T>(
        string scope,
        string queryType,
        string inputHash,
        T value,
        string expectedGenerationToken,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyDictionary<string, CacheReadResult<T>>> GetBulkAsync<T>(
        string scope,
        string queryType,
        IReadOnlyList<string> inputHashes,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, CacheReadResult<T>> result =
            inputHashes.ToDictionary(h => h, _ => new CacheReadResult<T>(default, string.Empty, false));
        return Task.FromResult(result);
    }

    public Task SetBulkAsync<T>(
        string scope,
        string queryType,
        IReadOnlyList<(string InputHash, T Value, DateTimeOffset ExpiresAtUtc)> items,
        string expectedGenerationToken,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async Task<Result<T>> GetOrCreateAsync<T>(
        string scope,
        string queryType,
        string inputHash,
        Func<string, CancellationToken, Task<Result<CachedRead<T>>>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var res = await factory(string.Empty, cancellationToken);
        if (res.IsFailure)
        {
            return res.Error;
        }

        return Result.Success(res.Value.Value);
    }

    public async Task<Result<IReadOnlyDictionary<CacheTarget, T>>> GetOrCreateBulkAsync<T>(
        IReadOnlyList<CacheTarget> targets,
        Func<IReadOnlyList<CacheTarget>, CancellationToken, Task<Result<IReadOnlyDictionary<CacheTarget, CachedRead<T>>>>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(factory);

        var res = await factory(targets, cancellationToken);
        if (res.IsFailure)
        {
            return res.Error;
        }

        var dict = res.Value.ToDictionary(kv => kv.Key, kv => kv.Value.Value);
        return Result.Success<IReadOnlyDictionary<CacheTarget, T>>(dict);
    }

    public Task InvalidateScopeAsync(
        string scope,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task InvalidateScopesAsync(
        IEnumerable<string> scopes,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
