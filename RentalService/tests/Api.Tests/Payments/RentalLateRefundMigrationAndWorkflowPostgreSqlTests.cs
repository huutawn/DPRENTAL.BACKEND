#pragma warning disable CA1822
using System.Text.Json;
using CreateInvoiceResponse = global::Dprental.RentalService.Application.DTOs.Payment.CreateInvoiceResponse;
using CreateInvoiceRequest = global::Dprental.RentalService.Application.DTOs.Payment.CreateInvoiceRequest;
using CancelInvoiceRequest = global::Dprental.RentalService.Application.DTOs.Payment.CancelInvoiceRequest;
using CancelInvoiceResponse = global::Dprental.RentalService.Application.DTOs.Payment.CancelInvoiceResponse;
using RequestRefundRequest = global::Dprental.RentalService.Application.DTOs.Payment.RequestRefundRequest;
using RequestRefundResponse = global::Dprental.RentalService.Application.DTOs.Payment.RequestRefundResponse;
using SettleRentalRequest = global::Dprental.RentalService.Application.DTOs.Payment.SettleRentalRequest;
using SettleRentalResponse = global::Dprental.RentalService.Application.DTOs.Payment.SettleRentalResponse;
using AdminReplayRequest = global::Dprental.RentalService.Application.DTOs.Operations.AdminReplayRequest;
using RentalServiceDbContext = global::Dprental.RentalService.Domain.Repositories.RentalServiceDbContext;
using InvoicePaidPayload = global::Dprental.RentalService.Application.Kafka.Events.InvoicePaidPayload;
using IPaymentWorkflowService = global::Dprental.RentalService.Application.IServices.Payments.IPaymentWorkflowService;
using RentalOperationExecutor = global::Dprental.RentalService.Application.Services.Operations.RentalOperationExecutor;
using ApplyReturnedUnitsCommandPayload = global::Dprental.RentalService.Application.Kafka.Contracts.ApplyReturnedUnitsCommandPayload;
using PaymentWorkflowService = global::Dprental.RentalService.Application.Services.Payments.PaymentWorkflowService;
using IRentalOperationExecutor = global::Dprental.RentalService.Application.IServices.Operations.IRentalOperationExecutor;
using Dprental.RentalService.Api.Controllers;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Background;
using Dprental.RentalService.Application.Clients;
using global::Dprental.RentalService.Application.DTOs.Inventory;
using global::Dprental.RentalService.Application.DTOs.Operations;
using global::Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.Supplier;
using global::Dprental.RentalService.Application.Kafka.Contracts;
using global::Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Security.Jwt;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Payments;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Payments;

