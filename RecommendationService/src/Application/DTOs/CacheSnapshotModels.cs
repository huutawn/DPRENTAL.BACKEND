using System.Text.Json.Serialization;

namespace Dprental.RecommendationService.Application.DTOs;

public sealed record UserEquipmentTypesSnapshotItem(
    [property: JsonPropertyName("equipmentTypeId")] string EquipmentTypeId,
    [property: JsonPropertyName("score")] long Score);

public sealed record UserEquipmentTypesSnapshot(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("generatedAt")] string GeneratedAt,
    [property: JsonPropertyName("items")] IReadOnlyList<UserEquipmentTypesSnapshotItem> Items);

public sealed record GlobalPopularListingsSnapshotItem(
    [property: JsonPropertyName("equipmentListingId")] string EquipmentListingId,
    [property: JsonPropertyName("score")] long Score);

public sealed record GlobalPopularListingsSnapshot(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("generatedAt")] string GeneratedAt,
    [property: JsonPropertyName("items")] IReadOnlyList<GlobalPopularListingsSnapshotItem> Items);
