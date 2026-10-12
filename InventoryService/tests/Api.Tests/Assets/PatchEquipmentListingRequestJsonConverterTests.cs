using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Common.Enums;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class PatchEquipmentListingRequestJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Read_ValidNameAndDescription_SetsPresenceAndValues()
    {
        var json = """
            {
                "name": "Sony FX3 kèm bộ phụ kiện",
                "description": "Máy quay phim chuyên nghiệp"
            }
            """;

        var request = JsonSerializer.Deserialize<PatchEquipmentListingRequest>(json, Options);

        Assert.NotNull(request);
        Assert.True(request.IsNameSpecified);
        Assert.Equal("Sony FX3 kèm bộ phụ kiện", request.Name);
        Assert.True(request.IsDescriptionSpecified);
        Assert.Equal("Máy quay phim chuyên nghiệp", request.Description);
        Assert.False(request.IsEmpty);
    }

    [Fact]
    public void Read_NullNameAndNullDescription_SetsPresenceWithNull()
    {
        var json = """
            {
                "name": null,
                "description": null
            }
            """;

        var request = JsonSerializer.Deserialize<PatchEquipmentListingRequest>(json, Options);

        Assert.NotNull(request);
        Assert.True(request.IsNameSpecified);
        Assert.Null(request.Name);
        Assert.True(request.IsDescriptionSpecified);
        Assert.Null(request.Description);
    }

    [Fact]
    public void Read_ValidStatusString_SetsParsedEnum()
    {
        var json = "{\"status\":\"Active\"}";
        var request = JsonSerializer.Deserialize<PatchEquipmentListingRequest>(json, Options);

        Assert.NotNull(request);
        Assert.True(request.IsStatusSpecified);
        Assert.Equal(EquipmentListingStatus.Active, request.Status);
    }

    [Theory]
    [InlineData("{\"status\":\"999\"}")]
    [InlineData("{\"status\":\"1\"}")]
    [InlineData("{\"status\":\"0\"}")]
    [InlineData("{\"status\":\"InvalidStatus\"}")]
    public void Read_NumericOrInvalidStatusString_ThrowsJsonException(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PatchEquipmentListingRequest>(json, Options));
    }

    [Fact]
    public void Read_NumericStatusOutOfRange_ThrowsJsonException()
    {
        var json = "{\"status\":999}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PatchEquipmentListingRequest>(json, Options));
    }

    [Fact]
    public void Read_ForbiddenProperty_Quantity_ThrowsJsonException()
    {
        var json = "{\"quantity\":5}";
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PatchEquipmentListingRequest>(json, Options));
        Assert.Contains("quantity", ex.Message);
    }

    [Fact]
    public void Read_DuplicateProperty_ThrowsJsonException()
    {
        var json = "{\"name\":\"Sony FX3\",\"name\":\"Sony FX3 2\"}";
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PatchEquipmentListingRequest>(json, Options));
        Assert.Contains("lặp lại", ex.Message);
    }

    [Fact]
    public void Read_UnknownAliasDes_ThrowsJsonException()
    {
        var json = "{\"des\":\"some description\"}";
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PatchEquipmentListingRequest>(json, Options));
        Assert.Contains("không được hỗ trợ", ex.Message);
    }

    [Fact]
    public void Write_IncludesNameAndDescriptionWhenSpecified()
    {
        var request = new PatchEquipmentListingRequest
        {
            IsNameSpecified = true,
            Name = "Sony FX3 v2",
            IsDescriptionSpecified = true,
            Description = null
        };

        var json = JsonSerializer.Serialize(request, Options);

        Assert.Contains("\"name\":\"Sony FX3 v2\"", json);
        Assert.Contains("\"description\":null", json);
    }
}
