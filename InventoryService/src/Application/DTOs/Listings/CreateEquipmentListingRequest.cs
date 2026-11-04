using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.Serialization.Converters;

namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Yêu cầu tạo mới một EquipmentListing ở trạng thái Draft cùng một thiết bị vật lý ban đầu qua số serial.
/// Năm sản xuất (manufactureYear) là bắt buộc và được kế thừa cho thiết bị vật lý đầu tiên.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateEquipmentListingRequest(
    Guid EquipmentTypeId,
    Guid BrandId,
    string Name,
    string Model,
    string SerialNumber,
    JsonElement SpecValues,
    RentalRateDto RentalRate,
    int? ManufactureYear = null,
    string? Description = null,
    int SchemaVersion = 1,
    IReadOnlyList<Guid>? MediaIds = null,
    Guid? ThumbnailMediaId = null,
    [property: JsonConverter(typeof(StrictIntegerDepositJsonConverter))] decimal? DepositAmount = null
);
