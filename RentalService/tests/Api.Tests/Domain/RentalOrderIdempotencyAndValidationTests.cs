using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class RentalOrderIdempotencyAndValidationTests
{
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private (RentalOrder Order, RentalItem Item, List<RentalUnitAssignment> Units) CreateReadyOrder(int quantity = 2)
    {
        List<InitialRentalUnitAssignmentDto> assignments = [];
        for (int i = 0; i < quantity; i++)
        {
            assignments.Add(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        }

        InitialRentalItemDto itemDto = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            _supplierId,
            quantity,
            new(2026, 10, 10),
            new(2026, 10, 15),
            "Camera Kit",
            null,
            "District 1",
            100_000m,
            200_000m,
            "group-01",
            assignments,
            TestListingSnapshots.Default);

        RentalOrder order = RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-IDEMP-001",
            _renterId,
            "VND",
            "Pickup",
            "Recipient",
            "0909090909",
            "Address",
            null,
            "refund-ref",
            "v1.0",
            "terms-doc",
            "terms-hash",
            "op-create",
            _now,
            [itemDto]).Value;

        order.ConfirmPayment("INV-001", "PAY-001", _now, TimeSpan.FromHours(24), "op-pay");
        order.SupplierAcceptItem(order.Items.First().Id, _supplierId, "INV-CONF-01", _now.AddHours(1), "op-acc");

        RentalItem item = order.Items.First();
        return (order, item, item.UnitAssignments.ToList());
    }

    [Fact]
    public void IdempotentRetry_WithSamePayload_ReturnsSuccessWithoutVersionOrTransitionIncrement()
    {
        InitialRentalUnitAssignmentDto a1 = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        InitialRentalItemDto itemDto = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            _supplierId,
            1,
            new(2026, 10, 10),
            new(2026, 10, 15),
            "Camera",
            null,
            "Address",
            100_000m,
            200_000m,
            "grp-1",
            [a1],
            TestListingSnapshots.Default);

        RentalOrder order = RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-IDEMP-RETRY",
            _renterId,
            "VND",
            "Pickup",
            "Recipient",
            "0909090909",
            "Address",
            null,
            "refund-ref",
            "v1.0",
            "terms-doc",
            "terms-hash",
            "op-create-retry",
            _now,
            [itemDto]).Value;

        // First call
        Result pay1 = order.ConfirmPayment("INV-100", "PAY-100", _now, TimeSpan.FromHours(24), "op-pay-100");
        Assert.True(pay1.IsSuccess);
        long versionAfterFirst = order.Version;
        int transitionsCountAfterFirst = order.StateTransitions.Count;
        int receiptsCountAfterFirst = order.OperationReceipts.Count;

        // Retry same call with same payload & operationId
        Result payRetry = order.ConfirmPayment("INV-100", "PAY-100", _now, TimeSpan.FromHours(24), "op-pay-100");
        Assert.True(payRetry.IsSuccess);
        Assert.Equal(versionAfterFirst, order.Version);
        Assert.Equal(transitionsCountAfterFirst, order.StateTransitions.Count);
        Assert.Equal(receiptsCountAfterFirst, order.OperationReceipts.Count);
    }

    [Fact]
    public void IdempotentRetry_WithDifferentPayload_ReturnsOperationConflict()
    {
        InitialRentalUnitAssignmentDto a1 = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        InitialRentalItemDto itemDto = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            _supplierId,
            1,
            new(2026, 10, 10),
            new(2026, 10, 15),
            "Camera",
            null,
            "Address",
            100_000m,
            200_000m,
            "grp-1",
            [a1],
            TestListingSnapshots.Default);

        RentalOrder order = RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-IDEMP-CONFLICT",
            _renterId,
            "VND",
            "Pickup",
            "Recipient",
            "0909090909",
            "Address",
            null,
            "refund-ref",
            "v1.0",
            "terms-doc",
            "terms-hash",
            "op-create-conf",
            _now,
            [itemDto]).Value;

        // First call succeeds
        order.ConfirmPayment("INV-100", "PAY-100", _now, TimeSpan.FromHours(24), "op-same-id");

        // Same operationId but different invoiceRef
        Result conflictRes = order.ConfirmPayment("INV-DIFFERENT", "PAY-100", _now, TimeSpan.FromHours(24), "op-same-id");
        Assert.True(conflictRes.IsFailure);
        Assert.Equal("Operation.Conflict", conflictRes.Error.Code);
    }

    [Fact]
    public void SubmitHandover_WithDuplicateAssignmentId_FailsWithDuplicateAssignment()
    {
        var (order, item, units) = CreateReadyOrder(2);

        // Same unit ID repeated twice
        List<HandoverObservationDto> observations =
        [
            new(units[0].Id, UnitCondition.Normal),
            new(units[0].Id, UnitCondition.Normal)
        ];

        Result result = order.SubmitHandover(item.Id, _supplierId, _now.AddDays(1), observations, null, _now.AddDays(1), "op-hand-dup");
        Assert.True(result.IsFailure);
        Assert.Equal("RentalItem.DuplicateAssignment", result.Error.Code);
    }

    [Fact]
    public void SubmitReturn_WithDuplicateAssignmentId_FailsWithDuplicateAssignment()
    {
        var (order, item, units) = CreateReadyOrder(2);
        order.SubmitHandover(item.Id, _supplierId, _now.AddDays(1), units.Select(u => new HandoverObservationDto(u.Id, UnitCondition.Normal)).ToList(), null, _now.AddDays(1), "op-hand");

        // Same unit returned twice in the same batch
        List<ReturnObservationDto> observations =
        [
            new(units[0].Id, UnitCondition.Normal),
            new(units[0].Id, UnitCondition.Normal)
        ];

        Result result = order.SubmitReturn(item.Id, _supplierId, _now.AddDays(5), observations, null, _now.AddDays(5), "op-ret-dup");
        Assert.True(result.IsFailure);
        Assert.Equal("RentalItem.DuplicateAssignment", result.Error.Code);
    }

    [Fact]
    public void SubmitDeductionProposal_WithDuplicateAssignmentId_FailsWithDuplicateAssignment()
    {
        var (order, item, units) = CreateReadyOrder(2);
        order.SubmitHandover(item.Id, _supplierId, _now.AddDays(1), units.Select(u => new HandoverObservationDto(u.Id, UnitCondition.Normal)).ToList(), null, _now.AddDays(1), "op-hand");
        order.SubmitReturn(item.Id, _supplierId, _now.AddDays(5), units.Select(u => new ReturnObservationDto(u.Id, UnitCondition.Damaged)).ToList(), null, _now.AddDays(5), "op-ret");

        // Duplicate line for the same unit
        List<DeductionLineDto> lines =
        [
            new(units[0].Id, 50_000m, "Reason 1"),
            new(units[0].Id, 50_000m, "Reason 2")
        ];

        Result result = order.SubmitDeductionProposal(item.Id, _supplierId, lines, _now.AddDays(5).AddHours(2), "op-prop-dup");
        Assert.True(result.IsFailure);
        Assert.Equal("RentalItem.DuplicateAssignment", result.Error.Code);
    }

    [Fact]
    public void CreateOrder_WithNegativePriceOrDeposit_FailsWithNegativeAmount()
    {
        InitialRentalUnitAssignmentDto a1 = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        InitialRentalItemDto negativeDailyRateItem = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            _supplierId,
            1,
            new(2026, 10, 10),
            new(2026, 10, 15),
            "Camera",
            null,
            "Address",
            -100_000m,
            200_000m,
            "grp-1",
            [a1],
            TestListingSnapshots.Default);

        Result<RentalOrder> failDailyRate = RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-NEG-RATE",
            _renterId,
            "VND",
            "Pickup",
            "Recipient",
            "0909090909",
            "Address",
            null,
            "refund-ref",
            "v1.0",
            "terms-doc",
            "terms-hash",
            "op-create-neg-rate",
            _now,
            [negativeDailyRateItem]);

        Assert.True(failDailyRate.IsFailure);
        Assert.Equal("Money.NegativeAmount", failDailyRate.Error.Code);
    }

    [Fact]
    public void CreateOrder_WithDuplicateItemIdsOrAssetIds_Fails()
    {
        Guid duplicateItemId = Guid.NewGuid();
        Guid duplicateAssetId = Guid.NewGuid();

        InitialRentalItemDto item1 = new(
            duplicateItemId,
            Guid.NewGuid(),
            _supplierId,
            1,
            new(2026, 10, 10),
            new(2026, 10, 15),
            "Camera 1",
            null,
            "Address",
            100_000m,
            200_000m,
            "grp-1",
            [new(Guid.NewGuid(), duplicateAssetId, Guid.NewGuid())],
            TestListingSnapshots.Default);

        InitialRentalItemDto item2 = new(
            duplicateItemId, // Duplicate item ID!
            Guid.NewGuid(),
            _supplierId,
            1,
            new(2026, 10, 10),
            new(2026, 10, 15),
            "Camera 2",
            null,
            "Address",
            100_000m,
            200_000m,
            "grp-2",
            [new(Guid.NewGuid(), duplicateAssetId, Guid.NewGuid())],
            TestListingSnapshots.Default);

        Result<RentalOrder> duplicateResult = RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-DUP-ITEM",
            _renterId,
            "VND",
            "Pickup",
            "Recipient",
            "0909090909",
            "Address",
            null,
            "refund-ref",
            "v1.0",
            "terms-doc",
            "terms-hash",
            "op-create-dup",
            _now,
            [item1, item2]);

        Assert.True(duplicateResult.IsFailure);
        Assert.Equal("Rental.DuplicateId", duplicateResult.Error.Code);
    }
}
