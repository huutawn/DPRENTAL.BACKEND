using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class RentalOrderEntityTests
{
    private readonly DateTimeOffset _now = new(2026, 10, 8, 8, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public void Create_ValidInputs_CalculatesAmountsAndInitializesPendingPayment()
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        Guid a1 = Guid.NewGuid();
        Guid a2 = Guid.NewGuid();

        // 3 days: 2026-10-10 to 2026-10-13
        DateOnly start = new(2026, 10, 10);
        DateOnly end = new(2026, 10, 13);
        decimal dailyRate = 150_000m;
        decimal depositPerUnit = 300_000m;
        int qty = 2;

        InitialRentalItemDto itemDto = new(
            itemId,
            Guid.NewGuid(),
            supplierId,
            qty,
            start,
            end,
            "Sony FX3 Camera Kit",
            "Cinema camera with lens",
            "District 1, HCMC",
            dailyRate,
            depositPerUnit,
            "group-001",
            [
                new(a1, Guid.NewGuid(), Guid.NewGuid()),
                new(a2, Guid.NewGuid(), Guid.NewGuid())
            ]);

        Result<RentalOrder> result = RentalOrder.Create(
            orderId,
            "ORD-20261008-001",
            renterId,
            "VND",
            "Pickup",
            "Nguyen Van A",
            "0909123456",
            "123 Nguyen Hue, D1",
            "Please test equipment before pickup",
            null, // defaults to ORIGINAL_PAYMENT_SOURCE
            "v1.0",
            "terms/v1.0.pdf",
            "sha256-terms-hash",
            "op-create-001",
            _now,
            [itemDto]);

        Assert.True(result.IsSuccess);
        RentalOrder order = result.Value;

        Assert.Equal(orderId, order.Id);
        Assert.Equal("ORD-20261008-001", order.OrderCode);
        Assert.Equal(RentalOrderStatus.PendingPayment, order.Status);
        Assert.Equal("VND", order.Currency);
        Assert.Equal("ORIGINAL_PAYMENT_SOURCE", order.RefundDestinationRef);
        Assert.Equal(1, order.Version);
        Assert.Equal(_now.AddMinutes(30), order.PaymentDeadline);

        // Verify Calculations:
        // Expected days = 3 (13 - 10)
        // RentalAmount = dailyRate * quantity * days = 150,000 * 2 * 3 = 900,000 VND
        // DepositAmount = depositPerUnit * quantity = 300,000 * 2 = 600,000 VND
        // TotalPayableAmount = 900,000 + 600,000 = 1,500,000 VND
        Assert.Single(order.Items);
        RentalItem item = order.Items.First();
        Assert.Equal(900_000m, item.RentalAmount);
        Assert.Equal(600_000m, item.DepositAmount);
        Assert.Equal(1_500_000m, item.TotalAmount);

        Assert.Equal(900_000m, order.TotalRentalAmount);
        Assert.Equal(600_000m, order.TotalDepositAmount);
        Assert.Equal(1_500_000m, order.TotalPayableAmount);

        // Verify Terms Acceptance
        Assert.NotNull(order.TermsAcceptance);
        Assert.Equal("v1.0", order.TermsAcceptance.TermsVersion);
        Assert.Equal("sha256-terms-hash", order.TermsAcceptance.TermsContentHash);

        // Verify Initial Checkout State Transition & Receipt
        Assert.Single(order.StateTransitions);
        RentalStateTransition transition = order.StateTransitions.First();
        Assert.Equal("CreateOrder", transition.Action);
        Assert.Equal(RentalOrderStatus.PendingPayment.ToString(), transition.ToState);

        Assert.Single(order.OperationReceipts);
        RentalOperationReceipt receipt = order.OperationReceipts.First();
        Assert.Equal("CreateOrder", receipt.Action);
        Assert.Equal("op-create-001", receipt.OperationId);
        Assert.True(receipt.IsSuccess);
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    [InlineData("vnd1")]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsNonVndOrMissingCurrency(string currency)
    {
        Result<RentalOrder> result = RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-CURR",
            Guid.NewGuid(),
            currency,
            "Pickup",
            "Customer",
            "0909123456",
            "Address",
            null,
            null,
            "v1",
            "doc",
            "hash",
            "op-curr",
            _now,
            [CreateSampleItemDto(100_000m, 200_000m)]);

        Assert.True(result.IsFailure);
        Assert.Equal("Money.CurrencyMismatch", result.Error.Code);
    }

    [Fact]
    public void Create_RejectsFractionalVndInRateOrDeposit()
    {
        // 1. Fractional daily rate
        Result<RentalOrder> rateResult = RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-FRAC",
            Guid.NewGuid(),
            "VND",
            "Pickup",
            "Customer",
            "0909123456",
            "Address",
            null,
            null,
            "v1",
            "doc",
            "hash",
            "op-frac",
            _now,
            [CreateSampleItemDto(100_000.50m, 200_000m)]);

        Assert.True(rateResult.IsFailure);
        Assert.Equal("Money.FractionalVndNotAllowed", rateResult.Error.Code);

        // 2. Fractional deposit
        Result<RentalOrder> depResult = RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-FRAC-DEP",
            Guid.NewGuid(),
            "VND",
            "Pickup",
            "Customer",
            "0909123456",
            "Address",
            null,
            null,
            "v1",
            "doc",
            "hash",
            "op-frac-dep",
            _now,
            [CreateSampleItemDto(100_000m, 200_000.25m)]);

        Assert.True(depResult.IsFailure);
        Assert.Equal("Money.FractionalVndNotAllowed", depResult.Error.Code);
    }

    [Fact]
    public void Create_RejectsNegativeOrExceedingCeilingAmounts()
    {
        // 1. Negative amount
        Result<RentalOrder> negResult = RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-NEG",
            Guid.NewGuid(),
            "VND",
            "Pickup",
            "Customer",
            "0909123456",
            "Address",
            null,
            null,
            "v1",
            "doc",
            "hash",
            "op-neg",
            _now,
            [CreateSampleItemDto(-50_000m, 200_000m)]);

        Assert.True(negResult.IsFailure);
        Assert.Equal("Money.NegativeAmount", negResult.Error.Code);

        // 2. Exceeding contract v1 ceiling (1,000,000,000,000 VND)
        Result<RentalOrder> exceedResult = RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-EXCEED",
            Guid.NewGuid(),
            "VND",
            "Pickup",
            "Customer",
            "0909123456",
            "Address",
            null,
            null,
            "v1",
            "doc",
            "hash",
            "op-exceed",
            _now,
            [CreateSampleItemDto(1_000_000_000_000m, 200_000m)]);

        Assert.True(exceedResult.IsFailure);
        Assert.Equal("Money.AmountLimitExceeded", exceedResult.Error.Code);
    }

    [Fact]
    public void Create_RejectsInvalidDatesOrNonPositiveQuantity()
    {
        // 1. End date before start date
        InitialRentalItemDto invalidDatesDto = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            new(2026, 10, 15),
            new(2026, 10, 10), // End < Start
            "Title",
            null,
            "Loc",
            100_000m,
            200_000m,
            "grp-01",
            [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())]);

        Result<RentalOrder> dateResult = RentalOrder.Create(
            Guid.NewGuid(), "ORD-DATE", Guid.NewGuid(), "VND", "Pickup", "Cust", "090", "Addr", null, null, "v1", "doc", "hash", "op-1", _now, [invalidDatesDto]);
        Assert.True(dateResult.IsFailure);
        Assert.Equal("Rental.InvalidPeriod", dateResult.Error.Code);

        // 2. Zero quantity
        InitialRentalItemDto zeroQtyDto = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            new(2026, 10, 10),
            new(2026, 10, 15),
            "Title",
            null,
            "Loc",
            100_000m,
            200_000m,
            "grp-01",
            []);

        Result<RentalOrder> qtyResult = RentalOrder.Create(
            Guid.NewGuid(), "ORD-QTY", Guid.NewGuid(), "VND", "Pickup", "Cust", "090", "Addr", null, null, "v1", "doc", "hash", "op-2", _now, [zeroQtyDto]);
        Assert.True(qtyResult.IsFailure);
        Assert.Equal("Rental.InvalidQuantity", qtyResult.Error.Code);
    }

    [Fact]
    public void Create_RejectsDuplicateItemOrAssignmentIds()
    {
        Guid dupItemId = Guid.NewGuid();
        InitialRentalItemDto item1 = CreateSampleItemDto(100_000m, 200_000m, itemId: dupItemId);
        InitialRentalItemDto item2 = CreateSampleItemDto(100_000m, 200_000m, itemId: dupItemId);

        Result<RentalOrder> dupItemResult = RentalOrder.Create(
            Guid.NewGuid(), "ORD-DUP-ITEM", Guid.NewGuid(), "VND", "Pickup", "Cust", "090", "Addr", null, null, "v1", "doc", "hash", "op-dup", _now, [item1, item2]);
        Assert.True(dupItemResult.IsFailure);
        Assert.Equal("Rental.DuplicateId", dupItemResult.Error.Code);

        Guid dupAssignmentId = Guid.NewGuid();
        InitialRentalItemDto itemDupAssignments = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            2,
            new(2026, 10, 10),
            new(2026, 10, 15),
            "Title",
            null,
            "Loc",
            100_000m,
            200_000m,
            "grp-01",
            [
                new(dupAssignmentId, Guid.NewGuid(), Guid.NewGuid()),
                new(dupAssignmentId, Guid.NewGuid(), Guid.NewGuid()) // Duplicate assignment ID
            ]);

        Result<RentalOrder> dupAssignResult = RentalOrder.Create(
            Guid.NewGuid(), "ORD-DUP-ASS", Guid.NewGuid(), "VND", "Pickup", "Cust", "090", "Addr", null, null, "v1", "doc", "hash", "op-dup-ass", _now, [itemDupAssignments]);
        Assert.True(dupAssignResult.IsFailure);
        Assert.Equal("RentalItem.DuplicateAssignment", dupAssignResult.Error.Code);
    }

    private static InitialRentalItemDto CreateSampleItemDto(
        decimal dailyRate,
        decimal depositPerUnit,
        Guid? itemId = null)
    {
        return new InitialRentalItemDto(
            itemId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            new(2026, 10, 10),
            new(2026, 10, 15),
            "Sample Camera",
            "Sample description",
            "District 1, HCMC",
            dailyRate,
            depositPerUnit,
            "group-sample-01",
            [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())]);
    }
}
