using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Domain.IRepositories;
using StackExchange.Redis;

namespace Dprental.InventoryService.Api.Recommendation;

public sealed partial class ValkeyRecommendationRankingReader
{
    private sealed class CachedRankingSnapshotDto
    {
        [JsonPropertyName("version")]
        public int? Version { get; set; }

        [JsonPropertyName("cycle")]
        public long? Cycle { get; set; }

        [JsonPropertyName("seed")]
        public string? Seed { get; set; }

        [JsonPropertyName("personal")]
        public List<PersonalScoreDto>? Personal { get; set; }

        [JsonPropertyName("global")]
        public List<GlobalScoreDto>? Global { get; set; }
    }

    private sealed record PersonalScoreDto(
        [property: JsonPropertyName("typeId")] Guid TypeId,
        [property: JsonPropertyName("score")] long Score);

    private sealed record GlobalScoreDto(
        [property: JsonPropertyName("listingId")] Guid ListingId,
        [property: JsonPropertyName("score")] long Score);

    private static bool TryParseSnapshotJson(
        byte[] bytes,
        long expectedCycle,
        string expectedSeed,
        bool isAnonymous,
        out IReadOnlyList<EquipmentTypeInterestScore> personalScores,
        out IReadOnlyList<ListingPopularityScore> globalScores)
    {
        personalScores = [];
        globalScores = [];

        try
        {
            var dto = JsonSerializer.Deserialize<CachedRankingSnapshotDto>(bytes);
            if (dto is null) return false;
            if (!dto.Version.HasValue || dto.Version.Value != 1) return false;
            if (!dto.Cycle.HasValue || dto.Cycle.Value != expectedCycle) return false;
            if (string.IsNullOrWhiteSpace(dto.Seed) || dto.Seed != expectedSeed) return false;
            if (dto.Personal is null || dto.Global is null) return false;

            if (isAnonymous && dto.Personal.Count > 0) return false;
            if (dto.Personal.Count == 0 && dto.Global.Count == 0) return false;
            if (dto.Personal.Count > MaxInterestsCount) return false;
            if (dto.Global.Count > MaxPopularListingsCount) return false;

            var personalList = new List<EquipmentTypeInterestScore>(dto.Personal.Count);
            var seenPersonal = new HashSet<Guid>();
            foreach (var p in dto.Personal)
            {
                if (p is null || p.TypeId == Guid.Empty || !seenPersonal.Add(p.TypeId)) return false;
                if (p.Score < 1 || p.Score > MaxValidScore) return false;
                personalList.Add(new EquipmentTypeInterestScore(p.TypeId, p.Score));
            }

            var globalList = new List<ListingPopularityScore>(dto.Global.Count);
            var seenGlobal = new HashSet<Guid>();
            foreach (var g in dto.Global)
            {
                if (g is null || g.ListingId == Guid.Empty || !seenGlobal.Add(g.ListingId)) return false;
                if (g.Score < 1 || g.Score > MaxValidScore) return false;
                globalList.Add(new ListingPopularityScore(g.ListingId, g.Score));
            }

            personalScores = personalList;
            globalScores = globalList;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParsePersonalEntries(
        SortedSetEntry[] entries,
        out IReadOnlyList<EquipmentTypeInterestScore> scores)
    {
        scores = [];
        if (entries.Length == 0) return true;
        if (entries.Length > MaxInterestsCount) return false;

        var list = new List<EquipmentTypeInterestScore>(entries.Length);
        var seen = new HashSet<Guid>();

        foreach (var entry in entries)
        {
            if (!TryParseCanonicalGuid(entry.Element, out var id) || id == Guid.Empty)
                return false;

            if (!seen.Add(id))
                return false;

            if (!IsValidScore(entry.Score, out long longScore))
                return false;

            list.Add(new EquipmentTypeInterestScore(id, longScore));
        }

        scores = list;
        return true;
    }

    private static bool TryParseGlobalEntries(
        SortedSetEntry[] entries,
        out IReadOnlyList<ListingPopularityScore> scores)
    {
        scores = [];
        if (entries.Length == 0) return true;
        if (entries.Length > MaxPopularListingsCount) return false;

        var list = new List<ListingPopularityScore>(entries.Length);
        var seen = new HashSet<Guid>();

        foreach (var entry in entries)
        {
            if (!TryParseCanonicalGuid(entry.Element, out var id) || id == Guid.Empty)
                return false;

            if (!seen.Add(id))
                return false;

            if (!IsValidScore(entry.Score, out long longScore))
                return false;

            list.Add(new ListingPopularityScore(id, longScore));
        }

        scores = list;
        return true;
    }

    private static bool TryParseCanonicalGuid(RedisValue value, out Guid id)
    {
        id = Guid.Empty;
        if (value.IsNull) return false;
        string? str = value;
        if (string.IsNullOrEmpty(str) || str.Length != 36) return false;

        if (!Guid.TryParseExact(str, "D", out id)) return false;

        // Strictly verify lowercase hex chars and hyphens (canonical "D" format)
        for (int i = 0; i < str.Length; i++)
        {
            char c = str[i];
            if (i == 8 || i == 13 || i == 18 || i == 23)
            {
                if (c != '-') return false;
            }
            else
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    return false;
            }
        }

        return id != Guid.Empty;
    }

    private static bool IsValidScore(double score, out long longScore)
    {
        longScore = 0;
        if (double.IsNaN(score) || double.IsInfinity(score)) return false;
        if (score < 1.0 || score > MaxValidScore) return false;
        if (Math.Floor(score) != score) return false;

        longScore = (long)score;
        return true;
    }

}
