using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Application.Caching;
using StackExchange.Redis;

namespace Dprental.InventoryService.Api.Caching;

public sealed class ValkeyRecommendationSnapshotReader : IRecommendationSnapshotReader
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(5);
    private readonly Func<IConnectionMultiplexer?> _getConnection;
    private readonly TimeProvider _timeProvider;

    public ValkeyRecommendationSnapshotReader(Func<IConnectionMultiplexer?> getConnection, TimeProvider timeProvider)
    {
        _getConnection = getConnection;
        _timeProvider = timeProvider;
    }

    public Task<RecommendationSnapshot?> GetPersonalTypesAsync(Guid userId, CancellationToken cancellationToken) =>
        ReadAsync($"rec:v1:user:{userId:D}:equipment-types", "equipmentTypeId", 20, cancellationToken);

    public Task<RecommendationSnapshot?> GetGlobalListingsAsync(CancellationToken cancellationToken) =>
        ReadAsync("rec:v1:global:popular-listings", "equipmentListingId", 100, cancellationToken);

    private async Task<RecommendationSnapshot?> ReadAsync(
        string key,
        string idProperty,
        int maxItems,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = _getConnection();
        if (connection?.IsConnected != true) return null;

        try
        {
            var value = await connection.GetDatabase().StringGetAsync(key)
                .WaitAsync(ReadTimeout, cancellationToken);
            if (!value.HasValue) return null;

            string json = value.ToString();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var version) || version.GetInt32() != 1 ||
                !root.TryGetProperty("generatedAt", out var generatedAtElement) ||
                !DateTimeOffset.TryParse(generatedAtElement.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var generatedAt) ||
                !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() > maxItems)
            {
                return null;
            }

            var now = _timeProvider.GetUtcNow();
            if (generatedAt > now || now - generatedAt >= SnapshotLifetime) return null;

            var ids = new List<Guid>(items.GetArrayLength());
            var seen = new HashSet<Guid>();
            long previousScore = long.MaxValue;
            string? previousId = null;
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty(idProperty, out var idElement) ||
                    !Guid.TryParse(idElement.GetString(), out var id) || id == Guid.Empty ||
                    !item.TryGetProperty("score", out var scoreElement) ||
                    !scoreElement.TryGetInt64(out var score) || score <= 0 ||
                    score > previousScore || !seen.Add(id))
                {
                    return null;
                }

                string canonicalId = id.ToString("D");
                if (score == previousScore &&
                    string.CompareOrdinal(previousId, canonicalId) > 0)
                {
                    return null;
                }

                ids.Add(id);
                previousScore = score;
                previousId = canonicalId;
            }

            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            return new RecommendationSnapshot(ids, generatedAt.ToUniversalTime(), hash);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException or
                                   InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
