using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Returns;

[Collection("PostgreSql")]
public class RentalReturnMigrationPostgreSqlTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MigratePendingHttpReturnReceipts_MigratesToManualReconciliation_PreservesEvidence_AndIdempotent()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var options = PostgresTestDatabaseHelper.CreateOptions(connString);
        try
        {
            Guid orderId = Guid.NewGuid();
            Guid itemId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid supplierId = Guid.NewGuid();

            // 1. Migrate up to 20261015050000_AddOutboxSequenceAndCheckoutOperation
            await using (var ctx = new RentalServiceDbContext(options))
            {
                var migrator = ctx.Database.GetService<IMigrator>();
                await migrator.MigrateAsync("20261015050000_AddOutboxSequenceAndCheckoutOperation");
            }

            // 2. Seed orders and receipts (HTTP returns pending/processing/failed, Succeeded, and Kafka returns)
            await using (var ctx = new RentalServiceDbContext(options))
            {
                var order = PostgresTestDatabaseHelper.CreateOrderWithItem(
                    orderId, itemId, supplierId, 1, FixedNow.AddDays(-2));

                // HTTP MarkAssetsReturned - Pending
                var rPending = new RentalOperationReceipt(
                    Guid.NewGuid(), orderId, "op:return:pending", "MarkAssetsReturned",
                    "fp-ret-pending", false, null, null, FixedNow.AddHours(-3),
                    status: RentalOperationReceipt.StatusPending, retryCount: 1,
                    targetItemId: itemId, payloadJson: "{\"units\":[\"u1\"]}",
                    apiResponseJson: "{\"evidence\":\"ev1\"}");
                order.AddOperationReceipt(rPending);

                // HTTP BlockDamagedAssets - Processing
                var rProc = new RentalOperationReceipt(
                    Guid.NewGuid(), orderId, "op:damage:proc", "BlockDamagedAssets",
                    "fp-dam-proc", false, null, null, FixedNow.AddHours(-2),
                    status: RentalOperationReceipt.StatusProcessing, retryCount: 2,
                    targetItemId: itemId, payloadJson: "{\"damageReason\":\"cracked lens\"}",
                    apiResponseJson: "{\"evidence\":\"ev2\"}");
                order.AddOperationReceipt(rProc);

                // HTTP MarkAssetsReturned - Failed
                var rFailed = new RentalOperationReceipt(
                    Guid.NewGuid(), orderId, "op:return:failed", "MarkAssetsReturned",
                    "fp-ret-failed", false, "HTTP_TIMEOUT", "Connection reset", FixedNow.AddHours(-1),
                    status: RentalOperationReceipt.StatusFailed, retryCount: 3,
                    targetItemId: itemId, payloadJson: "{\"units\":[\"u2\"]}");
                order.AddOperationReceipt(rFailed);

                // HTTP MarkAssetsReturned - Succeeded (must NOT be migrated)
                var rSucc = new RentalOperationReceipt(
                    Guid.NewGuid(), orderId, "op:return:succ", "MarkAssetsReturned",
                    "fp-ret-succ", true, null, null, FixedNow.AddHours(-4),
                    status: RentalOperationReceipt.StatusSucceeded, retryCount: 1,
                    targetItemId: itemId, payloadJson: "{\"units\":[\"u3\"]}");
                order.AddOperationReceipt(rSucc);

                // Kafka ApplyReturnedUnits - Pending (must NOT be migrated)
                var rKafka = new RentalOperationReceipt(
                    Guid.NewGuid(), orderId, "op:return:kafka", "ApplyReturnedUnits",
                    "fp-ret-kafka", false, null, null, FixedNow.AddMinutes(-30),
                    status: RentalOperationReceipt.StatusPending, retryCount: 0,
                    targetItemId: itemId, payloadJson: "{\"units\":[\"u4\"]}");
                order.AddOperationReceipt(rKafka);

                ctx.RentalOrders.Add(order);
                await ctx.SaveChangesAsync();
            }

            // 3. Migrate forward to latest migration
            await using (var ctx = new RentalServiceDbContext(options))
            {
                var migrator = ctx.Database.GetService<IMigrator>();
                await migrator.MigrateAsync();
            }

            // 4. Verify data transformation
            await using (var verifyCtx = new RentalServiceDbContext(options))
            {
                var receipts = await verifyCtx.RentalOperationReceipts
                    .OrderBy(r => r.Fingerprint)
                    .ToListAsync();

                // HTTP return pending -> MANUAL_RECONCILIATION_REQUIRED
                var mPending = receipts.Single(r => r.Fingerprint == "fp-ret-pending");
                Assert.Equal(RentalOperationReceipt.StatusManualReconciliationRequired, mPending.Status);
                Assert.False(mPending.IsSuccess);
                Assert.Equal("{\"units\":[\"u1\"]}", mPending.PayloadJson);
                Assert.Equal("{\"evidence\":\"ev1\"}", mPending.ApiResponseJson);
                Assert.Equal(1, mPending.RetryCount);

                // HTTP damage processing -> MANUAL_RECONCILIATION_REQUIRED
                var mProc = receipts.Single(r => r.Fingerprint == "fp-dam-proc");
                Assert.Equal(RentalOperationReceipt.StatusManualReconciliationRequired, mProc.Status);
                Assert.False(mProc.IsSuccess);
                Assert.Equal("{\"damageReason\":\"cracked lens\"}", mProc.PayloadJson);
                Assert.Equal("{\"evidence\":\"ev2\"}", mProc.ApiResponseJson);
                Assert.Equal(2, mProc.RetryCount);

                // HTTP return failed -> MANUAL_RECONCILIATION_REQUIRED
                var mFailed = receipts.Single(r => r.Fingerprint == "fp-ret-failed");
                Assert.Equal(RentalOperationReceipt.StatusManualReconciliationRequired, mFailed.Status);
                Assert.False(mFailed.IsSuccess);
                Assert.Equal("HTTP_TIMEOUT", mFailed.ErrorCode);

                // HTTP return succeeded -> stays Succeeded
                var mSucc = receipts.Single(r => r.Fingerprint == "fp-ret-succ");
                Assert.Equal(RentalOperationReceipt.StatusSucceeded, mSucc.Status);
                Assert.True(mSucc.IsSuccess);

                // Kafka return pending -> stays Pending
                var mKafka = receipts.Single(r => r.Fingerprint == "fp-ret-kafka");
                Assert.Equal(RentalOperationReceipt.StatusPending, mKafka.Status);
                Assert.False(mKafka.IsSuccess);
            }

            // 5. Idempotent re-run
            await using (var ctx = new RentalServiceDbContext(options))
            {
                await ctx.Database.MigrateAsync();
            }

            // 6. Downgrade must fail
            await using (var ctx = new RentalServiceDbContext(options))
            {
                var migrator = ctx.Database.GetService<IMigrator>();
                var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                    migrator.MigrateAsync("20261015050000_AddOutboxSequenceAndCheckoutOperation"));
                Assert.Contains("MANUAL_RECONCILIATION_REQUIRED", ex.Message);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }
}
