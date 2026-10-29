using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Services.Refunds;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Refunds;

public sealed partial class RentalRefundPostgreSqlTests
{
    private static AdminRefundCommandService SyncService(RentalServiceDbContext db, FakePaymentRentalClient payment) =>
        new(new RentalOrderRepository(db), new RentalRefundRepository(db), new FakeOrderLockManager(), db,
            new FixedTimeProvider(ConfirmedNow), NullLogger<AdminRefundCommandService>.Instance, payment);

    [Fact]
    public async Task PaymentUnavailable_DoesNotWriteLocalSuccess_AndRetryUsesSameSourceOperation()
    {
        await WithSeededDatabaseAsync(async (options, seed) =>
        {
            var payment = new FakePaymentRentalClient();
            await using var db = new RentalServiceDbContext(options);
            var service = SyncService(db, payment);
            var failed = await service.ConfirmTransferAsync(seed.Excavator.Id, new("BANK-RETRY", null), Admin());
            Assert.True(failed.IsFailure);
            Assert.False(await db.RentalRefundConfirmations.AnyAsync(c => c.RentalItemId == seed.Excavator.Id));
            payment.RefundSync = r => new RentalRefundSyncResponse(true, r.Amount, r.BankTransactionRef, r.ConfirmedBy, r.ConfirmedAt);
            var retry = await service.ConfirmTransferAsync(seed.Excavator.Id, new("BANK-RETRY", null), Admin());
            Assert.True(retry.IsSuccess, retry.Error.Message);
            Assert.Equal(payment.RefundSyncRequests[0].SourceOperationKey, payment.RefundSyncRequests[1].SourceOperationKey);
            Assert.Single(await db.RentalRefundConfirmations.Where(c => c.RentalItemId == seed.Excavator.Id).ToListAsync());
        });
    }

    [Fact]
    public async Task Backfill_DryRunDoesNotConfirmPayment_ApplyRetainsOriginalEvidence_AndReplayDoesNotWriteAgain()
    {
        await WithSeededDatabaseAsync(async (options, seed) =>
        {
            await using var db = new RentalServiceDbContext(options);
            var original = await db.RentalRefundConfirmations.AsNoTracking().SingleAsync(c => c.RentalItemId == seed.Rammer.Id);
            bool completed = false;
            var payment = new FakePaymentRentalClient
            {
                RefundSync = r =>
                {
                    if (r.BankTransactionRef is not null) completed = true;
                    return new RentalRefundSyncResponse(completed, r.Amount, completed ? original.BankTransactionRef : null,
                        completed ? original.ConfirmedBy : null, completed ? ConfirmedNow : null);
                }
            };
            var service = SyncService(db, payment);
            Assert.True((await service.ReconcileAsync(seed.Rammer.Id, false)).Value);
            Assert.False(completed);
            Assert.Null(payment.RefundSyncRequests[0].BankTransactionRef);
            Assert.True((await service.ReconcileAsync(seed.Rammer.Id, true)).IsSuccess);
            Assert.True(completed);
            Assert.Equal(original.ConfirmedBy, payment.RefundSyncRequests[1].ConfirmedBy);
            Assert.Equal(original.ConfirmedAt, payment.RefundSyncRequests[1].ConfirmedAt);
            Assert.False((await service.ReconcileAsync(seed.Rammer.Id, true)).Value);
            var stored = await db.RentalRefundConfirmations.AsNoTracking().SingleAsync(c => c.RentalItemId == seed.Rammer.Id);
            Assert.Equal(original.Id, stored.Id);
            Assert.Equal(original.ConfirmedAt, stored.ConfirmedAt);
            Assert.Equal(original.Note, stored.Note);
        });
    }

    [Fact]
    public async Task Backfill_PaymentOnlyConfirmationIsMirrored_WithoutClaimingItemCompleted()
    {
        await WithSeededDatabaseAsync(async (options, seed) =>
        {
            await using var db = new RentalServiceDbContext(options);
            var paymentAdmin = Guid.NewGuid();
            var payment = new FakePaymentRentalClient
            {
                RefundSync = r => new RentalRefundSyncResponse(true, r.Amount, "PAYMENT-DIRECT", paymentAdmin, ConfirmedNow)
            };
            var service = SyncService(db, payment);
            Assert.True((await service.ReconcileAsync(seed.Mixers.Id, false)).Value);
            Assert.False(await db.RentalRefundConfirmations.AnyAsync(c => c.RentalItemId == seed.Mixers.Id));
            Assert.True((await service.ReconcileAsync(seed.Mixers.Id, true)).Value);
            var evidence = await db.RentalRefundConfirmations.SingleAsync(c => c.RentalItemId == seed.Mixers.Id);
            Assert.Equal(paymentAdmin, evidence.ConfirmedBy);
            Assert.Equal("PAYMENT-DIRECT", evidence.BankTransactionRef);
            Assert.Equal(Common.Enums.RentalItemStatus.PendingSettlement,
                (await db.RentalItems.SingleAsync(i => i.Id == seed.Mixers.Id)).Status);
        });
    }

    [Fact]
    public async Task PaymentEvidenceMismatch_IsRejected_AndPendingPaymentIsNotMirrored()
    {
        await WithSeededDatabaseAsync(async (options, seed) =>
        {
            await using var db = new RentalServiceDbContext(options);
            var payment = new FakePaymentRentalClient
            {
                RefundSync = r => new RentalRefundSyncResponse(true, r.Amount + 1, "WRONG", _admin, ConfirmedNow)
            };
            var service = SyncService(db, payment);
            Assert.True((await service.ConfirmTransferAsync(seed.Mixers.Id, new("BANK", null), Admin())).IsFailure);
            Assert.False(await db.RentalRefundConfirmations.AnyAsync(c => c.RentalItemId == seed.Mixers.Id));
            payment.RefundSync = r => new RentalRefundSyncResponse(false, r.Amount, null, null, null);
            Assert.False((await service.ReconcileAsync(seed.Mixers.Id, true)).Value);
            Assert.False(await db.RentalRefundConfirmations.AnyAsync(c => c.RentalItemId == seed.Mixers.Id));
        });
    }
}
