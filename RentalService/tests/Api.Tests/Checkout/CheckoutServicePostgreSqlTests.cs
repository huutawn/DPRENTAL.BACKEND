using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Checkout;

/// <summary>
/// Chạy checkout trên PostgreSQL thật: sequence mã đơn, advisory lock theo renter, ràng buộc unique
/// của CheckoutOperationId và concurrency token của đơn.
/// </summary>
public sealed class CheckoutServicePostgreSqlTests
{
    private static readonly System.Text.Json.JsonSerializerOptions CamelCaseJsonOptions = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
    // 10:00 ngày 09/10/2026 giờ Việt Nam.
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 3, 0, 0, TimeSpan.Zero);
    private const string Key = "checkout-key-1";

    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly Guid _listingId = Guid.NewGuid();
    private readonly FakeInventoryListingClient _listings = new();
    private readonly FakeInventoryRentalClient _inventory = new();
    private readonly FakePaymentRentalClient _payment = new();

    [Fact]
    public async Task Submit_ConcurrentAndRepeatedSameKey_CreatesOnePayableOrder()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }

        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            DbContextOptions<RentalServiceDbContext> options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            Guid cartItemId = await SeedAsync(options);
            PostgresOrderLockManager locks = new(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString })
                    .Build(),
                NullLogger<PostgresOrderLockManager>.Instance);

            // 2 máy x 500.000 x 3 ngày + cọc 2 x 2.000.000.
            CheckoutRequest request = new(
                [cartItemId],
                new("Hồ Chí Minh", "123 Nguyễn Huệ, Quận 1", "Nguyễn Văn A", "0901234567", null),
                new("VCB", "0123456789", "NGUYEN VAN A"),
                "v1.0",
                true,
                3_000_000m);

            // Consumer và worker dùng chung cho mọi lần gửi, như một instance Rental đang chạy.
            FixedTimeProvider clock = new(Now);
            CheckoutCompletion completion = new(
                () => new RentalServiceDbContext(options), clock, _inventory, _payment, locks,
                context => new PostgresOrderCodeGenerator(context));

            CheckoutResult[] concurrent = await Task.WhenAll(
                Task.Run(() => SubmitAsync(options, locks, request, completion)),
                Task.Run(() => SubmitAsync(options, locks, request, completion)));
            CheckoutResult repeated = await SubmitAsync(options, locks, request, completion);

            Assert.All(concurrent, r => Assert.True(r.IsSuccess, r.Error.Code));
            Assert.True(repeated.IsSuccess, repeated.Error.Code);
            Assert.True(repeated.IsReplay);

            await using RentalServiceDbContext context = new(options);
            RentalOrder order = await context.RentalOrders
                .Include(o => o.Items).ThenInclude(i => i.UnitAssignments)
                .SingleAsync();

            Assert.All(concurrent, r => Assert.Equal(order.Id, r.Order!.Id));
            Assert.Equal(order.Id, repeated.Order!.Id);
            Assert.Equal("DH-2026-000001", order.OrderCode);
            Assert.Equal(RentalOrderStatus.PendingPayment, order.Status);
            Assert.Equal(Now.AddMinutes(30), order.PaymentDeadline);
            Assert.Equal("INV-1", order.InvoiceRef);
            Assert.Equal(3_000_000m, order.TotalPayableAmount);
            Assert.Equal(2, Assert.Single(order.Items).UnitAssignments.Count);
            Assert.Equal(0, await context.RentalCartItems.CountAsync());

            // Cùng Idempotency-Key chỉ ghi một lệnh giữ lịch và Inventory chỉ giữ máy một lần.
            var holdMsg = await context.RentalOutboxMessages.SingleAsync(m => m.MessageType == KafkaContractSpec.CommandTypes.CreateHold);
            using (var doc = System.Text.Json.JsonDocument.Parse(holdMsg.Payload))
            {
                var holdDeadline = doc.RootElement.GetProperty("payload").GetProperty("holdExpiresAt").GetDateTimeOffset();
                Assert.Equal(order.PaymentDeadline, holdDeadline);
            }
            Assert.Single(_inventory.HoldRequests);
            Assert.Empty(_inventory.Releases);
            var invReq = Assert.Single(_payment.InvoiceRequests);
            Assert.Equal(order.PaymentDeadline, invReq.PaymentDeadline);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }
    [Fact]
    public async Task RecoveryQuery_OnPostgreSql_ReturnsCreateInvoiceOver10AttemptsAndExhaustedReceipts()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }

        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            DbContextOptions<RentalServiceDbContext> options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            await using (var initCtx = new RentalServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();

                Guid orderId = Guid.NewGuid();
                var items = new[]
                {
                    new InitialRentalItemDto(
                        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
                        new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18),
                        "Camera Sony", null, "Pickup", 200_000m, 0m, "grp-01",
                        [new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
                        Dprental.RentalService.Api.Tests.Domain.TestListingSnapshots.Default)
                };
                var order = RentalOrder.Create(
                    orderId, "ORD-PG-001", _renterId, "VND", "Delivery",
                    "Nguyen Van A", "0901234567", "Address", null, null,
                    "v1.0", "terms.pdf", "hash", "op-seed", Now, items).Value;
                initCtx.RentalOrders.Add(order);

                // Receipt 1: CreateInvoice, RetryCount = 10, Status = Pending
                var r1 = new RentalOperationReceipt(
                    Guid.NewGuid(), orderId, "op-inv-1", "CreateInvoice", "fp-1",
                    isSuccess: false, errorCode: "Payment.Timeout", errorMessage: "Timeout",
                    createdAt: Now.AddMinutes(-10), status: RentalOperationReceipt.StatusPending,
                    retryCount: 10, nextRetryAt: Now.AddSeconds(-10), isTransient: true);

                // Receipt 2: CreateInvoice, RetryCount = 12, Status = Failed, TransientExhausted
                var r2 = new RentalOperationReceipt(
                    Guid.NewGuid(), orderId, "op-inv-2", "CreateInvoice", "fp-2",
                    isSuccess: false, errorCode: "Payment.Timeout", errorMessage: "Timeout",
                    createdAt: Now.AddMinutes(-20), status: RentalOperationReceipt.StatusFailed,
                    retryCount: 12, nextRetryAt: Now.AddSeconds(-10),
                    errorClassification: RentalOperationReceipt.ClassificationTransientExhausted, isTransient: false);

                // Receipt 3: CreateInvoice, Status = Failed, Business
                var r3 = new RentalOperationReceipt(
                    Guid.NewGuid(), orderId, "op-inv-3", "CreateInvoice", "fp-3",
                    isSuccess: false, errorCode: "Checkout.InvoiceRejected", errorMessage: "Rejected",
                    createdAt: Now.AddMinutes(-10), status: RentalOperationReceipt.StatusFailed,
                    retryCount: 1, nextRetryAt: null,
                    errorClassification: RentalOperationReceipt.ClassificationBusiness, isTransient: false);

                // Receipt 4: ReleaseAllocation, RetryCount = 10, Status = Pending
                var r4 = new RentalOperationReceipt(
                    Guid.NewGuid(), orderId, "op-rel-4", "ReleaseAllocation", "fp-4",
                    isSuccess: false, errorCode: "Inventory.Down", errorMessage: "Down",
                    createdAt: Now.AddMinutes(-10), status: RentalOperationReceipt.StatusPending,
                    retryCount: 10, nextRetryAt: Now.AddSeconds(-10), isTransient: true);

                initCtx.RentalOperationReceipts.AddRange(r1, r2, r3, r4);
                await initCtx.SaveChangesAsync();
            }

            await using (var queryCtx = new RentalServiceDbContext(options))
            {
                var repo = new OperationReceiptRepository(queryCtx);
                var results = await repo.GetPendingOperationDescriptorsAsync(
                    Now, 50, unlimitedAttemptActions: ["CreateInvoice"]);

                Assert.Contains(results, r => r.OperationId == "op-inv-1");
                Assert.Contains(results, r => r.OperationId == "op-inv-2");
                Assert.DoesNotContain(results, r => r.OperationId == "op-inv-3");
                Assert.DoesNotContain(results, r => r.OperationId == "op-rel-4");
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }
    [Fact]
    public async Task RecoveryQuery_BatchStarvation_ClosesOldReceiptsAndAdvancesToNewOperations()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }

        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            DbContextOptions<RentalServiceDbContext> options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            PostgresOrderLockManager locks = new(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString }).Build(),
                NullLogger<PostgresOrderLockManager>.Instance);

            Guid newOrderId = Guid.NewGuid();
            string newOpKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", newOrderId.ToString());

            await using (var initCtx = new RentalServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();

                // 50 expired orders with exhausted receipts
                for (int i = 0; i < 50; i++)
                {
                    Guid expOrderId = Guid.NewGuid();
                    var items = new[]
                    {
                        new InitialRentalItemDto(
                            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
                            new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18),
                            "Item", null, "Pickup", 200_000m, 0m, $"grp-{i}",
                            [new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
                            Dprental.RentalService.Api.Tests.Domain.TestListingSnapshots.Default)
                    };
                    var expOrder = RentalOrder.Create(
                        expOrderId, $"DH-EXP-{i:D4}", _renterId, "VND", "Delivery",
                        "Recipient", "0900000000", "Addr", null, null,
                        "v1.0", "terms.pdf", "hash", $"op-exp-{i}", Now.AddMinutes(-40), items,
                        paymentDeadline: Now.AddMinutes(-10)).Value;
                    expOrder.MarkPaymentExpired(Now.AddMinutes(-10), $"exp-op-{i}");

                    string expKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", expOrderId.ToString());
                    var r = new RentalOperationReceipt(
                        Guid.NewGuid(), expOrderId, expKey, "CreateInvoice", $"fp-{i}",
                        isSuccess: false, errorCode: "Payment.Timeout", errorMessage: "Timeout",
                        createdAt: Now.AddMinutes(-30).AddSeconds(i), status: RentalOperationReceipt.StatusFailed,
                        retryCount: 10, nextRetryAt: Now.AddMinutes(-5),
                        errorClassification: RentalOperationReceipt.ClassificationTransientExhausted, isTransient: false);

                    initCtx.RentalOrders.Add(expOrder);
                    initCtx.RentalOperationReceipts.Add(r);
                }

                // 1 newer active pending order with pending receipt
                var activeItems = new[]
                {
                    new InitialRentalItemDto(
                        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
                        new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18),
                        "Camera", null, "Pickup", 500_000m, 0m, "grp-active",
                        [new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
                        Dprental.RentalService.Api.Tests.Domain.TestListingSnapshots.Default)
                };
                var activeOrder = RentalOrder.Create(
                    newOrderId, "DH-ACTIVE-001", _renterId, "VND", "Delivery",
                    "Recipient", "0900000000", "Addr", null, null,
                    "v1.0", "terms.pdf", "hash", "op-active", Now, activeItems,
                    paymentDeadline: Now.AddMinutes(30)).Value;

                var activeReceipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(), newOrderId, newOpKey, "CreateInvoice", "fp-active", Now);

                initCtx.RentalOrders.Add(activeOrder);
                initCtx.RentalOperationReceipts.Add(activeReceipt);
                await initCtx.SaveChangesAsync();
            }

            // Query batch 1: gets the 50 exhausted receipts
            await using (var batch1Ctx = new RentalServiceDbContext(options))
            {
                var repo = new OperationReceiptRepository(batch1Ctx);
                var batch1 = await repo.GetPendingOperationDescriptorsAsync(Now, 50, unlimitedAttemptActions: ["CreateInvoice"]);
                Assert.Equal(50, batch1.Count);
                Assert.DoesNotContain(batch1, r => r.OperationId == newOpKey);

                // Process batch 1
                var executor = CreatePostgresExecutor(batch1Ctx, locks);
                foreach (var desc in batch1)
                {
                    var res = await executor.ExecuteScheduledOperationAsync(desc);
                    Assert.True(res.IsSuccess);
                }
            }

            // Query batch 2: all 50 are now closed, batch 2 returns the active receipt!
            await using (var batch2Ctx = new RentalServiceDbContext(options))
            {
                var repo = new OperationReceiptRepository(batch2Ctx);
                var batch2 = await repo.GetPendingOperationDescriptorsAsync(Now, 50, unlimitedAttemptActions: ["CreateInvoice"]);
                var activeDesc = Assert.Single(batch2);
                Assert.Equal(newOpKey, activeDesc.OperationId);

                // Process active receipt with real issuer
                var executor = CreatePostgresExecutor(batch2Ctx, locks);
                var res = await executor.ExecuteScheduledOperationAsync(activeDesc);
                Assert.True(res.IsSuccess);
            }

            // Query batch 3: nothing pending
            await using (var verifyCtx = new RentalServiceDbContext(options))
            {
                var repo = new OperationReceiptRepository(verifyCtx);
                var batch3 = await repo.GetPendingOperationDescriptorsAsync(Now, 50, unlimitedAttemptActions: ["CreateInvoice"]);
                Assert.Empty(batch3);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task RealIssuer_RecoveryOver10Attempts_PersistsToPostgreSql()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }

        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            DbContextOptions<RentalServiceDbContext> options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            PostgresOrderLockManager locks = new(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString }).Build(),
                NullLogger<PostgresOrderLockManager>.Instance);

            Guid orderId = Guid.NewGuid();
            string opId = "checkout-op-pg-10";
            string opKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", orderId.ToString());

            await using (var initCtx = new RentalServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();

                var items = new[]
                {
                    new InitialRentalItemDto(
                        Guid.NewGuid(), _supplierId, _listingId, 2,
                        new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18),
                        "Camera Sony", null, "Delivery", 500_000m, 0m, "grp-1",
                        [
                            new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
                            new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
                        ],
                        Dprental.RentalService.Api.Tests.Domain.TestListingSnapshots.Default)
                };
                var order = RentalOrder.Create(
                    orderId, "DH-2026-000010", _renterId, "VND", "Delivery",
                    "Nguyen Van A", "0901234567", "Hanoi", null, null,
                    "v1.0", "terms.pdf", "hash", opId, Now, items,
                    paymentDeadline: Now.AddMinutes(30)).Value;

                var receipt = new RentalOperationReceipt(
                    Guid.NewGuid(), orderId, opKey, "CreateInvoice", "fp-10",
                    isSuccess: false, errorCode: "Payment.Timeout", errorMessage: "Timeout",
                    createdAt: Now.AddMinutes(-5), status: RentalOperationReceipt.StatusPending,
                    retryCount: 10, nextRetryAt: Now.AddSeconds(-10), isTransient: true);

                var checkoutOp = new RentalCheckoutOperation(
                    opId, _renterId, "fp", orderId, Now.AddMinutes(30), "{}", "[]", Now);
                checkoutOp.MarkSucceeded(Now, invoiceStatus: "Pending");

                initCtx.RentalOrders.Add(order);
                initCtx.RentalOperationReceipts.Add(receipt);
                initCtx.RentalCheckoutOperations.Add(checkoutOp);
                await initCtx.SaveChangesAsync();
            }

            // Execute recovery using real issuer and executor
            await using (var execCtx = new RentalServiceDbContext(options))
            {
                var repo = new OperationReceiptRepository(execCtx);
                var descriptors = await repo.GetPendingOperationDescriptorsAsync(Now, 50, unlimitedAttemptActions: ["CreateInvoice"]);
                var desc = Assert.Single(descriptors);
                Assert.Equal(10, desc.RetryCount);

                var executor = CreatePostgresExecutor(execCtx, locks);
                var res = await executor.ExecuteScheduledOperationAsync(desc);
                Assert.True(res.IsSuccess);
            }

            // Verify in new DbContext
            await using (var verifyCtx = new RentalServiceDbContext(options))
            {
                var order = await verifyCtx.RentalOrders.FirstAsync(o => o.Id == orderId);
                Assert.Equal("INV-1", order.InvoiceRef);

                var receipt = await verifyCtx.RentalOperationReceipts.FirstAsync(r => r.OperationId == opKey);
                Assert.Equal(11, receipt.RetryCount);
                Assert.True(receipt.IsSuccess);
                Assert.Equal(RentalOperationReceipt.StatusSucceeded, receipt.Status);

                var op = await verifyCtx.RentalCheckoutOperations.FirstAsync(o => o.OperationId == opId);
                Assert.Equal("Ready", op.InvoiceStatus);

                var repo = new OperationReceiptRepository(verifyCtx);
                var pending = await repo.GetPendingOperationDescriptorsAsync(Now, 50, unlimitedAttemptActions: ["CreateInvoice"]);
                Assert.Empty(pending);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task TimeoutFollowedByBusinessRejection_StopsRetryingImmediately()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }

        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            DbContextOptions<RentalServiceDbContext> options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            PostgresOrderLockManager locks = new(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString }).Build(),
                NullLogger<PostgresOrderLockManager>.Instance);

            Guid orderId = Guid.NewGuid();
            string opKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", orderId.ToString());
            string opId = "op-timeout-reject";

            await using (var initCtx = new RentalServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();

                var items = new[]
                {
                    new InitialRentalItemDto(
                        Guid.NewGuid(), _supplierId, _listingId, 2,
                        new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18),
                        "Camera", null, "Delivery", 500_000m, 0m, "grp-1",
                        [
                            new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
                            new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
                        ],
                        Dprental.RentalService.Api.Tests.Domain.TestListingSnapshots.Default)
                };
                var order = RentalOrder.Create(
                    orderId, "DH-2026-000099", _renterId, "VND", "Delivery",
                    "Customer", "0900000000", "Addr", null, null,
                    "v1.0", "terms.pdf", "hash", opId, Now, items,
                    paymentDeadline: Now.AddMinutes(30)).Value;
                var receipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(), orderId, opKey, "CreateInvoice", "fp", Now);

                initCtx.RentalOrders.Add(order);
                initCtx.RentalOperationReceipts.Add(receipt);
                await initCtx.SaveChangesAsync();
            }

            var fakePayment = new FakePaymentRentalClient();
            fakePayment.AlwaysFailWith = FakePaymentRentalClient.Down; // Attempt 1: Timeout (uncertain)

            // Attempt 1: timeout
            await using (var ctx1 = new RentalServiceDbContext(options))
            {
                var executor1 = CreatePostgresExecutor(ctx1, locks, fakePayment);
                var desc = Assert.Single(await new OperationReceiptRepository(ctx1).GetPendingOperationDescriptorsAsync(Now, 50, unlimitedAttemptActions: ["CreateInvoice"]));
                var res1 = await executor1.ExecuteScheduledOperationAsync(desc);
                Assert.True(res1.IsFailure);
            }

            // Check receipt after attempt 1
            DateTimeOffset retryTime;
            await using (var checkCtx = new RentalServiceDbContext(options))
            {
                var r = await checkCtx.RentalOperationReceipts.FirstAsync(rc => rc.OperationId == opKey);
                Assert.True(r.IsOutcomeUncertain);
                Assert.NotNull(r.NextRetryAt);
                retryTime = r.NextRetryAt.Value;
            }

            // Attempt 2: Payment rejects with business failure
            fakePayment.AlwaysFailWith = CheckoutErrors.InvoiceRejected;

            await using (var ctx2 = new RentalServiceDbContext(options))
            {
                var clock = new FixedTimeProvider(retryTime.AddSeconds(1));
                var executor2 = CreatePostgresExecutor(ctx2, locks, fakePayment, clock);
                var desc = Assert.Single(await new OperationReceiptRepository(ctx2).GetPendingOperationDescriptorsAsync(retryTime.AddSeconds(1), 50, unlimitedAttemptActions: ["CreateInvoice"]));
                var res2 = await executor2.ExecuteScheduledOperationAsync(desc);
                Assert.True(res2.IsFailure);
            }

            // Verify in new context: receipt is Failed/Business, NextRetryAt is null, not returned in query
            await using (var finalCtx = new RentalServiceDbContext(options))
            {
                var r = await finalCtx.RentalOperationReceipts.FirstAsync(rc => rc.OperationId == opKey);
                Assert.Equal(RentalOperationReceipt.StatusFailed, r.Status);
                Assert.Equal(RentalOperationReceipt.ClassificationBusiness, r.ErrorClassification);
                Assert.Null(r.NextRetryAt);

                var pending = await new OperationReceiptRepository(finalCtx).GetPendingOperationDescriptorsAsync(Now.AddMinutes(10), 50, unlimitedAttemptActions: ["CreateInvoice"]);
                Assert.Empty(pending);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task ConcurrencyConflict_WithOtherWriter_HandledCorrectlyWithoutGraphCorruption()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }

        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            DbContextOptions<RentalServiceDbContext> options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            PostgresOrderLockManager locks = new(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString }).Build(),
                NullLogger<PostgresOrderLockManager>.Instance);

            Guid orderId = Guid.NewGuid();
            string opKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", orderId.ToString());
            string opId = "op-conflict-test";

            await using (var initCtx = new RentalServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();

                var items = new[]
                {
                    new InitialRentalItemDto(
                        Guid.NewGuid(), _supplierId, _listingId, 2,
                        new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18),
                        "Camera", null, "Delivery", 500_000m, 0m, "grp-1",
                        [
                            new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
                            new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
                        ],
                        Dprental.RentalService.Api.Tests.Domain.TestListingSnapshots.Default)
                };
                var order = RentalOrder.Create(
                    orderId, "DH-2026-000077", _renterId, "VND", "Delivery",
                    "Customer", "0900000000", "Addr", null, null,
                    "v1.0", "terms.pdf", "hash", opId, Now, items,
                    paymentDeadline: Now.AddMinutes(30)).Value;
                var receipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(), orderId, opKey, "CreateInvoice", "fp", Now);

                initCtx.RentalOrders.Add(order);
                initCtx.RentalOperationReceipts.Add(receipt);
                await initCtx.SaveChangesAsync();
            }

            var fakePayment = new FakePaymentRentalClient();
            fakePayment.OnCreateInvoice = _ =>
            {
                // Concurrent writer in another context updates the order before issuer commits
                using var conflictCtx = new RentalServiceDbContext(options);
                var conflictOrder = conflictCtx.RentalOrders.First(o => o.Id == orderId);
                conflictOrder.MarkPaymentExpired(Now.AddMinutes(31), "expiry-conflict");
                conflictOrder.RecordReadModelUpdate(Now.AddMinutes(31));
                conflictCtx.SaveChanges();
            };

            await using (var execCtx = new RentalServiceDbContext(options))
            {
                var executor = CreatePostgresExecutor(execCtx, locks, fakePayment);
                var desc = Assert.Single(await new OperationReceiptRepository(execCtx).GetPendingOperationDescriptorsAsync(Now, 50, unlimitedAttemptActions: ["CreateInvoice"]));
                var res = await executor.ExecuteScheduledOperationAsync(desc);
                Assert.True(res.IsSuccess);
            }

            // Verify in database: Order remains Expired, receipt was closed, no graph corruption
            await using (var verifyCtx = new RentalServiceDbContext(options))
            {
                var order = await verifyCtx.RentalOrders.FirstAsync(o => o.Id == orderId);
                Assert.Equal(RentalOrderStatus.Expired, order.Status);

                var receipt = await verifyCtx.RentalOperationReceipts.FirstAsync(r => r.OperationId == opKey);
                Assert.Equal(RentalOperationReceipt.StatusFailed, receipt.Status);
                Assert.Equal(RentalOperationReceipt.ClassificationBusiness, receipt.ErrorClassification);
                Assert.Equal("ORDER_NOT_PENDING", receipt.ErrorCode);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }
    [Fact]
    public async Task AtomicPersistence_InvoiceReceiptAndOperation_AllOrNothingOnPostgreSql()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }

        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            DbContextOptions<RentalServiceDbContext> options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            Guid orderId = Guid.NewGuid();
            string opKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", orderId.ToString());
            string opId = "op-atomic-test";

            await using (var initCtx = new RentalServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();

                var items = new[]
                {
                    new InitialRentalItemDto(
                        Guid.NewGuid(), _supplierId, _listingId, 2,
                        new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18),
                        "Camera", null, "Delivery", 500_000m, 0m, "grp-1",
                        [
                            new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
                            new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
                        ],
                        Dprental.RentalService.Api.Tests.Domain.TestListingSnapshots.Default)
                };
                var order = RentalOrder.Create(
                    orderId, "DH-2026-000088", _renterId, "VND", "Delivery",
                    "Customer", "0900000000", "Addr", null, null,
                    "v1.0", "terms.pdf", "hash", opId, Now, items,
                    paymentDeadline: Now.AddMinutes(30)).Value;
                var receipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(), orderId, opKey, "CreateInvoice", "fp", Now);
                var checkoutOp = new RentalCheckoutOperation(
                    opId, _renterId, "fp", orderId, Now.AddMinutes(30), "{}", "[]", Now);
                checkoutOp.MarkSucceeded(Now, invoiceStatus: "Pending");

                initCtx.RentalOrders.Add(order);
                initCtx.RentalOperationReceipts.Add(receipt);
                initCtx.RentalCheckoutOperations.Add(checkoutOp);
                await initCtx.SaveChangesAsync();
            }

            // 1. Success case: All 3 records committed atomically
            await using (var execCtx = new RentalServiceDbContext(options))
            {
                var orders = new RentalOrderRepository(execCtx);
                var operations = new RentalCheckoutOperationRepository(execCtx);
                var issuer = new CheckoutInvoiceIssuer(_payment, orders, operations, execCtx, new FixedTimeProvider(Now), NullLogger<CheckoutInvoiceIssuer>.Instance);

                var order = await orders.GetByIdWithDetailsAsync(orderId);
                var res = await issuer.EnsureInvoiceAsync(order!);
                Assert.True(res.IsSuccess);
            }

            await using (var verifyCtx = new RentalServiceDbContext(options))
            {
                var order = await verifyCtx.RentalOrders.FirstAsync(o => o.Id == orderId);
                var receipt = await verifyCtx.RentalOperationReceipts.FirstAsync(r => r.OperationId == opKey);
                var op = await verifyCtx.RentalCheckoutOperations.FirstAsync(o => o.OperationId == opId);

                Assert.Equal("INV-1", order.InvoiceRef);
                Assert.True(receipt.IsSuccess);
                Assert.Equal(RentalOperationReceipt.StatusSucceeded, receipt.Status);
                Assert.Equal("Ready", op.InvoiceStatus);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }


    private RentalOperationExecutor CreatePostgresExecutor(
        RentalServiceDbContext context,
        IOrderLockManager locks,
        IPaymentRentalClient? paymentClient = null,
        TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? new FixedTimeProvider(Now);
        var orders = new RentalOrderRepository(context);
        var receipts = new OperationReceiptRepository(context);
        var operations = new RentalCheckoutOperationRepository(context);
        var outbox = new OutboxRepository(context);
        var config = new ConfigurationBuilder().Build();
        var pay = paymentClient ?? _payment;
        var issuer = new CheckoutInvoiceIssuer(pay, orders, operations, context, clock, NullLogger<CheckoutInvoiceIssuer>.Instance);

        return new RentalOperationExecutor(
            orders, receipts, outbox, locks, _inventory, pay,
            inventoryCommands: null!, unitOfWork: context, configuration: config,
            logger: NullLogger<RentalOperationExecutor>.Instance,
            invoiceIssuer: issuer, checkoutOperationRepo: operations,
            timeProvider: clock);
    }



    private async Task<Guid> SeedAsync(DbContextOptions<RentalServiceDbContext> options)
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, Now).Value;
        Guid cartItemId = cart.AddItem(
            Guid.NewGuid(), _listingId, _supplierId, 2, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 14), Now).Value.Id;

        await using RentalServiceDbContext context = new(options);
        await context.Database.MigrateAsync();
        context.RentalCarts.Add(cart);
        await context.SaveChangesAsync();

        _listings.Listings[_listingId] = TestListings.Create(_listingId, _supplierId, "Sony A7 IV", 500_000m, thumbnailMediaId: Guid.NewGuid());
        _listings.AvailableQuantities[_listingId] = 10;

        return cartItemId;
    }

    private async Task<CheckoutResult> SubmitAsync(
        DbContextOptions<RentalServiceDbContext> options,
        PostgresOrderLockManager locks,
        CheckoutRequest request,
        CheckoutCompletion completion)
    {
        CheckoutSubmitResult accepted;
        await using (RentalServiceDbContext context = new(options))
        {
            accepted = await NewService(context, locks).SubmitAsync(new(_renterId, true), request, Key);
        }

        return accepted.IsFailure
            ? accepted.Failure!
            : await completion.CompleteAsync(_renterId, Key);
    }

    private CheckoutService NewService(RentalServiceDbContext context, PostgresOrderLockManager locks)
    {
        RentalOrderRepository orders = new(context);
        RentalCartRepository carts = new(context);
        OutboxRepository outbox = new(context);
        FixedTimeProvider clock = new(Now);
        InventoryCommandProducer inventoryCommands = new(outbox, Microsoft.Extensions.Options.Options.Create(new KafkaOptions { Enabled = true }), clock);

        return new CheckoutService(
            new CheckoutPreparationService(
                carts,
                orders,
                _listings,
                Microsoft.Extensions.Options.Options.Create(new RentalTermsOptions
                {
                    CurrentVersion = "v1.0",
                    DocumentRef = "terms/v1.0.pdf",
                    ContentHash = "hash123456"
                }),
                clock),
            new RentalCheckoutOperationRepository(context),
            orders,
            locks,
            inventoryCommands,
            context,
            clock);
    }
}
