using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class RentalOrderFulfilmentAndReturnsTests
{
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private (RentalOrder Order, RentalItem Item, List<RentalUnitAssignment> Units) CreateReadyToHandoverOrder(int quantity = 2)
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
            assignments);

        RentalOrder order = RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-FULFIL-001",
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
            "op-fulfil",
            _now,
            [itemDto]).Value;

        order.ConfirmPayment("INV-001", "PAY-001", _now, TimeSpan.FromHours(24), "op-pay");
        order.SupplierAcceptItem(order.Items.First().Id, _supplierId, _now.AddHours(1), "op-acc");

        RentalItem item = order.Items.First();
        return (order, item, item.UnitAssignments.ToList());
    }

    [Fact]
    public void SubmitHandover_MovesItemAndOrderToInRental()
    {
        var (order, item, units) = CreateReadyToHandoverOrder(2);

        List<HandoverObservationDto> observations =
        [
            new(units[0].Id, UnitCondition.Normal, "Good condition"),
            new(units[1].Id, UnitCondition.Normal, "Minor cosmetic scratch")
        ];

        Result result = order.SubmitHandover(
            item.Id,
            _supplierId,
            _now.AddDays(1),
            observations,
            "Handed over in person",
            _now.AddDays(1),
            "op-handover");

        Assert.True(result.IsSuccess);
        Assert.Equal(RentalItemStatus.InRental, item.Status);
        Assert.Equal(RentalOrderStatus.InRental, order.Status);
        Assert.NotNull(item.HandoverRecord);
        Assert.All(item.UnitAssignments, u => Assert.Equal(RentalUnitAssignmentStatus.HandedOver, u.Status));
    }

    [Fact]
    public void SubmitHandover_RejectsIncompleteObservations()
    {
        var (order, item, units) = CreateReadyToHandoverOrder(2);

        // Submit observation for only 1 unit out of 2
        List<HandoverObservationDto> incompleteObs =
        [
            new(units[0].Id, UnitCondition.Normal, "Good condition")
        ];

        Result result = order.SubmitHandover(
            item.Id,
            _supplierId,
            _now.AddDays(1),
            incompleteObs,
            null,
            _now.AddDays(1),
            "op-handover-fail");

        Assert.True(result.IsFailure);
        Assert.Equal("RentalItem.IncompleteHandover", result.Error.Code);
    }

    [Fact]
    public void SubmitReturn_PartialReturn_KeepsItemInRental()
    {
        var (order, item, units) = CreateReadyToHandoverOrder(2);
        order.SubmitHandover(
            item.Id,
            _supplierId,
            _now.AddDays(1),
            [
                new(units[0].Id, UnitCondition.Normal),
                new(units[1].Id, UnitCondition.Normal)
            ],
            null,
            _now.AddDays(1),
            "op-handover");

        // Return only unit 0
        List<ReturnObservationDto> partialReturn =
        [
            new(units[0].Id, UnitCondition.Normal, "Returned normal")
        ];

        Result retResult = order.SubmitReturn(
            item.Id,
            _supplierId,
            _now.AddDays(3),
            partialReturn,
            "Early return of 1 item",
            _now.AddDays(3),
            "op-ret-1");

        Assert.True(retResult.IsSuccess);
        Assert.Equal(RentalItemStatus.InRental, item.Status);
        Assert.Equal(RentalOrderStatus.InRental, order.Status);
        Assert.False(item.AreAllUnitsReturned);

        RentalUnitAssignment returnedUnit = item.UnitAssignments.First(u => u.Id == units[0].Id);
        RentalUnitAssignment remainingUnit = item.UnitAssignments.First(u => u.Id == units[1].Id);

        Assert.Equal(RentalUnitAssignmentStatus.ReturnedNormal, returnedUnit.Status);
        Assert.Equal(RentalUnitAssignmentStatus.HandedOver, remainingUnit.Status);
    }

    [Fact]
    public void SubmitReturn_AllReturnedNormal_CompletesReturnsWithoutDamage()
    {
        var (order, item, units) = CreateReadyToHandoverOrder(1);
        order.SubmitHandover(item.Id, _supplierId, _now.AddDays(1), [new(units[0].Id, UnitCondition.Normal)], null, _now.AddDays(1), "op-hand");

        Result retResult = order.SubmitReturn(
            item.Id,
            _supplierId,
            _now.AddDays(5),
            [new(units[0].Id, UnitCondition.Normal)],
            "Returned in great shape",
            _now.AddDays(5),
            "op-ret");

        Assert.True(retResult.IsSuccess);
        Assert.True(item.AreAllUnitsReturned);
        Assert.False(item.HasDamagedUnits);
        Assert.NotNull(item.ReturnsCompletedAt);
        // Item remains ready for completion
        Assert.Equal(RentalItemStatus.InRental, item.Status);
    }

    [Fact]
    public void SubmitReturn_WithDamage_TransitionsItemToPendingSettlement()
    {
        var (order, item, units) = CreateReadyToHandoverOrder(1);
        order.SubmitHandover(item.Id, _supplierId, _now.AddDays(1), [new(units[0].Id, UnitCondition.Normal)], null, _now.AddDays(1), "op-hand");

        DateTimeOffset returnTime = _now.AddDays(5);
        Result retResult = order.SubmitReturn(
            item.Id,
            _supplierId,
            returnTime,
            [new(units[0].Id, UnitCondition.Damaged, "Lens cracked", [Guid.NewGuid()])],
            "Damaged on return",
            returnTime,
            "op-ret-dam");

        Assert.True(retResult.IsSuccess);
        Assert.True(item.AreAllUnitsReturned);
        Assert.True(item.HasDamagedUnits);
        Assert.Equal(RentalItemStatus.PendingSettlement, item.Status);
        Assert.Equal(RentalOrderStatus.PendingSettlement, order.Status);
        // Deduction deadline must be exactly 48 hours from ReturnsCompletedAt
        Assert.Equal(returnTime.AddHours(48), item.DeductionDeadline);
    }
}
