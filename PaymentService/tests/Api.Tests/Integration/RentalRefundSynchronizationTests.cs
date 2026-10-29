using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class RentalRefundSynchronizationTests
{
    [Fact]
    public async Task ReadOnlyProbe_ThenLegacyConfirmation_UsesFinancialWorkflow_AndReplaysOnce()
    {
        await using var h = await FinancialWorkflowHarness.CreateAsync();
        var invoice = await h.SeedAsync();
        var settlementRequest = FinancialWorkflowHarness.SettlementRequest(invoice);
        await using (var db = new PaymentServiceDbContext(h.PaymentOptions))
            Assert.True((await h.Settlement(db).SettleAsync(settlementRequest, default)).IsSuccess);
        await h.ConfirmAsync();
        var admin = Guid.NewGuid();
        var request = new RentalRefundSyncRequest(invoice.RentalOrderId, invoice.Items.Single().RentalItemId,
            settlementRequest.OperationKey, 70_000, null, null, null, null);
        await using (var db = new PaymentServiceDbContext(h.PaymentOptions))
        {
            var service = h.Refund(db);
            var probe = await service.SynchronizeRentalAsync(request, default);
            Assert.True(probe.IsSuccess, probe.Error.Message);
            Assert.False(probe.Value.Completed);
            Assert.Equal(ManualRefundStatus.PendingManualRefund, (await db.ManualRefundRequirements.SingleAsync()).Status);
            request = request with
            {
                BankTransactionRef = "LEGACY-BANK",
                ConfirmedBy = admin,
                ConfirmedAt = DateTimeOffset.UtcNow.AddHours(-1),
                Note = "Original admin confirmation"
            };
            var applied = await service.SynchronizeRentalAsync(request, default);
            Assert.True(applied.IsSuccess, applied.Error.Message);
            Assert.True(applied.Value.Completed);
            Assert.Equal(admin, applied.Value.ConfirmedBy);
        }
        await using (var db = new PaymentServiceDbContext(h.PaymentOptions))
        {
            var replay = await h.Refund(db).SynchronizeRentalAsync(request, default);
            Assert.True(replay.IsSuccess, replay.Error.Message);
            Assert.True(replay.Value.Completed);
            Assert.Equal(1, await db.OperationReceipts.CountAsync(x => x.OperationType == "ConfirmManualRefund"));
            Assert.Equal(1, await db.OutboxMessages.CountAsync(x => x.MessageType == "RentalSettled"));
            Assert.Equal(SettlementState.Completed, (await db.RentalItemSettlements.SingleAsync()).Status);
            var changedBank = await h.Refund(db).SynchronizeRentalAsync(request with { BankTransactionRef = "OTHER" }, default);
            Assert.Equal("Payment.RefundEvidenceConflict", changedBank.Error.Code);
        }
    }

    [Fact]
    public async Task WrongOrderItemAmountOrActor_CannotConfirm_AndDirectPaymentEvidenceCanBeRead()
    {
        await using var h = await FinancialWorkflowHarness.CreateAsync();
        var invoice = await h.SeedAsync();
        var source = FinancialWorkflowHarness.SettlementRequest(invoice);
        await using (var db = new PaymentServiceDbContext(h.PaymentOptions))
            Assert.True((await h.Settlement(db).SettleAsync(source, default)).IsSuccess);
        await h.ConfirmAsync();
        await using var context = new PaymentServiceDbContext(h.PaymentOptions);
        var service = h.Refund(context);
        var request = new RentalRefundSyncRequest(invoice.RentalOrderId, invoice.Items.Single().RentalItemId,
            source.OperationKey, 70_000, "BANK", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1), null);
        Assert.Equal("Payment.RefundSyncMismatch", (await service.SynchronizeRentalAsync(request with { Amount = 1 }, default)).Error.Code);
        Assert.Equal("Payment.RefundSyncMismatch", (await service.SynchronizeRentalAsync(request with { RentalOrderId = Guid.NewGuid() }, default)).Error.Code);
        Assert.Equal("Payment.RefundSyncMismatch", (await service.SynchronizeRentalAsync(request with { RentalItemId = Guid.NewGuid() }, default)).Error.Code);
        Assert.Equal("Payment.InvalidRefundEvidence", (await service.SynchronizeRentalAsync(request with { ConfirmedBy = null }, default)).Error.Code);
        Assert.Equal(ManualRefundStatus.PendingManualRefund, (await context.ManualRefundRequirements.SingleAsync()).Status);
        var id = await context.ManualRefundRequirements.Select(x => x.Id).SingleAsync();
        var direct = await service.CompleteAsync(id, new("DIRECT-BANK", "direct-confirm", "Admin confirmed transfer"), request.ConfirmedBy!.Value, default);
        Assert.True(direct.IsSuccess, direct.Error.Message);
        var mirror = await service.SynchronizeRentalAsync(request with { BankTransactionRef = null }, default);
        Assert.True(mirror.IsSuccess, mirror.Error.Message);
        Assert.Equal("DIRECT-BANK", mirror.Value.BankTransactionRef);
        Assert.Equal(direct.Value.CompletedBy, mirror.Value.ConfirmedBy);
        Assert.Equal(direct.Value.CompletedAt.ToUnixTimeMilliseconds(), mirror.Value.ConfirmedAt!.Value.ToUnixTimeMilliseconds());
    }
}
