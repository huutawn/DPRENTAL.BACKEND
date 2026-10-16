using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.Caching;

/// <summary>
/// Đại diện cho kết quả đọc nguồn thành công kèm theo thời điểm hết hạn tuyệt đối (UTC).
/// Nếu ExpiresAtUtc là null, kết quả thành công nhưng không được ghi vào cache.
/// </summary>
/// <typeparam name="T">Kiểu dữ liệu.</typeparam>
/// <param name="Value">Giá trị dữ liệu.</param>
/// <param name="ExpiresAtUtc">Mốc hết hạn tuyệt đối UTC (null nếu không cache).</param>
public readonly record struct CachedRead<T>(
    T Value,
    DateTimeOffset? ExpiresAtUtc
);

/// <summary>
/// Mục tiêu cache xác định bởi scope, queryType và inputHash.
/// </summary>
public readonly record struct CacheTarget(
    string Scope,
    string QueryType,
    string InputHash
)
{
    public string CacheKey => $"{Scope}:{QueryType}:{InputHash}";
}

/// <summary>
/// Kết quả đọc từ Valkey read cache.
/// </summary>
/// <typeparam name="T">Kiểu dữ liệu cache.</typeparam>
/// <param name="Value">Giá trị DTO đã giải mã (hoặc null nếu miss/hết hạn/payload lỗi/generation lệch).</param>
/// <param name="GenerationToken">Token thế hệ hiện tại của scope tại thời điểm đọc.</param>
/// <param name="IsHit">Đánh dấu cache hit hợp lệ.</param>
/// <param name="ExpiresAtUtc">Mốc hết hạn của dữ liệu được cache.</param>
public readonly record struct CacheReadResult<T>(
    T? Value,
    string GenerationToken,
    bool IsHit,
    DateTimeOffset? ExpiresAtUtc = null
)
{
    public void Deconstruct(out T? value, out string generationToken)
    {
        value = Value;
        generationToken = GenerationToken;
    }

    public void Deconstruct(out T? value, out string generationToken, out bool isHit)
    {
        value = Value;
        generationToken = GenerationToken;
        isHit = IsHit;
    }

    public void Deconstruct(out T? value, out string generationToken, out bool isHit, out DateTimeOffset? expiresAtUtc)
    {
        value = Value;
        generationToken = GenerationToken;
        isHit = IsHit;
        expiresAtUtc = ExpiresAtUtc;
    }
}

/// <summary>
/// Hợp đồng cache đọc hợp nhất cho toàn bộ InventoryService (Valkey Read Cache v2).
/// Hỗ trợ đọc/bulk read, ghi có điều kiện theo generation token của từng scope và invalidation atomic $O(1)$.
/// </summary>
public interface IInventoryReadCache
{
    /// <summary>
    /// Cho biết cache có đang được kích hoạt hay không.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Đọc dữ liệu từ cache theo scope, loại truy vấn và hash input đã chuẩn hóa.
    /// </summary>
    Task<CacheReadResult<T>> GetAsync<T>(
        string scope,
        string queryType,
        string inputHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ghi dữ liệu vào cache có điều kiện: chỉ ghi nếu generation token của scope vẫn khớp với expectedGenerationToken.
    /// Bỏ qua ghi nếu deadline đã qua hoặc payload vượt quá giới hạn 1 MiB.
    /// </summary>
    Task<bool> SetAsync<T>(
        string scope,
        string queryType,
        string inputHash,
        T value,
        string expectedGenerationToken,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đọc bulk các item trong cùng một scope (pipeline theo slot).
    /// </summary>
    Task<IReadOnlyDictionary<string, CacheReadResult<T>>> GetBulkAsync<T>(
        string scope,
        string queryType,
        IReadOnlyList<string> inputHashes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ghi bulk các item trong cùng một scope có điều kiện theo generation token.
    /// </summary>
    Task SetBulkAsync<T>(
        string scope,
        string queryType,
        IReadOnlyList<(string InputHash, T Value, DateTimeOffset ExpiresAtUtc)> items,
        string expectedGenerationToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pipeline lấy hoặc tạo cache duy nhất (single pipeline):
    /// Kiểm tra cache -> gate theo key -> recheck -> snapshot generation -> source fill -> conditional write.
    /// Giới hạn tối đa 32 source fills đồng thời và 1024 requests đang chờ.
    /// </summary>
    Task<Result<T>> GetOrCreateAsync<T>(
        string scope,
        string queryType,
        string inputHash,
        Func<string, CancellationToken, Task<Result<CachedRead<T>>>> factory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pipeline lấy hoặc tạo cache bulk duy nhất cho các truy vấn theo lô trong cùng một scope.
    /// Gom các miss thành một lời gọi factory batch duy nhất.
    /// </summary>
    Task<Result<IReadOnlyDictionary<CacheTarget, T>>> GetOrCreateBulkAsync<T>(
        IReadOnlyList<CacheTarget> targets,
        Func<IReadOnlyList<CacheTarget>, CancellationToken, Task<Result<IReadOnlyDictionary<CacheTarget, CachedRead<T>>>>> factory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đổi generation token của scope, khiến toàn bộ dữ liệu thuộc scope đó của generation cũ không còn được đọc.
    /// </summary>
    Task InvalidateScopeAsync(
        string scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đổi generation token của nhiều scope.
    /// </summary>
    Task InvalidateScopesAsync(
        IEnumerable<string> scopes,
        CancellationToken cancellationToken = default);
}
