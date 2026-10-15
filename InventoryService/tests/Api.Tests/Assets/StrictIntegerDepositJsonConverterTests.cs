using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Listings;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class StrictIntegerDepositJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static string BuildCreateJson(string? depositAmountJson = null)
    {
        var depositPart = depositAmountJson is not null ? $",\"depositAmount\":{depositAmountJson}" : "";
        return $$"""
            {
                "equipmentTypeId": "{{Guid.NewGuid():D}}",
                "brandId": "{{Guid.NewGuid():D}}",
                "name": "Sony FX3",
                "model": "FX3",
                "serialNumber": "SN-001",
                "specValues": {},
                "rentalRate": { "dailyRate": 100000, "currency": "VND" }
                {{depositPart}}
            }
            """;
    }

    [Fact]
    public void Read_OmittedDepositAmount_LeavesNull()
    {
        var json = BuildCreateJson();
        var request = JsonSerializer.Deserialize<CreateEquipmentListingRequest>(json, Options);
        Assert.NotNull(request);
        Assert.Null(request.DepositAmount);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("500000", 500000)]
    [InlineData("999999999999999999", 999999999999999999)]
    [InlineData("-100", -100)]
    public void Read_ValidIntegerToken_ParsesExactDecimal(string depositToken, decimal expected)
    {
        var json = BuildCreateJson(depositToken);
        var request = JsonSerializer.Deserialize<CreateEquipmentListingRequest>(json, Options);
        Assert.NotNull(request);
        Assert.Equal(expected, request.DepositAmount);
    }

    [Fact]
    public void Read_NullDepositAmount_ThrowsJsonException()
    {
        var json = BuildCreateJson("null");
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateEquipmentListingRequest>(json, Options));
        Assert.Contains("INVALID_DEPOSIT_AMOUNT", ex.Message);
    }

    [Theory]
    [InlineData("500000.5")]
    [InlineData("0.00000000000000000000000000000001")]
    [InlineData("100.9999999999999999999999999999")]
    [InlineData("\"not-a-number\"")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("99999999999999999999999999999999999999999999")]
    public void Read_InvalidDepositAmountToken_ThrowsJsonException(string invalidToken)
    {
        var json = BuildCreateJson(invalidToken);
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateEquipmentListingRequest>(json, Options));
        Assert.Contains("INVALID_DEPOSIT_AMOUNT", ex.Message);
    }
}
