using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class RentalOrderPricingAndInvariantsTests
{
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly Guid _listingId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public void CreateOrder_CalculatesRentalDays_RentalAmount_AndDepositCorrectly()
    {
        DateOnly start = new(2026, 10, 10);
        DateOnly end = new(2026, 10, 15); // 6 days, both ends inclusive
        int quantity = 3;
        decimal dailyRate = 100_000m;
        decimal depositPerUnit = 500_000m;

        List<InitialRentalUnitAssignmentDto> assignments =
        [
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        ];

        InitialRentalItemDto itemDto = new(
            Guid.NewGuid(),
            _listingId,
            _supplierId,
            quantity,
            start,
            end,
            "Sony A7 IV Camera",
            "Professional Camera Kit",
            "123 Nguyen Hue, District 1, HCMC",
            dailyRate,
            depositPerUnit,
            "hold-group-001",
            assignments,
            TestListingSnapshots.Default);

        Result<RentalOrder> orderResult = RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-20261008-001",
            _renterId,
            "VND",
            "Pickup",
            "Nguyen Van A",
            "0901234567",
            "123 Nguyen Hue, District 1",
            null,
            "bank_account_123",
            "v1.0",
            "terms/v1.0.pdf",
            "hash123456",
            "op-checkout-101",
            _now,
            [itemDto]);

        Assert.True(orderResult.IsSuccess);
        RentalOrder order = orderResult.Value;

        Assert.Single(order.Items);
        RentalItem item = order.Items.First();

        Assert.Equal(6, item.RentalDays); // 10 → 15 inclusive = 6 days
        Assert.Equal(100_000m * 3 * 6, item.RentalAmount); // 1,800,000 VND
        Assert.Equal(500_000m * 3, item.DepositAmount); // 1,500,000 VND
        Assert.Equal(RentalItemStatus.PendingPayment, item.Status);
        Assert.Equal(RentalOrderStatus.PendingPayment, order.Status);
        Assert.Equal(3, item.UnitAssignments.Count);
        Assert.All(item.UnitAssignments, u => Assert.Equal(RentalUnitAssignmentStatus.Assigned, u.Status));

        // Payment deadline must be 30 minutes from creation
        Assert.Equal(_now.AddMinutes(30), order.PaymentDeadline);

        // Terms acceptance was recorded
        Assert.NotNull(order.TermsAcceptance);
        Assert.Equal(_renterId, order.TermsAcceptance.RenterId);
        Assert.Equal("v1.0", order.TermsAcceptance.TermsVersion);

        // State transition was logged for checkout
        Assert.Single(order.StateTransitions);
        RentalStateTransition initialTransition = order.StateTransitions.First();
        Assert.Equal("CreateOrder", initialTransition.Action);
        Assert.Equal(RentalOrderStatus.PendingPayment.ToString(), initialTransition.ToState);
    }

    [Fact]
    public void CreateOrder_RejectsAssignmentCountMismatch()
    {
        DateOnly start = new(2026, 10, 10);
        DateOnly end = new(2026, 10, 12);
        int quantity = 2;

        // Provide only 1 assignment instead of 2
        List<InitialRentalUnitAssignmentDto> assignments =
        [
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        ];

        InitialRentalItemDto itemDto = new(
            Guid.NewGuid(),
            _listingId,
            _supplierId,
            quantity,
            start,
            end,
            "Title",
            null,
            "Location",
            50_000m,
            100_000m,
            "group-1",
            assignments,
            TestListingSnapshots.Default);

        Result<RentalOrder> result = RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-ERR-001",
            _renterId,
            "VND",
            "Pickup",
            "John Doe",
            "0909090909",
            "Address",
            null,
            "refund-ref",
            "v1",
            "doc-ref",
            "hash",
            "op-err",
            _now,
            [itemDto]);

        Assert.True(result.IsFailure);
        Assert.Equal("RentalItem.QuantityMismatch", result.Error.Code);
    }

    [Fact]
    public void CreateOrder_RejectsInvalidCurrency()
    {
        InitialRentalItemDto itemDto = new(
            Guid.NewGuid(),
            _listingId,
            _supplierId,
            1,
            new(2026, 10, 10),
            new(2026, 10, 12),
            "Title",
            null,
            "Location",
            50_000m,
            100_000m,
            "group-1",
            [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
            TestListingSnapshots.Default);

        Result<RentalOrder> result = RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-ERR-002",
            _renterId,
            "VIETNAMDONG", // Not 3 chars
            "Pickup",
            "John Doe",
            "0909090909",
            "Address",
            null,
            "refund-ref",
            "v1",
            "doc-ref",
            "hash",
            "op-err",
            _now,
            [itemDto]);

        Assert.True(result.IsFailure);
        Assert.Equal("Money.CurrencyMismatch", result.Error.Code);
    }
}
