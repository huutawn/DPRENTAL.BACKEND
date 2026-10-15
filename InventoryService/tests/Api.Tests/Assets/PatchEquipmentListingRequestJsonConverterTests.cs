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

    [Fact]
    public void Read_MediaIdsAndManufactureYear_SetsPresenceAndPreservesOrder()
    {
        var m1 = Guid.NewGuid();
        var m2 = Guid.NewGuid();
        var json = $$"""
            {
                "manufactureYear": 2024,
                "mediaIds": ["{{m2}}", "{{m1}}"]
            }
            """;

        var request = JsonSerializer.Deserialize<PatchEquipmentListingRequest>(json, Options);

        Assert.NotNull(request);
        Assert.True(request.IsManufactureYearSpecified);
        Assert.Equal(2024, request.ManufactureYear);
        Assert.True(request.IsMediaIdsSpecified);
        Assert.NotNull(request.MediaIds);
        Assert.Equal([m2, m1], request.MediaIds);
        Assert.False(request.IsEmpty);
    }

    [Fact]
    public void Read_EmptyMediaIdsArray_SetsPresenceWithEmptyList()
    {
        var json = "{\"mediaIds\":[]}";
        var request = JsonSerializer.Deserialize<PatchEquipmentListingRequest>(json, Options);

        Assert.NotNull(request);
        Assert.True(request.IsMediaIdsSpecified);
        Assert.NotNull(request.MediaIds);
        Assert.Empty(request.MediaIds);
        Assert.False(request.IsEmpty);
    }

    [Fact]
    public void Read_NullMediaIdsAndNullManufactureYear_SetsPresenceWithNull()
    {
        var json = """
            {
                "manufactureYear": null,
                "mediaIds": null
            }
            """;

        var request = JsonSerializer.Deserialize<PatchEquipmentListingRequest>(json, Options);

        Assert.NotNull(request);
        Assert.True(request.IsManufactureYearSpecified);
        Assert.Null(request.ManufactureYear);
        Assert.True(request.IsMediaIdsSpecified);
        Assert.Null(request.MediaIds);
    }

    [Theory]
    [InlineData("{\"mediaIds\":\"not-an-array\"}")]
    [InlineData("{\"mediaIds\":[\"not-a-guid\"]}")]
    [InlineData("{\"manufactureYear\":\"2024\"}")]
    public void Read_InvalidMediaIdsOrManufactureYearType_ThrowsJsonException(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PatchEquipmentListingRequest>(json, Options));
    }

    [Fact]
    public void Write_IncludesMediaIdsAndManufactureYearWhenSpecified()
    {
        var m1 = Guid.NewGuid();
        var request = new PatchEquipmentListingRequest
        {
            IsManufactureYearSpecified = true,
            ManufactureYear = 2025,
            IsMediaIdsSpecified = true,
            MediaIds = [m1]
        };

        var json = JsonSerializer.Serialize(request, Options);

        Assert.Contains("\"manufactureYear\":2025", json);
        Assert.Contains($"\"mediaIds\":[\"{m1:D}\"]", json);
    }

    [Theory]
    [InlineData("{\"depositAmount\":0}", 0)]
    [InlineData("{\"depositAmount\":500000}", 500000)]
    [InlineData("{\"depositAmount\":999999999999999999}", 999999999999999999)]
    public void Read_ValidDepositAmount_SetsPresenceAndValue(string json, decimal expected)
    {
        var request = JsonSerializer.Deserialize<PatchEquipmentListingRequest>(json, Options);
        Assert.NotNull(request);
        Assert.True(request.IsDepositAmountSpecified);
        Assert.Equal(expected, request.DepositAmount);
    }

    [Fact]
    public void Read_NullDepositAmount_SetsPresenceWithNull()
    {
        var request = JsonSerializer.Deserialize<PatchEquipmentListingRequest>("{\"depositAmount\":null}", Options);
        Assert.NotNull(request);
        Assert.True(request.IsDepositAmountSpecified);
        Assert.Null(request.DepositAmount);
    }

    [Fact]
    public void Read_OmittedDepositAmount_IsNotSpecified()
    {
        var request = JsonSerializer.Deserialize<PatchEquipmentListingRequest>("{\"name\":\"New Camera\"}", Options);
        Assert.NotNull(request);
        Assert.False(request.IsDepositAmountSpecified);
        Assert.Null(request.DepositAmount);
    }

    [Theory]
    [InlineData("{\"depositAmount\":500000.5}")]
    [InlineData("{\"depositAmount\":0.00000000000000000000000000000001}")]
    [InlineData("{\"depositAmount\":\"not-a-number\"}")]
    [InlineData("{\"depositAmount\":true}")]
    [InlineData("{\"depositAmount\":{}}")]
    [InlineData("{\"depositAmount\":[]}")]
    public void Read_InvalidDepositAmount_FractionOrWrongType_ThrowsJsonException(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PatchEquipmentListingRequest>(json, Options));
    }

    [Fact]
    public void Write_IncludesDepositAmountWhenSpecified()
    {
        var reqWithVal = new PatchEquipmentListingRequest
        {
            IsDepositAmountSpecified = true,
            DepositAmount = 500_000m
        };
        var jsonWithVal = JsonSerializer.Serialize(reqWithVal, Options);
        Assert.Contains("\"depositAmount\":500000", jsonWithVal);

        var reqWithNull = new PatchEquipmentListingRequest
        {
            IsDepositAmountSpecified = true,
            DepositAmount = null
        };
        var jsonWithNull = JsonSerializer.Serialize(reqWithNull, Options);
        Assert.Contains("\"depositAmount\":null", jsonWithNull);

        var reqOmitted = new PatchEquipmentListingRequest
        {
            IsNameSpecified = true,
            Name = "Sony"
        };
        var jsonOmitted = JsonSerializer.Serialize(reqOmitted, Options);
        Assert.DoesNotContain("depositAmount", jsonOmitted);
    }
}