public sealed class RentalLateRefundMigrationAndWorkflowPostgreSqlTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static RentalOrder CreateLateOrder(Guid orderId, Guid renterId, Guid itemId, decimal dailyRate = 200_000m, decimal depositPerUnit = 500_000m)
    {
        var assignments = new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) };
        var item = new InitialRentalItemDto(
            itemId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            new DateOnly(2026, 10, 15),
            new DateOnly(2026, 10, 18),
            "Camera Sony",
            null,
            "Pickup",
            dailyRate,
            depositPerUnit,
            "grp-01",
            assignments,
            TestListingSnapshots.Default);

        var order = RentalOrder.Create(
            orderId,
            $"ORD-{orderId:N}"[..20],
            renterId,
            "VND",
            "Pickup",
            "Customer",
            "0909123456",
            "Address",
            null,
            "v1.0",
            "terms.pdf",
            "hash-1",
            $"chk-op-{orderId:N}",
            FixedNow.AddMinutes(-40),
            [item]).Value;
        order.AttachInvoice("INV-001", FixedNow.AddMinutes(-10));
        return order;
    }

    [Fact]
    public async Task Migration_UpgradesFromPriorMigration_MigratesIncompleteLateRefundReceipts_PreservesPayloadAndAudit()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);

            // 1. Migrate up to 20261015004900_MakeRentalItemListingDetailsOptional (migration immediately before late-refund migration;
            // dữ liệu mẫu ghi bằng model hiện tại nên schema phải đã có đơn con)
            await using (var ctx = new RentalServiceDbContext(options))
            {
                var migrator = ctx.Database.GetService<IMigrator>();
                await migrator.MigrateAsync("20261015004900_MakeRentalItemListingDetailsOptional");
            }

            Guid orderLateId = Guid.NewGuid();
            Guid orderNormalId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid itemLateId = Guid.NewGuid();
            Guid itemNormalId = Guid.NewGuid();

            // 2. Seed data before the late-refund migration
            await using (var ctx = new LegacySchemaRentalServiceDbContext(options))
            {
                var lateOrder = CreateLateOrder(orderLateId, renterId, itemLateId);
                lateOrder.MarkPaymentExpired(FixedNow.AddMinutes(-10), "op-expire");
                lateOrder.MarkAsLatePayment("INV-001", "PAY-LATE", FixedNow.AddMinutes(-15), FixedNow.AddMinutes(-5));

                // Late order receipts:
                // Receipt 1: RequestRefund (Pending) -> MUST become MANUAL_REFUND_REQUIRED
                var r1 = new RentalOperationReceipt(
                    Guid.NewGuid(), orderLateId, RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{orderLateId}:{itemLateId}"), "RequestRefund",
                    "fp-refund-pending", false, null, null, FixedNow.AddMinutes(-5),
                    status: RentalOperationReceipt.StatusPending, retryCount: 0,
                    targetItemId: itemLateId,
                    prerequisiteOperationId: RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{orderLateId}:{itemLateId}"),
                    payloadJson: "{\"amount\":500000}");
                lateOrder.AddOperationReceipt(r1);

                // Receipt 2: RequestRefund (Processing) -> MUST become MANUAL_REFUND_REQUIRED
                var r2 = new RentalOperationReceipt(
                    Guid.NewGuid(), orderLateId, RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"proc:{itemLateId}"), "RequestRefund",
                    "fp-refund-proc", false, null, null, FixedNow.AddMinutes(-5),
                    status: RentalOperationReceipt.StatusProcessing, retryCount: 1,
                    lastAttemptedAt: FixedNow.AddMinutes(-3), nextRetryAt: FixedNow.AddMinutes(5),
                    targetItemId: itemLateId,
                    payloadJson: "{\"amount\":500000}");
                lateOrder.AddOperationReceipt(r2);

                // Receipt 3: RequestRefund (Failed) -> MUST become MANUAL_REFUND_REQUIRED
                var r3 = new RentalOperationReceipt(
                    Guid.NewGuid(), orderLateId, RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"failed:{itemLateId}"), "RequestRefund",
                    "fp-refund-failed", false, "TIMEOUT", "Failed attempt", FixedNow.AddMinutes(-5),
                    status: RentalOperationReceipt.StatusFailed, retryCount: 2,
                    lastAttemptedAt: FixedNow.AddMinutes(-2),
                    targetItemId: itemLateId,
                    payloadJson: "{\"amount\":500000}");
                lateOrder.AddOperationReceipt(r3);

                // Receipt 4: RequestRefund (Succeeded) -> MUST stay Succeeded
                var r4 = new RentalOperationReceipt(
                    Guid.NewGuid(), orderLateId, RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"succ:{itemLateId}"), "RequestRefund",
                    "fp-refund-succ", true, null, null, FixedNow.AddMinutes(-5),
                    status: RentalOperationReceipt.StatusSucceeded, retryCount: 1,
                    completedAt: FixedNow.AddMinutes(-1),
                    targetItemId: itemLateId,
                    payloadJson: "{\"amount\":500000}",
                    apiResponseJson: "{\"refundRef\":\"REF-01\"}");
                lateOrder.AddOperationReceipt(r4);

                // Receipt 5: ReleaseAllocation (Pending) -> MUST stay Pending
                var r5 = new RentalOperationReceipt(
                    Guid.NewGuid(), orderLateId, RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{orderLateId}:{itemLateId}"), "ReleaseAllocation",
                    "fp-release", false, null, null, FixedNow.AddMinutes(-5),
                    status: RentalOperationReceipt.StatusPending, retryCount: 0,
                    targetItemId: itemLateId,
                    payloadJson: "{\"hold\":\"group-1\"}");
                lateOrder.AddOperationReceipt(r5);

                // Order B: NOT a late payment order (LatePaymentRefundRequired = false)
                var normalOrder = CreateLateOrder(orderNormalId, renterId, itemNormalId);
                // Receipt 6: RequestRefund (Pending) on non-late order -> MUST stay Pending
                var r6 = new RentalOperationReceipt(
                    Guid.NewGuid(), orderNormalId, RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{orderNormalId}:{itemNormalId}"), "RequestRefund",
                    "fp-normal-refund", false, null, null, FixedNow.AddMinutes(-5),
                    status: RentalOperationReceipt.StatusPending, retryCount: 0,
                    targetItemId: itemNormalId,
                    payloadJson: "{\"amount\":500000}");
                normalOrder.AddOperationReceipt(r6);

                ctx.RentalOrders.AddRange(lateOrder, normalOrder);
                await ctx.SaveChangesAsync();
            }

            // 3. Migrate forward to latest migration (applies MigrateIncompleteLateRefundReceiptsToManual)
            await using (var ctx = new RentalServiceDbContext(options))
            {
                var migrator = ctx.Database.GetService<IMigrator>();
                await migrator.MigrateAsync();
            }

            // 4. Assert data transformation
            await using (var verifyCtx = new RentalServiceDbContext(options))
            {
                var receipts = await verifyCtx.RentalOperationReceipts
                    .OrderBy(r => r.Fingerprint)
                    .ToListAsync();

                // The legacy migration marks incomplete late refunds for manual review;
                // the current migration reopens only late-payment refunds as retryable.
                var migrated1 = receipts.Single(r => r.Fingerprint == "fp-refund-pending");
                Assert.Equal(RentalOperationReceipt.StatusPending, migrated1.Status);
                Assert.False(migrated1.IsSuccess);
                Assert.Equal("{\"amount\":500000}", migrated1.PayloadJson);

                var migrated2 = receipts.Single(r => r.Fingerprint == "fp-refund-proc");
                Assert.Equal(RentalOperationReceipt.StatusPending, migrated2.Status);
                Assert.Equal(1, migrated2.RetryCount); // Retry history preserved

                var migrated3 = receipts.Single(r => r.Fingerprint == "fp-refund-failed");
                Assert.Equal(RentalOperationReceipt.StatusPending, migrated3.Status);
                Assert.Null(migrated3.ErrorCode);
                Assert.Null(migrated3.ErrorMessage);

                // Receipt 4 (Succeeded) -> stays Succeeded
                var succ = receipts.Single(r => r.Fingerprint == "fp-refund-succ");
                Assert.Equal(RentalOperationReceipt.StatusSucceeded, succ.Status);
                Assert.True(succ.IsSuccess);

                // Receipt 5 (ReleaseAllocation) -> stays Pending
                var rel = receipts.Single(r => r.Fingerprint == "fp-release");
                Assert.Equal(RentalOperationReceipt.StatusPending, rel.Status);

                // Receipt 6 (on normal order) -> stays Pending
                var norm = receipts.Single(r => r.Fingerprint == "fp-normal-refund");
                Assert.Equal(RentalOperationReceipt.StatusPending, norm.Status);
            }

            // 5. Re-running MigrateAsync is idempotent and makes no further changes
            await using (var ctx = new RentalServiceDbContext(options))
            {
                await ctx.Database.MigrateAsync();
            }

            await using (var verifyCtx2 = new RentalServiceDbContext(options))
            {
                var receipt = await verifyCtx2.RentalOperationReceipts.SingleAsync(r => r.Fingerprint == "fp-refund-pending");
                Assert.Equal(RentalOperationReceipt.StatusPending, receipt.Status);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Migration_Downgrade_IsBlockedWithForwardFixException()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);

            await using (var ctx = new RentalServiceDbContext(options))
            {
                await ctx.Database.MigrateAsync();
            }

            // Attempting to downgrade past MigrateIncompleteLateRefundReceiptsToManual must fail with forward-fix error
            await using (var ctx = new RentalServiceDbContext(options))
            {
                var migrator = ctx.Database.GetService<IMigrator>();
                var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                    migrator.MigrateAsync("20261012082545_AddOrderCodeSequence"));

                Assert.Contains("forward-fix", ex.Message, StringComparison.OrdinalIgnoreCase);
            }

            // Verify migration history is not corrupted
            await using (var ctx = new RentalServiceDbContext(options))
            {
                var appliedMigrations = (await ctx.Database.GetAppliedMigrationsAsync()).ToList();
                Assert.Contains("20261015020421_MigrateIncompleteLateRefundReceiptsToManual", appliedMigrations);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task OperationRecoveryWorker_ProcessBatchAsync_DoesNotPickUpManualRefund_ProcessesReleaseAllocation()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new RentalServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid itemId = Guid.NewGuid();

            await using (var ctx = new RentalServiceDbContext(options))
            {
                var order = CreateLateOrder(orderId, renterId, itemId);
                var pastTime = DateTimeOffset.UtcNow.AddMinutes(-15);
                order.MarkPaymentExpired(pastTime.AddMinutes(5), "op-exp");
                order.MarkAsLatePayment("INV-001", "PAY-LATE", pastTime.AddMinutes(5), pastTime.AddMinutes(10));

                string cancelOpKey = RentalOperationExecutor.ComputeOperationKey("CancelInvoice", $"{orderId}");
                var cancelReceipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(), orderId, cancelOpKey, "CancelInvoice",
                    "fp-cancel-test", pastTime, itemId,
                    JsonSerializer.Serialize(new CancelInvoiceRequest(orderId, "INV-001", "Late payment cancel", cancelOpKey), JsonOptions));
                order.AddOperationReceipt(cancelReceipt);

                var refundReceipt = new RentalOperationReceipt(
                    Guid.NewGuid(), orderId, RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{orderId}:{itemId}"), "RequestRefund",
                    "fp-ref-test", false, null, null, pastTime,
                    status: RentalOperationReceipt.StatusManualRefundRequired,
                    targetItemId: itemId,
                    prerequisiteOperationId: RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{orderId}:{itemId}"),
                    payloadJson: "{\"totalRefundAmount\":500000}");
                order.AddOperationReceipt(refundReceipt);

                ctx.RentalOrders.Add(order);
                await ctx.SaveChangesAsync();
            }

            var spyPayment = new SpyPaymentClient();
            var stubInventory = new StubInventoryClient();

            // Set up DI container for worker
            var services = new ServiceCollection();
            services.AddDbContext<RentalServiceDbContext>(opt =>
            {
                opt.UseNpgsql(connString);
            });
            services.AddScoped<IRentalOrderRepository, RentalOrderRepository>();
            services.AddScoped<IOperationReceiptRepository, OperationReceiptRepository>();
            services.AddScoped<IOutboxRepository, OutboxRepository>();
            services.AddSingleton<IInventoryCommandProducer>(new StubInventoryProducer());
            services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<RentalServiceDbContext>());
            services.AddScoped<IOrderLockManager, FakeOrderLockManager>();
            services.AddSingleton<IPaymentRentalClient>(spyPayment);
            services.AddSingleton<IInventoryRentalClient>(stubInventory);
            services.AddSingleton<TimeProvider>(new TestTimeProvider(FixedNow));
            services.AddScoped<IRentalOperationExecutor>(sp => new RentalOperationExecutor(
                sp.GetRequiredService<IRentalOrderRepository>(),
                sp.GetRequiredService<IOperationReceiptRepository>(),
                sp.GetRequiredService<IOutboxRepository>(),
                sp.GetRequiredService<IOrderLockManager>(),
                sp.GetRequiredService<IInventoryRentalClient>(),
                sp.GetRequiredService<IPaymentRentalClient>(),
                sp.GetRequiredService<IInventoryCommandProducer>(),
                sp.GetRequiredService<IUnitOfWork>(),
                sp.GetRequiredService<IConfiguration>(),
                NullLogger<RentalOperationExecutor>.Instance,
                timeProvider: sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OperationRecovery:BatchSize"] = "50",
                ["OperationRecovery:PollingIntervalSeconds"] = "15"
            }).Build());
            services.AddLogging();

            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            var worker = new OperationRecoveryWorker(
                scopeFactory,
                sp.GetRequiredService<IConfiguration>(),
                NullLogger<OperationRecoveryWorker>.Instance);

            // Execute one batch recovery
            int processed = await worker.ProcessBatchAsync(CancellationToken.None);

            // ReleaseAllocation was processed (1), but RequestRefund was NEVER picked up
            Assert.Equal(1, processed);
            Assert.Equal(0, spyPayment.RequestRefundCalls); // Payment refund was NOT called!

            await using (var verifyCtx = new RentalServiceDbContext(options))
            {
                var can = await verifyCtx.RentalOperationReceipts.SingleAsync(r => r.Action == "CancelInvoice");
                Assert.Equal(RentalOperationReceipt.StatusSucceeded, can.Status);

                var refund = await verifyCtx.RentalOperationReceipts.SingleAsync(r => r.Action == "RequestRefund");
                Assert.Equal(RentalOperationReceipt.StatusManualRefundRequired, refund.Status);
                Assert.False(refund.IsSuccess);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task ExecutorGuards_DirectExecution_AndReplay_BlockedForManualRefundReceipt()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new RentalServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid itemId = Guid.NewGuid();
            string refundOpKey = RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{orderId}:{itemId}");

            await using (var ctx = new RentalServiceDbContext(options))
            {
                var order = CreateLateOrder(orderId, renterId, itemId);
                order.MarkPaymentExpired(FixedNow.AddMinutes(-10), "op-exp");

                var refundReceipt = new RentalOperationReceipt(
                    Guid.NewGuid(), orderId, refundOpKey, "RequestRefund",
                    "fp-guard-test", false, null, null, FixedNow.AddMinutes(-5),
                    status: RentalOperationReceipt.StatusManualRefundRequired,
                    targetItemId: itemId,
                    payloadJson: "{\"amount\":500000}");
                order.AddOperationReceipt(refundReceipt);

                ctx.RentalOrders.Add(order);
                await ctx.SaveChangesAsync();
            }

            var spyPayment = new SpyPaymentClient();
            var stubInventory = new StubInventoryClient();
            var time = new TestTimeProvider(FixedNow);

            await using (var ctx = new RentalServiceDbContext(options))
            {
                var orderRepo = new RentalOrderRepository(ctx);
                var receiptRepo = new OperationReceiptRepository(ctx);
                var outboxRepo = new OutboxRepository(ctx);
                var lockMgr = new FakeOrderLockManager();

                var config = new ConfigurationBuilder().Build();
                var executor = new RentalOperationExecutor(
                    orderRepo, receiptRepo, outboxRepo, lockMgr,
                    stubInventory, spyPayment, ctx, config,
                    NullLogger<RentalOperationExecutor>.Instance, time);

                // 1. Direct ExecuteRequestRefundAsync on manual receipt -> returns Conflict
                var directRes = await executor.ExecuteRequestRefundAsync(orderId, itemId, refundOpKey);
                Assert.True(directRes.IsFailure);
                Assert.Equal("MANUAL_REFUND_REQUIRED", directRes.Error.Code);
                Assert.Equal(0, spyPayment.RequestRefundCalls);

                // 2. Scheduled execution on descriptor obtained before manual transition -> blocked by executor guard
                var desc = new OperationReceiptDescriptor(
                    Guid.NewGuid(), orderId, refundOpKey, "RequestRefund", itemId,
                    RentalOperationReceipt.StatusManualRefundRequired, 0, null, FixedNow, null, false);
                var schedRes = await executor.ExecuteScheduledOperationAsync(desc);
                Assert.True(schedRes.IsFailure);
                Assert.Equal("MANUAL_REFUND_REQUIRED", schedRes.Error.Code);
                Assert.Equal(0, spyPayment.RequestRefundCalls);

                // 3. ReplayStoredOperationAsync -> returns Conflict
                var replayRes = await executor.ReplayStoredOperationAsync(orderId, refundOpKey);
                Assert.True(replayRes.IsFailure);
                Assert.Equal("MANUAL_REFUND_REQUIRED", replayRes.Error.Code);
                Assert.Equal(0, spyPayment.RequestRefundCalls);

                // 4. Admin Replay with valid Admin role -> returns 409 Conflict
                var adminRes = await executor.ExecuteAdminReplayAsync(orderId, refundOpKey, Guid.NewGuid(), "Admin reason", "admin-key-1");
                Assert.True(adminRes.IsFailure);
                Assert.Equal(ErrorType.Conflict, adminRes.Error.Type);
                Assert.Equal("MANUAL_REFUND_REQUIRED", adminRes.Error.Code);
                Assert.Equal(0, spyPayment.RequestRefundCalls);
            }

            // Explicit manual-refund receipts stay manual and cannot be replayed by the worker.
            await using (var verifyCtx = new RentalServiceDbContext(options))
            {
                var receipt = await verifyCtx.RentalOperationReceipts.SingleAsync(r => r.OperationId == refundOpKey);
                Assert.Equal(RentalOperationReceipt.StatusManualRefundRequired, receipt.Status);
                Assert.False(receipt.IsSuccess);
                Assert.Equal(0, receipt.RetryCount);

                var order = await verifyCtx.RentalOrders.Include(o => o.StateTransitions).SingleAsync(o => o.Id == orderId);
                Assert.DoesNotContain(order.StateTransitions, t => t.ToState == RentalOperationReceipt.StatusPending);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task AdminReplayEndpoint_AuthorizationAndRegression_401And403()
    {
        var dummyExecutor = new DummyExecutor();
        var dummyRepo = new DummyOrderRepo();

        // 1. Missing Authentication -> 401 Unauthorized
        var controller401 = new RentalInternalOperationsController(dummyExecutor, dummyRepo, RentalCallerContext.Anonymous);

        var res401 = await controller401.ReplayOperation(Guid.NewGuid(), "op-1", new AdminReplayRequest("Test reason"), "key-1");
        var objRes401 = Assert.IsType<ObjectResult>(res401);
        Assert.Equal(StatusCodes.Status401Unauthorized, objRes401.StatusCode);

        // 2. User is NOT Admin (e.g. Renter / Customer role) -> 403 Forbidden
        var nonAdminCaller = RentalCallerContext.FromClaims([
            new(RentalAuthClaims.Subject, Guid.NewGuid().ToString("D")),
            new(RentalAuthClaims.Roles, "customer")
        ]);
        var controller403 = new RentalInternalOperationsController(dummyExecutor, dummyRepo, nonAdminCaller);

        var res403 = await controller403.ReplayOperation(Guid.NewGuid(), "op-1", new AdminReplayRequest("Test reason"), "key-1");
        var objRes403 = Assert.IsType<ObjectResult>(res403);
        Assert.Equal(StatusCodes.Status403Forbidden, objRes403.StatusCode);
    }

    [Fact]
    public async Task LatePaymentReplay_DoesNotCreateAdditionalReceiptOrOutbox()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new RentalServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid itemId = Guid.NewGuid();

            RentalOrder order;
            await using (var ctx = new RentalServiceDbContext(options))
            {
                order = CreateLateOrder(orderId, renterId, itemId);
                ctx.RentalOrders.Add(order);
                await ctx.SaveChangesAsync();
            }

            var spyPayment = new SpyPaymentClient();
            var stubInventory = new StubInventoryClient();
            var time = new TestTimeProvider(FixedNow.AddMinutes(40));

            var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
                Guid.NewGuid(), "InvoicePaid", 1, "payment-service", orderId.ToString("D"),
                FixedNow.AddMinutes(40), "corr-late", null,
                new InvoicePaidPayload(orderId, renterId, "INV-001", "PAY-LATE-1", order.TotalPayableAmount, "VND", FixedNow.AddMinutes(10)));

            // 1. Process late payment first time
            await using (var ctx1 = new RentalServiceDbContext(options))
            {
                var wf = CreateWorkflowService(ctx1, stubInventory, spyPayment, time);
                var res1 = await wf.ProcessInvoicePaidAsync(envelope);
                Assert.True(res1.IsSuccess);
            }

            // 2. Replay same event second time
            await using (var ctx2 = new RentalServiceDbContext(options))
            {
                var wf = CreateWorkflowService(ctx2, stubInventory, spyPayment, time);
                var res2 = await wf.ProcessInvoicePaidAsync(envelope);
                Assert.True(res2.IsSuccess);
            }

            // Assert: exactly 1 refund receipt, exactly 1 Outbox message
            await using (var verifyCtx = new RentalServiceDbContext(options))
            {
                var receipts = await verifyCtx.RentalOperationReceipts
                    .Where(r => r.Action == "RequestRefund")
                    .ToListAsync();
                Assert.Single(receipts);
                Assert.Equal(RentalOperationReceipt.StatusPending, receipts[0].Status);

                var outbox = await verifyCtx.RentalOutboxMessages
                    .Where(m => m.MessageType == KafkaContractSpec.EventTypes.LatePaymentRefundRequested)
                    .ToListAsync();
                Assert.Single(outbox);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }
    [Fact]
    public async Task ConcurrentDuplicatePayment_OnSeparateDbContexts_ResultsInSingleSetOfSideEffects()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new RentalServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid itemId = Guid.NewGuid();

            RentalOrder order;
            await using (var ctx = new RentalServiceDbContext(options))
            {
                order = CreateLateOrder(orderId, renterId, itemId);
                ctx.RentalOrders.Add(order);
                await ctx.SaveChangesAsync();
            }

            var spyPayment = new SpyPaymentClient();
            var stubInventory = new StubInventoryClient();
            var time = new TestTimeProvider(FixedNow.AddMinutes(40));

            var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
                Guid.NewGuid(), "InvoicePaid", 1, "payment-service", orderId.ToString("D"),
                FixedNow.AddMinutes(40), "corr-concurrent", null,
                new InvoicePaidPayload(orderId, renterId, "INV-001", "PAY-CONCURRENT", order.TotalPayableAmount, "VND", FixedNow.AddMinutes(10)));

            // Run two workers concurrently with separate DbContexts
            var task1 = Task.Run(async () =>
            {
                await using var ctx1 = new RentalServiceDbContext(options);
                var wf1 = CreateWorkflowService(ctx1, stubInventory, spyPayment, time);
                return await wf1.ProcessInvoicePaidAsync(envelope);
            });

            var task2 = Task.Run(async () =>
            {
                await using var ctx2 = new RentalServiceDbContext(options);
                var wf2 = CreateWorkflowService(ctx2, stubInventory, spyPayment, time);
                return await wf2.ProcessInvoicePaidAsync(envelope);
            });

            var results = await Task.WhenAll(task1, task2);
            Assert.All(results, r => Assert.True(r.IsSuccess));

            // Assert: exactly 1 refund receipt, exactly 1 Outbox message, exactly 1 inbox message
            await using (var verifyCtx = new RentalServiceDbContext(options))
            {
                var receipts = await verifyCtx.RentalOperationReceipts
                    .Where(r => r.Action == "RequestRefund")
                    .ToListAsync();
                Assert.Single(receipts);

                var outbox = await verifyCtx.RentalOutboxMessages
                    .Where(m => m.MessageType == KafkaContractSpec.EventTypes.LatePaymentRefundRequested)
                    .ToListAsync();
                Assert.Single(outbox);

                var inbox = await verifyCtx.RentalInboxMessages
                    .Where(m => m.MessageId == envelope.MessageId)
                    .ToListAsync();
                Assert.Single(inbox);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }


    private static PaymentWorkflowService CreateWorkflowService(
        RentalServiceDbContext db,
        IInventoryRentalClient invClient,
        IPaymentRentalClient payClient,
        TimeProvider tp)
    {
        var orderRepo = new RentalOrderRepository(db);
        var inboxRepo = new InboxRepository(db);
        var outboxRepo = new OutboxRepository(db);
        var receiptRepo = new OperationReceiptRepository(db);
        var lockMgr = new FakeOrderLockManager();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:RentalEventsTopic"] = "rental.events.v1"
        }).Build();

        var executor = new RentalOperationExecutor(
            orderRepo, receiptRepo, outboxRepo, lockMgr,
            invClient, payClient, db, config,
            NullLogger<RentalOperationExecutor>.Instance, tp);

        return new PaymentWorkflowService(
            orderRepo, inboxRepo, outboxRepo, receiptRepo, lockMgr,
            executor, db,
            new InventoryCommandProducer(new KafkaOutboxWriter(outboxRepo), Microsoft.Extensions.Options.Options.Create(new Dprental.RentalService.Application.Kafka.Configuration.KafkaOptions()), tp),
            config, NullLogger<PaymentWorkflowService>.Instance,
            new RentalEventProducer(new KafkaOutboxWriter(outboxRepo), Microsoft.Extensions.Options.Options.Create(new Dprental.RentalService.Application.Kafka.Configuration.KafkaOptions()), tp), tp);
    }

    private sealed class SpyPaymentClient : IPaymentRentalClient
    {
        public int RequestRefundCalls { get; private set; }

        public Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(CreateInvoiceRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<Result<CancelInvoiceResponse>> CancelInvoiceAsync(CancelInvoiceRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new CancelInvoiceResponse(request.InvoiceRef, true, DateTimeOffset.UtcNow, false)));

        public Task<Result<RequestRefundResponse>> RequestRefundAsync(RequestRefundRequest request, CancellationToken ct = default)
        {
            RequestRefundCalls++;
            return Task.FromResult(Result.Success(new RequestRefundResponse("REF-001", request.OperationKey, "Accepted", request.TotalRefundAmount, "VND", false)));
        }

        public Task<Result<SettleRentalResponse>> SettleRentalAsync(SettleRentalRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private sealed class StubInventoryClient : IInventoryRentalClient
    {
        public Task<Result<ExtendHoldResponse>> ExtendHoldAsync(string allocationGroupId, ExtendHoldRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new ExtendHoldResponse("group-1", allocationGroupId, request.NewHoldExpiresAt, FixedNow, false)));

        public Task<Result<ReleaseAllocationResponse>> ReleaseAllocationAsync(string allocationGroupId, ReleaseAllocationRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new ReleaseAllocationResponse("group-1", request.OperationKey, allocationGroupId, FixedNow, false)));

        public Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(ListingAvailabilityCheckRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ListingRentalSnapshotsResponse>> GetListingRentalSnapshotsAsync(ListingRentalSnapshotsRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CreateHoldResponse>> CreateHoldAsync(CreateHoldRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ConfirmAllocationResponse>> ConfirmAllocationAsync(string allocationGroupId, ConfirmAllocationRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CancelAllocationResponse>> CancelAllocationAsync(string allocationGroupId, CancelAllocationRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<MarkAssetsOverdueResponse>> MarkAssetsOverdueAsync(string allocationGroupId, MarkAssetsOverdueRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<MarkAssetsReturnedResponse>> MarkAssetsReturnedAsync(string allocationGroupId, MarkAssetsReturnedRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<BlockDamagedAssetsResponse>> BlockDamagedAssetsAsync(string allocationGroupId, BlockDamagedAssetsRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class DummyExecutor : IRentalOperationExecutor
    {
        public Task<Result> ExecuteExtendHoldAsync(Guid orderId, Guid itemId, string operationKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Result<SupplierDecisionResponse>> ExecuteSupplierAcceptAsync(Guid orderId, Guid itemId, Guid supplierId, string idempotencyKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Result<SupplierDecisionResponse>> ExecuteSupplierRejectAsync(Guid orderId, Guid itemId, Guid supplierId, string reason, string? note, string idempotencyKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Result<IReadOnlyList<SupplierDecisionResponse>>> ExecuteSubOrderAcceptAsync(Guid orderId, Guid subOrderId, Guid supplierId, string idempotencyKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Result<IReadOnlyList<SupplierDecisionResponse>>> ExecuteSubOrderRejectAsync(Guid orderId, Guid subOrderId, Guid supplierId, string reason, string? note, string idempotencyKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Result> ExecuteReconcileAcceptAsync(Guid orderId, Guid itemId, string acceptOperationKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Result> ExecuteCancelInvoiceAsync(Guid orderId, string operationKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Result> ExecuteReleaseAllocationAsync(Guid orderId, Guid itemId, string operationKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Result> ExecuteRequestRefundAsync(Guid orderId, Guid itemId, string operationKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Result> RequestRefundDirectAsync(RentalOrder order, RentalItem item, RentalOperationReceipt receipt, string operationKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Result> ExecutePaymentExpiryAsync(Guid orderId, string operationKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Result> ExecuteScheduledOperationAsync(OperationReceiptDescriptor descriptor, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Result> ReplayStoredOperationAsync(Guid orderId, string operationId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Result<AdminReplayResponse>> ExecuteAdminReplayAsync(Guid orderId, string operationId, Guid adminActorId, string reason, string idempotencyKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ScheduleReleaseAndRefundAsync(RentalOrder order, RentalItem item, Guid actorId, string reason, DateTimeOffset utcNow, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DummyOrderRepo : IRentalOrderRepository
    {
        public Task<RentalOrder?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RentalOrder?> GetByOrderCodeAsync(string orderCode, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RentalOrder?> GetByCheckoutOperationIdAsync(string operationId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<RentalOrder>> GetByRenterIdAsync(Guid renterId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RentalOrder?> GetPendingPaymentByRenterIdAsync(Guid renterId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<RentalOrder>> GetBySupplierIdAsync(Guid supplierId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RentalOrder> CreateAsync(RentalOrder order, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> UpdateAsync(RentalOrder order, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RentalOrder?> GetByIdWithDetailsAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RentalOrder?> GetForCheckoutPollingAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Guid?> GetRenterIdByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RentalOrder?> GetByItemIdWithDetailsAsync(Guid itemId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Guid?> GetOrderIdByItemIdAsync(Guid itemId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> GetPendingPaymentExpiredOrderIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> GetPendingSupplierTimeoutItemIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<RenterCalendarItemRecord>> GetRenterCalendarItemsAsync(Guid renterId, DateOnly monthStart, DateOnly monthEnd, IReadOnlyCollection<RentalItemStatus> targetStatuses, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SupplierConfirmationStatisticsRecord> GetSupplierConfirmationStatisticsAsync(Guid supplierId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> GetOverdueUnprotectedOrderIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> GetDeductionTimeoutItemIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> GetRenterResponseTimeoutItemIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> GetDisputedItemIdsAsync(int skip, int take, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> GetSettlementReadyItemIdsAsync(int batchSize, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RentalSubOrder?> GetSubOrderByIdAsync(Guid subOrderId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RecordPage<RenterOrderSummaryRecord>> GetRenterOrderPageAsync(Guid renterId, RentalOrderStatus? status, int skip, int take, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RentalOrder?> GetRenterOrderWithSubOrdersAsync(Guid orderId, Guid renterId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RecordPage<SupplierSubOrderSummaryRecord>> GetPaidSubOrderPageForSupplierAsync(Guid supplierId, RentalItemStatus? itemStatus, int skip, int take, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RecordPage<AdminOrderSummaryRecord>> SearchOrdersForAdminAsync(AdminOrderSearchCriteria criteria, int skip, int take, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RentalOrder?> GetOrderForAdminAsync(Guid orderId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RentalSubOrder?> GetPaidSubOrderForSupplierAsync(Guid subOrderId, Guid supplierId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class Mock<T> where T : class
    {
        public T Object => (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));
    }

    private sealed class StubInventoryProducer : IInventoryCommandProducer
    {
        public Task<Result<ProducerEnqueueResult>> EnqueueCreateHoldAsync(CreateHoldCommandPayload command, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "CreateHold", "topic", "key")));

        public Task<Result<ProducerEnqueueResult>> EnqueueExtendHoldAsync(ExtendHoldCommandPayload command, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "ExtendHold", "topic", "key")));

        public Task<Result<ProducerEnqueueResult>> EnqueueConfirmAllocationAsync(ConfirmAllocationCommandPayload command, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "ConfirmAllocation", "topic", "key")));

        public Task<Result<ProducerEnqueueResult>> EnqueueReleaseAllocationAsync(ReleaseAllocationCommandPayload command, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "ReleaseAllocation", "topic", "key")));

        public Task<Result<ProducerEnqueueResult>> EnqueueMarkAssetsHandedOverAsync(MarkAssetsHandedOverCommandPayload command, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "Handover", "topic", "key")));
        public Task<Result<ProducerEnqueueResult>> EnqueueMarkAssetsOverdueAsync(MarkAssetsOverdueCommandPayload command, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "MarkAssetsOverdue", "topic", "key")));
        public Task<Result<ProducerEnqueueResult>> EnqueueApplyReturnedUnitsAsync(ApplyReturnedUnitsCommandPayload command, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "ApplyReturnedUnits", "topic", "key")));
    }
}
