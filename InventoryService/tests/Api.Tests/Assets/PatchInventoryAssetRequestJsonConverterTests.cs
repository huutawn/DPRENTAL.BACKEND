using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class PatchInventoryAssetRequestJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Read_ValidSerialNumber_SetsPresenceAndValue()
    {
        var json = "{\"serialNumber\":\"SN-12345\"}";

        var request = JsonSerializer.Deserialize<PatchInventoryAssetRequest>(json, Options);

        Assert.NotNull(request);
        Assert.True(request.IsSerialNumberSpecified);
        Assert.Equal("SN-12345", request.SerialNumber);
        Assert.False(request.IsEmpty);
    }

    [Fact]
    public void Read_NullSerialNumber_AllowedAndSetsNull()
    {
        var json = "{\"serialNumber\":null}";

        var request = JsonSerializer.Deserialize<PatchInventoryAssetRequest>(json, Options);

        Assert.NotNull(request);
        Assert.True(request.IsSerialNumberSpecified);
        Assert.Null(request.SerialNumber);
    }

    [Fact]
    public void Read_ValidManufactureYear_SetsPresenceAndValue()
    {
        var json = "{\"manufactureYear\":2024}";

        var request = JsonSerializer.Deserialize<PatchInventoryAssetRequest>(json, Options);

        Assert.NotNull(request);
        Assert.True(request.IsManufactureYearSpecified);
        Assert.Equal(2024, request.ManufactureYear);
    }

    [Fact]
    public void Read_ForbiddenProperty_BrandIdOrModel_ThrowsJsonException()
    {
        var json = "{\"brandId\":\"" + Guid.NewGuid() + "\"}";

        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PatchInventoryAssetRequest>(json, Options));

        Assert.Contains("không được phép cập nhật", ex.Message);
    }

    [Fact]
    public void Read_DuplicateProperty_ThrowsJsonException()
    {
        var json = "{\"serialNumber\":\"SN1\",\"serialNumber\":\"SN2\"}";

        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PatchInventoryAssetRequest>(json, Options));

        Assert.Contains("bị lặp lại", ex.Message);
    }

    [Fact]
    public void Write_PropertiesSpecified_SerializesCorrectly()
    {
        var request = new PatchInventoryAssetRequest
        {
            IsSerialNumberSpecified = true,
            SerialNumber = "SN-999",
            IsManufactureYearSpecified = true,
            ManufactureYear = 2023
        };

        var json = JsonSerializer.Serialize(request, Options);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("SN-999", doc.RootElement.GetProperty("serialNumber").GetString());
        Assert.Equal(2023, doc.RootElement.GetProperty("manufactureYear").GetInt32());
    }
}
