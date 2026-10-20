using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.Serialization.Converters;
using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Yêu cầu cập nhật thông tin chung hoặc trạng thái của EquipmentListing (PATCH) có theo dõi presence.
/// Từ chối cập nhật số lượng qua field 'quantity'.
/// </summary>
[JsonConverter(typeof(PatchEquipmentListingRequestJsonConverter))]
public sealed class PatchEquipmentListingRequest
{
    [JsonIgnore]
    public bool IsBrandIdSpecified { get; set; }
    public Guid? BrandId { get; set; }
    [JsonIgnore]
    public bool IsNameSpecified { get; set; }
    public string? Name { get; set; }

    [JsonIgnore]
    public bool IsDescriptionSpecified { get; set; }
    public string? Description { get; set; }

    [JsonIgnore]
    public bool IsModelSpecified { get; set; }
    public string? Model { get; set; }

    [JsonIgnore]
    public bool IsManufactureYearSpecified { get; set; }
    public int? ManufactureYear { get; set; }

    [JsonIgnore]
    public bool IsMediaIdsSpecified { get; set; }
    public IReadOnlyList<Guid>? MediaIds { get; set; }

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
    public bool IsStatusSpecified { get; set; }
    public EquipmentListingStatus? Status { get; set; }


    [JsonIgnore]
    public bool IsDepositAmountSpecified { get; set; }
    public decimal? DepositAmount { get; set; }
    [JsonIgnore]
    public bool IsEmpty =>
        !IsBrandIdSpecified &&
        !IsNameSpecified &&
        !IsDescriptionSpecified &&
        !IsModelSpecified &&
        !IsManufactureYearSpecified &&
        !IsMediaIdsSpecified &&
        !IsThumbnailMediaIdSpecified &&
        !IsSchemaVersionSpecified &&
        !IsSpecValuesSpecified &&
        !IsStatusSpecified &&
        !IsDepositAmountSpecified;
}
