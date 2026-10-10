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

        RentalItem item = order.Items.First();
        RentalUnitAssignment assignment = item.UnitAssignments.First();

        // 1. Direct Handover Record construction
        Guid handoverId = Guid.NewGuid();
        assignment.RecordHandover(handoverId, UnitCondition.Normal, "Handover note", [handoverMediaId]);
        HandoverRecord handover = new(
            handoverId,
            item.Id,
            now.AddDays(1),
            "Handover batch note",
            supplierId,
            now.AddDays(1),
            now.AddDays(1));
        handover.AddAssignment(assignment);
        item.SetHandoverRecord(handover);

        // 2. Direct Return Record construction
        Guid returnId = Guid.NewGuid();
        assignment.RecordReturn(returnId, UnitCondition.Damaged, "Return note", [returnMediaId], null, null);
        ReturnRecord returnRecord = new(
            returnId,
            item.Id,
            now.AddDays(5),
            "Return batch note",
            supplierId,
            now.AddDays(5));
        returnRecord.AddAssignment(assignment);
        item.AddReturnRecord(returnRecord);

        // 3. Direct Deduction Proposal and Dispute construction
        Guid propId = Guid.NewGuid();
        DeductionProposal proposal = new(
            propId,
            item.Id,
            supplierId,
            now.AddDays(5),
            now.AddDays(7));
        assignment.RecordDeduction(propId, 250_000m, "Deduction reason", [deductionMediaId]);
        proposal.AddAssignment(assignment);

        Guid disputeId = Guid.NewGuid();
        Dispute dispute = proposal.OpenDispute(
            disputeId,
            renterId,
            "Dispute reason",
            [disputeMediaId],
            now.AddDays(5).AddHours(4));

        Guid adminId = Guid.NewGuid();
        dispute.Resolve(
            200_000m,
            "Resolution reason",
            adminId,
            now.AddDays(6),
            [resolutionMediaId]);
        proposal.ApplyResolution();
        item.SetDeductionProposal(proposal);

        // 4. Direct State Transitions and Operation Receipts
        order.AddOperationReceipt(new RentalOperationReceipt(Guid.NewGuid(), orderId, "op-pay", "ConfirmPayment", "f-pay", true, null, null, now));
        order.AddOperationReceipt(new RentalOperationReceipt(Guid.NewGuid(), orderId, "op-acc", "SupplierAccept", "f-acc", true, null, null, now));
        order.AddOperationReceipt(new RentalOperationReceipt(Guid.NewGuid(), orderId, "op-resolve", "ResolveDispute", "f-res", true, null, null, now));

        order.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), orderId, itemId, "PendingPayment", "PendingSupplierConfirmation", "InvoicePaid", renterId, ActorRole.System, "Paid", now, "op-pay"));
        order.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), orderId, itemId, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", supplierId, ActorRole.Supplier, "Accepted", now, "op-acc"));
        order.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), orderId, itemId, "AwaitingHandover", "InRental", "SubmitHandover", supplierId, ActorRole.Supplier, "Handover", now, "op-hand"));
        order.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), orderId, itemId, "InRental", "PendingSettlement", "SubmitReturn", supplierId, ActorRole.Supplier, "Return", now, "op-ret"));
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
            Assert.Single(reloaded.Items);

            RentalItem reloadedItem = reloaded.Items.First();
            Assert.NotNull(reloadedItem.DeductionProposal?.Dispute);
            Assert.Equal(200_000m, reloadedItem.DeductionProposal.Dispute.FinalDeductionAmount);
            // Verify encapsulated media collections on assignment
            RentalUnitAssignment reloadedAssignment = reloadedItem.UnitAssignments.First();
            Assert.Contains(handoverMediaId, reloadedAssignment.HandoverMediaIds);
            Assert.Contains(returnMediaId, reloadedAssignment.ReturnMediaIds);
            Assert.Contains(deductionMediaId, reloadedAssignment.DeductionMediaIds);

            // Verify dispute media
            Assert.NotNull(reloadedItem.DeductionProposal?.Dispute);
            Assert.Contains(disputeMediaId, reloadedItem.DeductionProposal.Dispute.MediaIds);
            Assert.Contains(resolutionMediaId, reloadedItem.DeductionProposal.Dispute.ResolutionMediaIds);

            // Verify operation receipts
            Assert.True(reloaded.OperationReceipts.Count >= 4);
            Assert.Contains(reloaded.OperationReceipts, r => r.OperationId == "op-create" && r.Action == "CreateOrder");
            Assert.Contains(reloaded.OperationReceipts, r => r.OperationId == "op-pay" && r.Action == "ConfirmPayment");
            Assert.Contains(reloaded.OperationReceipts, r => r.OperationId == "op-acc" && r.Action == "SupplierAccept");
            Assert.Contains(reloaded.OperationReceipts, r => r.OperationId == "op-resolve" && r.Action == "ResolveDispute");

            // Verify state transitions
            Assert.True(reloaded.StateTransitions.Count >= 5);
        }
    }
}
