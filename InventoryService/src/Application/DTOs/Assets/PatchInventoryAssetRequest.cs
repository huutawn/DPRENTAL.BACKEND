using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.Serialization.Converters;

namespace Dprental.InventoryService.Application.DTOs.Assets;

/// <summary>
/// Yêu cầu cập nhật một phần thông tin vật lý của đơn vị thiết bị (PATCH) có theo dõi presence.
/// Chỉ cho phép cập nhật serialNumber và manufactureYear.
/// </summary>
[JsonConverter(typeof(PatchInventoryAssetRequestJsonConverter))]
public sealed class PatchInventoryAssetRequest
{
    [JsonIgnore]
    public bool IsSerialNumberSpecified { get; set; }
    public string? SerialNumber { get; set; }

    [JsonIgnore]
    public bool IsManufactureYearSpecified { get; set; }
    public int? ManufactureYear { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        !IsSerialNumberSpecified &&
        !IsManufactureYearSpecified;
}
