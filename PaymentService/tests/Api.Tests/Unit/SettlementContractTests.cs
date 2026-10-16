using System.Text.Json;
using Dprental.PaymentService.Application.DTOs.Events;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Unit;

public sealed class SettlementContractTests
{
    private static SettleRentalRequest ValidRequest() => new(
        Guid.NewGuid(), Guid.NewGuid(), "INV-001", "SEPAY:123", Guid.NewGuid(),
        10_000_000m, 2_000_000m, 8_000_000m, "VND", "decision-001", "settle-001");

    [Fact]
    public void Request_ValidBreakdown_Passes()
    {
        Assert.True(ValidRequest().Validate().IsSuccess);
    }

    [Fact]
    public void Request_DeductionExceedsDeposit_Fails()
    {
        var result = (ValidRequest() with
        {
            FinalDeductionAmount = 10_000_001m,
            RefundedDepositAmount = 0m
        }).Validate();

        Assert.True(result.IsFailure);
        Assert.Equal("SETTLEMENT_EXCEEDS_DEPOSIT", result.Error.Code);
    }

    [Fact]
    public void Request_RefundBreakdownMismatch_Fails()
    {
        var result = (ValidRequest() with { RefundedDepositAmount = 7_000_000m }).Validate();

        Assert.True(result.IsFailure);
        Assert.Equal("Payment.SettlementBreakdownMismatch", result.Error.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1.5)]
    public void Request_InvalidVnd_Fails(decimal invalidAmount)
    {
        Assert.True((ValidRequest() with { DepositAmount = invalidAmount }).Validate().IsFailure);
    }

    [Fact]
    public void RentalSettledPayload_UsesRentalWireFieldNames()
    {
        var payload = new RentalSettledPayload(
            Guid.NewGuid(), Guid.NewGuid(), "INV-001", "SEPAY:123", "SET-001",
            "settle-001", 2_000_000m, 8_000_000m, "VND", "transfer-001", DateTimeOffset.UtcNow);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));

        Assert.Equal(8_000_000m, json.RootElement.GetProperty("refundedDepositAmount").GetDecimal());
        Assert.Equal("SET-001", json.RootElement.GetProperty("settlementRef").GetString());
        Assert.Equal("transfer-001", json.RootElement.GetProperty("refundRef").GetString());
    }
}
