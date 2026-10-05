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
    public void Read_ValidBrandId_SetsPresenceAndValue()
    {
        var brandId = Guid.NewGuid();
        var json = $"{{\"brandId\":\"{brandId}\"}}";

        var request = JsonSerializer.Deserialize<PatchInventoryAssetRequest>(json, Options);

        Assert.NotNull(request);
        Assert.True(request.IsBrandIdSpecified);
        Assert.Equal(brandId, request.BrandId);
        Assert.False(request.IsEmpty);
    }

    [Fact]
    public void Read_NullBrandId_ThrowsJsonException()
    {
        var json = "{\"brandId\":null}";

        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PatchInventoryAssetRequest>(json, Options));

        Assert.Contains("không được null", ex.Message);
    }

    [Theory]
    [InlineData("{\"brandId\":\"not-a-guid\"}")]
    [InlineData("{\"brandId\":123}")]
    [InlineData("{\"brandId\":true}")]
    [InlineData("{\"brandId\":[\"a\"]}")]
    public void Read_InvalidBrandIdTypeOrFormat_ThrowsJsonException(string json)
    {
        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PatchInventoryAssetRequest>(json, Options));

        Assert.Contains("GUID hợp lệ", ex.Message);
    }

    [Fact]
    public void Read_DuplicateBrandId_ThrowsJsonException()
    {
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var json = $"{{\"brandId\":\"{g1}\",\"brandId\":\"{g2}\"}}";

        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PatchInventoryAssetRequest>(json, Options));

        Assert.Contains("bị lặp lại", ex.Message);
    }

    [Fact]
    public void Read_ForbiddenProperty_ThrowsJsonException()
    {
        var json = "{\"equipmentTypeId\":\"" + Guid.NewGuid() + "\"}";

        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PatchInventoryAssetRequest>(json, Options));

        Assert.Contains("không hợp lệ hoặc bị cấm", ex.Message);
    }

    [Fact]
    public void Write_BrandIdSpecified_SerializesProperty()
    {
        var brandId = Guid.NewGuid();
        var request = new PatchInventoryAssetRequest
        {
            IsBrandIdSpecified = true,
            BrandId = brandId
        };

        var json = JsonSerializer.Serialize(request, Options);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("brandId", out var prop));
        Assert.Equal(brandId, prop.GetGuid());
    }

    [Fact]
    public void Write_BrandIdNotSpecified_OmitsProperty()
    {
        var request = new PatchInventoryAssetRequest
        {
            IsModelSpecified = true,
            Model = "Sony FX3"
        };

        var json = JsonSerializer.Serialize(request, Options);

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("brandId", out _));
        Assert.True(doc.RootElement.TryGetProperty("model", out _));
    }
}
