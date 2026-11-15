using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.Serialization.Converters;

namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Yêu cầu tạo mới một EquipmentListing ở trạng thái Draft.
/// Cho phép tạo nháp khi chưa đủ ảnh, giá thuê (RentalRate) hoặc máy vật lý.
/// Số serial là tùy chọn (nullable); nếu không tạo đơn vị máy hoặc để trống serial thì đơn vị máy được tạo với serial null hoặc không tạo.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateEquipmentListingRequest(
    Guid EquipmentTypeId,
    Guid BrandId,
    string Name,
    string Model,
    string? SerialNumber = null,
    JsonElement SpecValues = default,
    RentalRateDto? RentalRate = null,
    int? ManufactureYear = null,
    string? Description = null,
    int SchemaVersion = 1,
    IReadOnlyList<Guid>? MediaIds = null,
    Guid? ThumbnailMediaId = null,
    [property: JsonConverter(typeof(StrictIntegerDepositJsonConverter))] decimal? DepositAmount = null,
    bool? CreateInitialUnit = null
);
