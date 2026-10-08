using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class RentalPersistenceAndMappingTests
{
    private static RentalServiceDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new RentalServiceDbContext(options);
    }

    [Fact]
    public async Task DbContext_PersistsAndReloads_AggregateWithReceiptsAndEncapsulatedMedia()
    {
        string dbName = $"RentalDb_{Guid.NewGuid():N}";
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        Guid unitId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid handoverMediaId = Guid.NewGuid();
        Guid returnMediaId = Guid.NewGuid();
        Guid deductionMediaId = Guid.NewGuid();
        Guid disputeMediaId = Guid.NewGuid();
        Guid resolutionMediaId = Guid.NewGuid();

        // 1. Arrange & Mutate aggregate
        InitialRentalUnitAssignmentDto assignmentDto = new(unitId, Guid.NewGuid(), Guid.NewGuid());
        InitialRentalItemDto itemDto = new(
            itemId,
            Guid.NewGuid(),
            supplierId,
            1,
            new(2026, 10, 10),
            new(2026, 10, 15),
            "Sony FX3 Camera Kit",
            "Professional Camera Kit",
            "District 1, HCMC",
            200_000m,
            500_000m,
            "group-01",
            [assignmentDto]);

        RentalOrder order = RentalOrder.Create(
            orderId,
            "ORD-PERSIST-001",
            renterId,
            "VND",
            "Pickup",
            "Customer A",
            "0909123456",
            "123 Nguyen Hue",
            "Call before delivery",
            "bank-ref-001",
            "v1.0",
            "terms/v1.0.pdf",
            "hash123456",
            "op-create",
            now,
            [itemDto]).Value;

        order.ConfirmPayment("INV-001", "PAY-001", now, TimeSpan.FromHours(24), "op-pay");
        order.SupplierAcceptItem(itemId, supplierId, "INV-CONF-001", now.AddHours(1), "op-acc");

        order.SubmitHandover(
            itemId,
            supplierId,
            now.AddDays(1),
            [new(unitId, UnitCondition.Normal, "Handover note", [handoverMediaId])],
            "Handover batch note",
            now.AddDays(1),
            "op-hand");

        order.SubmitReturn(
            itemId,
            supplierId,
            now.AddDays(5),
            [new(unitId, UnitCondition.Damaged, "Return note", [returnMediaId])],
            "Return batch note",
            now.AddDays(5),
            "op-ret");

        order.SubmitDeductionProposal(
            itemId,
            supplierId,
            [new(unitId, 250_000m, "Deduction reason", [deductionMediaId])],
            now.AddDays(5).AddHours(2),
            "op-prop");

        order.DisputeDeductionProposal(
            itemId,
            renterId,
            "Dispute reason",
            [disputeMediaId],
            now.AddDays(5).AddHours(4),
            "op-disp");

        Guid adminId = Guid.NewGuid();
        order.ResolveDispute(
            itemId,
            adminId,
            ActorRole.Admin,
            200_000m,
            "Resolution reason",
            [resolutionMediaId],
            now.AddDays(6),
            "op-resolve");

        // Save aggregate in Context 1
        await using (var context1 = CreateDbContext(dbName))
        {
            context1.RentalOrders.Add(order);
            await context1.SaveChangesAsync();
        }

        // 2. Reload aggregate in Context 2
        await using (var context2 = CreateDbContext(dbName))
        {
            RentalOrder? reloaded = await context2.RentalOrders
                .Include(o => o.TermsAcceptance)
                .Include(o => o.Items)
                    .ThenInclude(i => i.UnitAssignments)
                .Include(o => o.Items)
                    .ThenInclude(i => i.HandoverRecord)
                .Include(o => o.Items)
                    .ThenInclude(i => i.ReturnRecords)
                .Include(o => o.Items)
                    .ThenInclude(i => i.DeductionProposal)
                        .ThenInclude(p => p!.Dispute)
                .Include(o => o.StateTransitions)
                .Include(o => o.OperationReceipts)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            Assert.NotNull(reloaded);
            Assert.Equal("ORD-PERSIST-001", reloaded.OrderCode);
            Assert.Equal(RentalOrderStatus.PendingSettlement, reloaded.Status);
            Assert.Single(reloaded.Items);

            RentalItem reloadedItem = reloaded.Items.First();
            Assert.Equal("INV-CONF-001", reloadedItem.InventoryConfirmationRef);
            Assert.Equal(200_000m, reloadedItem.FinalDeductionAmount);

            // Verify encapsulated media collections on assignment
            RentalUnitAssignment assignment = reloadedItem.UnitAssignments.First();
            Assert.Contains(handoverMediaId, assignment.HandoverMediaIds);
            Assert.Contains(returnMediaId, assignment.ReturnMediaIds);
            Assert.Contains(deductionMediaId, assignment.DeductionMediaIds);

            // Verify dispute media
            Assert.NotNull(reloadedItem.DeductionProposal?.Dispute);
            Assert.Contains(disputeMediaId, reloadedItem.DeductionProposal.Dispute.MediaIds);
            Assert.Contains(resolutionMediaId, reloadedItem.DeductionProposal.Dispute.ResolutionMediaIds);

            // Verify operation receipts
            Assert.True(reloaded.OperationReceipts.Count >= 7);
            Assert.Contains(reloaded.OperationReceipts, r => r.OperationId == "op-create" && r.Action == "CreateOrder");
            Assert.Contains(reloaded.OperationReceipts, r => r.OperationId == "op-pay" && r.Action == "ConfirmPayment");
            Assert.Contains(reloaded.OperationReceipts, r => r.OperationId == "op-acc" && r.Action == "SupplierAccept");
            Assert.Contains(reloaded.OperationReceipts, r => r.OperationId == "op-resolve" && r.Action == "ResolveDispute");

            // Verify state transitions
            Assert.True(reloaded.StateTransitions.Count >= 5);
        }
    }
}
