using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.Serialization.Converters;

namespace Dprental.InventoryService.Application.DTOs.Assets;

/// <summary>
/// Yêu cầu cập nhật một phần thông tin tài sản (PATCH) có theo dõi presence.
/// </summary>
[JsonConverter(typeof(PatchInventoryAssetRequestJsonConverter))]
public sealed class PatchInventoryAssetRequest
{
    [JsonIgnore]
    public bool IsModelSpecified { get; set; }
    public string? Model { get; set; }

    [JsonIgnore]
    public bool IsSerialNumberSpecified { get; set; }
    public string? SerialNumber { get; set; }

    [JsonIgnore]
    public bool IsManufactureYearSpecified { get; set; }
    public int? ManufactureYear { get; set; }

    [JsonIgnore]
    public bool IsThumbnailMediaIdSpecified { get; set; }
    public Guid? ThumbnailMediaId { get; set; }

    [JsonIgnore]
    public bool IsSchemaVersionSpecified { get; set; }
    public int? SchemaVersion { get; set; }

    [JsonIgnore]
    public bool IsSpecValuesSpecified { get; set; }
    public JsonElement? SpecValues { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        !IsModelSpecified &&
        !IsSerialNumberSpecified &&
        !IsManufactureYearSpecified &&
        !IsThumbnailMediaIdSpecified &&
        !IsSchemaVersionSpecified &&
        !IsSpecValuesSpecified;
}
