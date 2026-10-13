using System.Text.Json;
using System.Text.Json.Nodes;
using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.DTOs.Events;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Application.Services;
using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.Domain.Repository;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class PostgresIdempotencyAndConcurrencyTests
{
    private static readonly IOptions<PaymentMerchantOptions> MerchantOptions =
        Microsoft.Extensions.Options.Options.Create(new PaymentMerchantOptions
        {
            Bank = "MBBank",
            Account = "0383799632"
        });
    private static InvoiceService CreateInvoiceService(
        PaymentServiceDbContext ctx,
        IOptions<PaymentMerchantOptions> merchantOptions,
        TimeProvider? timeProvider = null) =>
        CreateInvoiceService(new EfInvoiceRepository(ctx), merchantOptions, timeProvider);

    private static InvoiceService CreateInvoiceService(
        IInvoiceRepository repo,
        IOptions<PaymentMerchantOptions> merchantOptions,
        TimeProvider? timeProvider = null)
    {
        var tp = timeProvider ?? TimeProvider.System;
        var lifecycle = new InvoiceLifecycleService(repo, tp);
        var creation = new InvoiceCreationService(repo, merchantOptions, lifecycle, tp);
        var recording = new PaymentRecordingService(repo, tp);
        return new InvoiceService(creation, lifecycle, recording);
    }


    private const string InvoicePaidEventSchemaJson = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "InvoicePaidEventEnvelope",
      "type": "object",
      "required": [
        "messageId",
        "messageType",
        "version",
        "producer",
        "aggregateId",
        "occurredAt",
        "correlationId",
        "payload"
      ],
      "properties": {
        "messageId": { "type": "string", "format": "uuid" },
        "messageType": { "type": "string", "const": "InvoicePaid" },
        "version": { "type": "integer", "const": 1 },
        "producer": { "type": "string" },
        "aggregateId": { "type": "string", "format": "uuid" },
        "occurredAt": { "type": "string", "format": "date-time" },
        "correlationId": { "type": "string" },
        "causationId": { "type": ["string", "null"], "format": "uuid" },
        "payload": {
          "type": "object",
          "required": [
            "rentalOrderId",
            "renterId",
            "invoiceRef",
            "paymentRef",
            "paidAmount",
            "currency",
            "paidAt"
          ],
          "properties": {
            "rentalOrderId": { "type": "string", "format": "uuid" },
            "renterId": { "type": "string", "format": "uuid" },
            "invoiceRef": { "type": "string" },
            "paymentRef": { "type": "string" },
            "paidAmount": { "type": "integer", "minimum": 0 },
            "currency": { "type": "string", "minLength": 3, "maxLength": 3 },
            "paidAt": { "type": "string", "format": "date-time" }
          }
        }
      }
    }
    """;

    internal static void AssertValidInvoicePaidOutboxJson(string jsonPayload)
    {
        var schema = JsonSchema.FromText(InvoicePaidEventSchemaJson);
        using var doc = JsonDocument.Parse(jsonPayload);

        var evalResult = schema.Evaluate(doc.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true
        });

        Assert.True(evalResult.IsValid, $"Outbox JSON failed InvoicePaid v1 schema validation: {jsonPayload}");
    }

    [Fact]
    public async Task CreateInvoice_SequentialReplay_ReturnsExistingWithIsReplayTrue()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new PaymentServiceDbContext(options);
            await ctx.Database.MigrateAsync();

            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
            var service = CreateInvoiceService(ctx, MerchantOptions, fakeTime);

            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            var req = new CreateInvoiceRequest(
                orderId, "ORD-SEQ-001", renterId, "VND", fakeTime.GetUtcNow().AddMinutes(30),
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 500_000m)],
                "op-seq-001");

            var firstRes = await service.CreateInvoiceAsync(req);
            Assert.True(firstRes.IsSuccess);
            Assert.False(firstRes.Value.IsReplay);
            Assert.Equal(1_000_000m, firstRes.Value.TotalAmount);

            // Replay with exact same key and payload
            var replayRes = await service.CreateInvoiceAsync(req);
            Assert.True(replayRes.IsSuccess);
            Assert.True(replayRes.Value.IsReplay);
            Assert.Equal(firstRes.Value.InvoiceRef, replayRes.Value.InvoiceRef);
            Assert.Equal(firstRes.Value.TotalAmount, replayRes.Value.TotalAmount);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task CreateInvoice_WithPlus07OffsetDeadline_PersistsUtcAndReplaysWithEquivalentUtc_AndRecordsPaymentWithOffset()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new PaymentServiceDbContext(options);
            await ctx.Database.MigrateAsync();

            var offsetPlus7 = TimeSpan.FromHours(7);
            var nowPlus7 = new DateTimeOffset(2026, 10, 12, 17, 0, 0, offsetPlus7);
            var deadlinePlus7 = new DateTimeOffset(2026, 10, 12, 17, 30, 0, offsetPlus7);
            var fakeTime = new FakeTimeProvider(nowPlus7.ToUniversalTime());
            var service = CreateInvoiceService(ctx, MerchantOptions, fakeTime);

            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid itemId = Guid.NewGuid();
            Guid supplierId = Guid.NewGuid();

            var reqPlus7 = new CreateInvoiceRequest(
                orderId, "ORD-TZ-001", renterId, "VND", deadlinePlus7,
                [new CreateInvoiceItemRequest(itemId, supplierId, 500_000m, 1_000_000m)],
                "op-tz-001");

            var createRes = await service.CreateInvoiceAsync(reqPlus7);
            Assert.True(createRes.IsSuccess);
            Assert.False(createRes.Value.IsReplay);
            Assert.Equal(TimeSpan.Zero, createRes.Value.PaymentDeadline.Offset);
            Assert.Equal(deadlinePlus7.ToUniversalTime(), createRes.Value.PaymentDeadline);

            // Replay with equivalent UTC deadline
            var reqUtc = new CreateInvoiceRequest(
                orderId, "ORD-TZ-001", renterId, "VND", deadlinePlus7.ToUniversalTime(),
                [new CreateInvoiceItemRequest(itemId, supplierId, 500_000m, 1_000_000m)],
                "op-tz-001");

            var replayRes = await service.CreateInvoiceAsync(reqUtc);
            Assert.True(replayRes.IsSuccess);
            Assert.True(replayRes.Value.IsReplay);
            Assert.Equal(createRes.Value.InvoiceRef, replayRes.Value.InvoiceRef);

            // RecordPayment with +07:00 offset PaidAt
            var paidAtPlus7 = new DateTimeOffset(2026, 10, 12, 17, 15, 0, offsetPlus7);
            var payRes = await service.RecordPaymentReceivedAsync(
                createRes.Value.InvoiceRef, "PAY-TZ-001", 1_500_000m, "VND", paidAtPlus7);
            Assert.True(payRes.IsSuccess);

            var dbInvoice = await ctx.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceRef == createRes.Value.InvoiceRef);
            Assert.Equal(InvoiceStatus.Paid, dbInvoice.Status);
            Assert.Equal(paidAtPlus7.ToUniversalTime(), dbInvoice.PaidAt);

            var outbox = await ctx.OutboxMessages.AsNoTracking().SingleAsync(o => o.AggregateId == orderId.ToString("D"));
            AssertValidInvoicePaidOutboxJson(outbox.Payload);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task CreateInvoice_ReplayAfterDeadlinePassed_UpdatesStatusToExpiredAndReturnsReplayWithoutAddingRows()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new PaymentServiceDbContext(options);
            await ctx.Database.MigrateAsync();

            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
            var service = CreateInvoiceService(ctx, MerchantOptions, fakeTime);

            Guid orderId = Guid.NewGuid();
            var req = new CreateInvoiceRequest(
                orderId, "ORD-DEADLINE-001", Guid.NewGuid(), "VND", fakeTime.GetUtcNow().AddMinutes(30),
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                "op-deadline-001");

            var firstRes = await service.CreateInvoiceAsync(req);
            Assert.True(firstRes.IsSuccess);
            Assert.False(firstRes.Value.IsReplay);

            // Advance time past deadline
            fakeTime.Advance(TimeSpan.FromHours(2));

            // Replay should trigger expiry under row lock and still return replay response
            var replayRes = await service.CreateInvoiceAsync(req);
            Assert.True(replayRes.IsSuccess);
            Assert.True(replayRes.Value.IsReplay);
            Assert.Equal(firstRes.Value.InvoiceRef, replayRes.Value.InvoiceRef);

            // Verify invoice in DB was updated to EXPIRED and no extra invoice/receipt was added
            var dbInvoice = await ctx.Invoices.AsNoTracking().SingleAsync(i => i.RentalOrderId == orderId);
            Assert.Equal(InvoiceStatus.Expired, dbInvoice.Status);
            Assert.Equal(fakeTime.GetUtcNow(), dbInvoice.ExpiredAt);
            Assert.Equal(1, await ctx.Invoices.CountAsync(i => i.RentalOrderId == orderId));
            Assert.Equal(1, await ctx.OperationReceipts.CountAsync(r => r.RentalOrderId == orderId));
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task RecordPayment_BeforeAtAndAfterDeadline_EnforcesDeadlineAndPersistsExpired()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new PaymentServiceDbContext(options);
            await ctx.Database.MigrateAsync();

            var startTime = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
            var deadline = startTime.AddMinutes(30);
            var fakeTime = new FakeTimeProvider(startTime);
            var service = CreateInvoiceService(ctx, MerchantOptions, fakeTime);

            // Case 1: PaidAt before deadline -> succeeds, emits valid InvoicePaid outbox, and replays even after deadline
            Guid order1 = Guid.NewGuid();
            var inv1 = (await service.CreateInvoiceAsync(new CreateInvoiceRequest(
                order1, "ORD-DL-BEFORE", Guid.NewGuid(), "VND", deadline,
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 300_000m, 0m)],
                "op-dl-1"))).Value;

            var payBefore = await service.RecordPaymentReceivedAsync(inv1.InvoiceRef, "PAY-DL-1", 300_000m, "VND", deadline.AddSeconds(-1));
            Assert.True(payBefore.IsSuccess);

            //Case 2: PaidAt == deadline while current time < deadline -> rejected as INVOICE_EXPIRED, no outbox
            Guid order2 = Guid.NewGuid();
            var inv2 = (await service.CreateInvoiceAsync(new CreateInvoiceRequest(
                order2, "ORD-DL-EXACT", Guid.NewGuid(), "VND", deadline,
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 300_000m, 0m)],
                "op-dl-2"))).Value;

            var payAtDeadline = await service.RecordPaymentReceivedAsync(inv2.InvoiceRef, "PAY-DL-2", 300_000m, "VND", deadline);
            Assert.True(payAtDeadline.IsFailure);
            Assert.Equal("INVOICE_EXPIRED", payAtDeadline.Error.Code);
            Assert.Equal(0, await ctx.OutboxMessages.CountAsync(o => o.AggregateId == order2.ToString("D")));

            // Case 3: Current time has passed deadline -> persists EXPIRED on the invoice, emits no outbox
            Guid order3 = Guid.NewGuid();
            var inv3 = (await service.CreateInvoiceAsync(new CreateInvoiceRequest(
                order3, "ORD-DL-AFTER", Guid.NewGuid(), "VND", deadline,
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 300_000m, 0m)],
                "op-dl-3"))).Value;

            fakeTime.Advance(TimeSpan.FromMinutes(35)); // now = startTime + 35m > deadline

            var payAfterDeadline = await service.RecordPaymentReceivedAsync(inv3.InvoiceRef, "PAY-DL-3", 300_000m, "VND", fakeTime.GetUtcNow());
            Assert.True(payAfterDeadline.IsFailure);
            Assert.Equal("INVOICE_EXPIRED", payAfterDeadline.Error.Code);

            var dbInv3 = await ctx.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceRef == inv3.InvoiceRef);
            Assert.Equal(InvoiceStatus.Expired, dbInv3.Status);
            Assert.NotNull(dbInv3.ExpiredAt);
            Assert.Equal(0, await ctx.OutboxMessages.CountAsync(o => o.AggregateId == order3.ToString("D")));

            // Replay on already-PAID invoice (inv1) after deadline still succeeds idempotently
            var paidReplay = await service.RecordPaymentReceivedAsync(inv1.InvoiceRef, "PAY-DL-1", 300_000m, "VND", fakeTime.GetUtcNow());
            Assert.True(paidReplay.IsSuccess);
            Assert.Equal(1, await ctx.OutboxMessages.CountAsync(o => o.AggregateId == order1.ToString("D")));
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task CreateInvoice_ConflictingPayload_ReturnsDuplicateOperationKey()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new PaymentServiceDbContext(options);
            await ctx.Database.MigrateAsync();

            var service = CreateInvoiceService(ctx, MerchantOptions);
            string opKey = "op-conflict-001";
            var deadline = DateTimeOffset.UtcNow.AddMinutes(30);

            var req1 = new CreateInvoiceRequest(
                Guid.NewGuid(), "ORD-001", Guid.NewGuid(), "VND", deadline,
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                opKey);

            var req2 = new CreateInvoiceRequest(
                Guid.NewGuid(), "ORD-002", Guid.NewGuid(), "VND", deadline,
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 800_000m, 0m)],
                opKey);

            var res1 = await service.CreateInvoiceAsync(req1);
            Assert.True(res1.IsSuccess);

            var res2 = await service.CreateInvoiceAsync(req2);
            Assert.True(res2.IsFailure);
            Assert.Equal("DUPLICATE_OPERATION_KEY", res2.Error.Code);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task CreateInvoice_SameRentalOrderIdWithDifferentKey_ReturnsInvoiceAlreadyExists()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new PaymentServiceDbContext(options);
            await ctx.Database.MigrateAsync();

            var service = CreateInvoiceService(ctx, MerchantOptions);
            Guid sharedOrderId = Guid.NewGuid();
            var deadline = DateTimeOffset.UtcNow.AddMinutes(30);

            var req1 = new CreateInvoiceRequest(
                sharedOrderId, "ORD-001", Guid.NewGuid(), "VND", deadline,
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                "op-key-1");

            var req2 = new CreateInvoiceRequest(
                sharedOrderId, "ORD-001", Guid.NewGuid(), "VND", deadline,
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                "op-key-2");

            var res1 = await service.CreateInvoiceAsync(req1);
            Assert.True(res1.IsSuccess);

            var res2 = await service.CreateInvoiceAsync(req2);
            Assert.True(res2.IsFailure);
            Assert.Equal("INVOICE_ALREADY_EXISTS", res2.Error.Code);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task CreateInvoice_ConcurrentRequests_OnlyOneWinsAndOthersReplay()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            var deadline = DateTimeOffset.UtcNow.AddMinutes(30);
            var req = new CreateInvoiceRequest(
                orderId, "ORD-CONC-001", renterId, "VND", deadline,
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 400_000m, 200_000m)],
                "op-conc-key-1");

            int parallelism = 5;
            var tasks = Enumerable.Range(0, parallelism).Select(async _ =>
            {
                await using var threadCtx = new PaymentServiceDbContext(options);
                var svc = CreateInvoiceService(threadCtx, MerchantOptions);
                return await svc.CreateInvoiceAsync(req);
            }).ToArray();

            var results = await Task.WhenAll(tasks);

            int creations = results.Count(r => r.IsSuccess && !r.Value.IsReplay);
            int replays = results.Count(r => r.IsSuccess && r.Value.IsReplay);

            Assert.Equal(1, creations);
            Assert.Equal(parallelism - 1, replays);

            // Verify exactly 1 invoice in database
            await using var verifyCtx = new PaymentServiceDbContext(options);
            int invoiceCount = await verifyCtx.Invoices.CountAsync(i => i.RentalOrderId == orderId);
            int receiptCount = await verifyCtx.OperationReceipts.CountAsync(r => r.RentalOrderId == orderId);

            Assert.Equal(1, invoiceCount);
            Assert.Equal(1, receiptCount);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task CreateInvoice_ZeroTotal_CreatesPaidInvoiceWithOneOutboxEnvelope_ReplayDoesNotDuplicate()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new PaymentServiceDbContext(options);
            await ctx.Database.MigrateAsync();

            var service = CreateInvoiceService(ctx, MerchantOptions);
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            var req = new CreateInvoiceRequest(
                orderId, "ORD-ZERO-001", renterId, "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 0m, 0m)],
                "op-zero-001");

            var first = await service.CreateInvoiceAsync(req);
            Assert.True(first.IsSuccess);
            Assert.False(first.Value.IsReplay);

            // Replay
            var second = await service.CreateInvoiceAsync(req);
            Assert.True(second.IsSuccess);
            Assert.True(second.Value.IsReplay);

            // Verify invoice is PAID
            var invoice = await ctx.Invoices.FirstAsync(i => i.RentalOrderId == orderId);
            Assert.Equal(InvoiceStatus.Paid, invoice.Status);
            Assert.Equal(0m, invoice.TotalAmount);
            Assert.NotNull(invoice.PaymentRef);

            // Verify outbox message against InvoicePaid v1 JSON Schema
            var outboxMessages = await ctx.OutboxMessages.Where(o => o.AggregateId == orderId.ToString("D")).ToListAsync();
            Assert.Single(outboxMessages);
            AssertValidInvoicePaidOutboxJson(outboxMessages[0].Payload);

            var envelope = JsonSerializer.Deserialize<PaymentEventEnvelope<InvoicePaidPayload>>(outboxMessages[0].Payload);
            Assert.NotNull(envelope);
            Assert.Equal("InvoicePaid", envelope.MessageType);
            Assert.Equal("payment-service", envelope.Producer);
            Assert.Equal(1, envelope.Version);
            Assert.Equal(envelope.MessageId.ToString("D"), envelope.CorrelationId);
            Assert.Equal(0m, envelope.Payload.PaidAmount);
            Assert.Equal("VND", envelope.Payload.Currency);
            Assert.Equal(invoice.InvoiceRef, envelope.Payload.InvoiceRef);
            Assert.Equal(invoice.PaymentRef, envelope.Payload.PaymentRef);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task CancelInvoice_ExpiredInvoice_AcknowledgesClosureAndKeepsExpiredStatus()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new PaymentServiceDbContext(options);
            await ctx.Database.MigrateAsync();

            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
            var service = CreateInvoiceService(ctx, MerchantOptions, fakeTime);

            Guid orderId = Guid.NewGuid();
            var req = new CreateInvoiceRequest(
                orderId, "ORD-EXP-001", Guid.NewGuid(), "VND", fakeTime.GetUtcNow().AddMinutes(30),
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 200_000m, 0m)],
                "op-create-exp");

            var createRes = await service.CreateInvoiceAsync(req);
            Assert.True(createRes.IsSuccess);

            // Fast forward time past deadline
            fakeTime.Advance(TimeSpan.FromHours(1));

            var cancelReq = new CancelInvoiceRequest(orderId, createRes.Value.InvoiceRef, "Expired by timeout", "op-cancel-exp");
            var cancelRes = await service.CancelInvoiceAsync(createRes.Value.InvoiceRef, cancelReq);

            Assert.True(cancelRes.IsSuccess);
            Assert.True(cancelRes.Value.Cancelled);
            Assert.Equal(fakeTime.GetUtcNow(), cancelRes.Value.CancelledAt);

            // Verify status in DB is EXPIRED
            var invoice = await ctx.Invoices.AsNoTracking().FirstAsync(i => i.InvoiceRef == createRes.Value.InvoiceRef);
            Assert.Equal(InvoiceStatus.Expired, invoice.Status);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task CoordinatedRace_GetReadsPending_PaymentCommits_GetAcquiresLock_KeepsPaidAndHidesQr()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            var startTime = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
            var deadline = startTime.AddMinutes(30);
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            string invoiceRef;

            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var setupTime = new FakeTimeProvider(startTime);
                var setupSvc = CreateInvoiceService(setupCtx, MerchantOptions, setupTime);
                var createRes = await setupSvc.CreateInvoiceAsync(new CreateInvoiceRequest(
                    orderId, "ORD-GET-RACE-001", renterId, "VND", deadline,
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-get-race-create"));
                invoiceRef = createRes.Value.InvoiceRef;
            }

            // Prepare GET service at a time just past deadline so it attempts on-read expiry after reading PENDING
            await using var getCtx = new PaymentServiceDbContext(options);
            var getTime = new FakeTimeProvider(deadline.AddSeconds(5));
            var getRepo = new TestInvoiceRepositoryDecorator(new EfInvoiceRepository(getCtx));
            var getSvc = CreateInvoiceService(getRepo, MerchantOptions, getTime);

            // When GET has already read PENDING (AsNoTracking) and is about to acquire the row lock,
            // commit the payment in a separate DbContext!
            getRepo.OnBeforeFindInvoiceWithRowLock = async ct =>
            {
                await using var payCtx = new PaymentServiceDbContext(options);
                var payTime = new FakeTimeProvider(deadline.AddSeconds(-10));
                var paySvc = CreateInvoiceService(payCtx, MerchantOptions, payTime);
                var payRes = await paySvc.RecordPaymentReceivedAsync(
                    invoiceRef, "PAY-DURING-GET-001", 500_000m, "VND", deadline.AddSeconds(-10), ct);
                Assert.True(payRes.IsSuccess);
            };

            var getRes = await getSvc.GetInvoiceAsync(invoiceRef, renterId);
            Assert.True(getRes.IsSuccess);
            Assert.Equal(InvoiceStatus.Paid, getRes.Value.Status);
            Assert.Null(getRes.Value.QrUrl);

            // Verify database kept PAID and was NOT overwritten to EXPIRED
            await using var verifyCtx = new PaymentServiceDbContext(options);
            var dbInvoice = await verifyCtx.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceRef == invoiceRef);
            Assert.Equal(InvoiceStatus.Paid, dbInvoice.Status);
            Assert.Equal("PAY-DURING-GET-001", dbInvoice.PaymentRef);
            Assert.Null(dbInvoice.ExpiredAt);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task ConcurrentDuplicatePaymentRef_AcrossTwoInvoices_OnlyOnePaidAndOneOutbox()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            var now = DateTimeOffset.UtcNow;
            var deadline = now.AddMinutes(30);
            string invRef1;
            string invRef2;

            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var setupSvc = CreateInvoiceService(setupCtx, MerchantOptions);
                var res1 = await setupSvc.CreateInvoiceAsync(new CreateInvoiceRequest(
                    Guid.NewGuid(), "ORD-DUP-PAY-1", Guid.NewGuid(), "VND", deadline,
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-dup-pay-1"));
                var res2 = await setupSvc.CreateInvoiceAsync(new CreateInvoiceRequest(
                    Guid.NewGuid(), "ORD-DUP-PAY-2", Guid.NewGuid(), "VND", deadline,
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-dup-pay-2"));

                invRef1 = res1.Value.InvoiceRef;
                invRef2 = res2.Value.InvoiceRef;
            }

            const string sharedPaymentRef = "PAY-CONCURRENT-DUP-001";
            var paidAt = now.AddMinutes(1);

            var task1 = Task.Run(async () =>
            {
                await using var ctx1 = new PaymentServiceDbContext(options);
                var svc1 = CreateInvoiceService(ctx1, MerchantOptions);
                return await svc1.RecordPaymentReceivedAsync(invRef1, sharedPaymentRef, 500_000m, "VND", paidAt);
            });

            var task2 = Task.Run(async () =>
            {
                await using var ctx2 = new PaymentServiceDbContext(options);
                var svc2 = CreateInvoiceService(ctx2, MerchantOptions);
                return await svc2.RecordPaymentReceivedAsync(invRef2, sharedPaymentRef, 500_000m, "VND", paidAt);
            });

            var results = await Task.WhenAll(task1, task2);

            Assert.Equal(1, results.Count(r => r.IsSuccess));
            var failed = Assert.Single(results, r => r.IsFailure);
            Assert.Equal("DUPLICATE_PAYMENT_REF", failed.Error.Code);
            Assert.Equal(ErrorType.Conflict, failed.Error.Type);

            await using var verifyCtx = new PaymentServiceDbContext(options);
            var invoices = await verifyCtx.Invoices
                .AsNoTracking()
                .Where(i => i.InvoiceRef == invRef1 || i.InvoiceRef == invRef2)
                .ToListAsync();

            Assert.Equal(1, invoices.Count(i => i.Status == InvoiceStatus.Paid && i.PaymentRef == sharedPaymentRef));
            Assert.Equal(1, invoices.Count(i => i.Status == InvoiceStatus.Pending && i.PaymentRef == null));
            Assert.Equal(1, await verifyCtx.OutboxMessages.CountAsync());
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Concurrency_CancelAndMarkPaid_SerializedWithRowLocks_DoesNotCorrupt()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            Guid orderId = Guid.NewGuid();
            string invoiceRef;
            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var svc = CreateInvoiceService(setupCtx, MerchantOptions);
                var createRes = await svc.CreateInvoiceAsync(new CreateInvoiceRequest(
                    orderId, "ORD-RACE-001", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 300_000m, 100_000m)],
                    "op-create-race"));
                invoiceRef = createRes.Value.InvoiceRef;
            }

            // Task A tries to Cancel
            var cancelTask = Task.Run(async () =>
            {
                await using var ctxA = new PaymentServiceDbContext(options);
                var svcA = CreateInvoiceService(ctxA, MerchantOptions);
                return await svcA.CancelInvoiceAsync(
                    invoiceRef,
                    new CancelInvoiceRequest(orderId, invoiceRef, "Renter cancel", "op-cancel-race"));
            });

            // Task B tries to MarkPaid (PAY-02 boundary)
            var payTask = Task.Run(async () =>
            {
                await using var ctxB = new PaymentServiceDbContext(options);
                var svcB = CreateInvoiceService(ctxB, MerchantOptions);
                return await svcB.RecordPaymentReceivedAsync(
                    invoiceRef, "PAY-RACE-001", 400_000m, "VND", DateTimeOffset.UtcNow);
            });

            var cancelResult = await cancelTask;
            var payResult = await payTask;

            // One should succeed and the other should either receive conflict or expected terminal status
            await using var verifyCtx = new PaymentServiceDbContext(options);
            var finalInvoice = await verifyCtx.Invoices.AsNoTracking().FirstAsync(i => i.InvoiceRef == invoiceRef);

            Assert.True(finalInvoice.Status is InvoiceStatus.Paid or InvoiceStatus.Cancelled);

            if (finalInvoice.Status == InvoiceStatus.Paid)
            {
                Assert.True(payResult.IsSuccess);
                // If Cancel came second, it must fail with INVOICE_ALREADY_PAID
                if (cancelResult.IsFailure)
                {
                    Assert.Equal("INVOICE_ALREADY_PAID", cancelResult.Error.Code);
                }
            }
            else if (finalInvoice.Status == InvoiceStatus.Cancelled)
            {
                Assert.True(cancelResult.IsSuccess);
                // If Paid came second, it must fail with INVOICE_CANCELLED
                if (payResult.IsFailure)
                {
                    Assert.Equal("INVOICE_CANCELLED", payResult.Error.Code);
                }
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task RecordPayment_BlockedOnRowLockPastDeadline_PersistsExpiredWithoutPaymentRefOrOutbox()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            var startTime = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
            var deadline = startTime.AddMinutes(30);
            var validPaidAt = startTime.AddMinutes(10);
            var fakeTime = new FakeTimeProvider(startTime.AddMinutes(10));

            Guid orderId = Guid.NewGuid();
            string invoiceRef;
            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var setupSvc = CreateInvoiceService(setupCtx, MerchantOptions, fakeTime);
                var createRes = await setupSvc.CreateInvoiceAsync(new CreateInvoiceRequest(
                    orderId, "ORD-LOCK-WAIT-EXP", Guid.NewGuid(), "VND", deadline,
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-lock-wait-exp"));
                invoiceRef = createRes.Value.InvoiceRef;
            }

            // 1. Connection 1 acquires and holds row lock on the PENDING invoice
            await using var holdConn = new NpgsqlConnection(connString);
            await holdConn.OpenAsync();
            int holderPid;
            await using (var pidCmd = holdConn.CreateCommand())
            {
                pidCmd.CommandText = "SELECT pg_backend_pid();";
                holderPid = (int)(await pidCmd.ExecuteScalarAsync())!;
            }

            await using var holdTx = await holdConn.BeginTransactionAsync();
            await using (var lockCmd = holdConn.CreateCommand())
            {
                lockCmd.Transaction = holdTx;
                lockCmd.CommandText = @"SELECT ""Id"" FROM invoices WHERE ""InvoiceRef"" = @ref FOR UPDATE;";
                lockCmd.Parameters.AddWithValue("ref", invoiceRef);
                await lockCmd.ExecuteScalarAsync();
            }

            // 2. Connection 2 starts RecordPaymentReceivedAsync BEFORE deadline with valid PaidAt
            var recordTask = Task.Run(async () =>
            {
                await using var payCtx = new PaymentServiceDbContext(options);
                var paySvc = CreateInvoiceService(payCtx, MerchantOptions, fakeTime);
                return await paySvc.RecordPaymentReceivedAsync(
                    invoiceRef, "PAY-LOCK-WAIT-EXP", 500_000m, "VND", validPaidAt);
            });

            // 3. Confirm Connection 2 is actively blocked on PostgreSQL lock held by holderPid
            await WaitUntilBlockedByPidAsync(connString, holderPid);

            // 4. Advance FakeTimeProvider past deadline while Connection 2 is waiting on the lock, then release lock without changing invoice
            fakeTime.Advance(TimeSpan.FromMinutes(25)); // now = startTime + 35m > deadline
            await holdTx.RollbackAsync();

            var payResult = await recordTask;

            // 5. Assert INVOICE_EXPIRED, DB status EXPIRED, PaymentRef null, and 0 outbox messages
            Assert.True(payResult.IsFailure);
            Assert.Equal("INVOICE_EXPIRED", payResult.Error.Code);

            await using var verifyCtx = new PaymentServiceDbContext(options);
            var dbInvoice = await verifyCtx.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceRef == invoiceRef);
            Assert.Equal(InvoiceStatus.Expired, dbInvoice.Status);
            Assert.Null(dbInvoice.PaymentRef);
            Assert.Null(dbInvoice.PaidAt);
            Assert.Equal(fakeTime.GetUtcNow(), dbInvoice.ExpiredAt);
            Assert.Equal(0, await verifyCtx.OutboxMessages.CountAsync(o => o.AggregateId == orderId.ToString("D")));
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task RecordPayment_BlockedOnRowLockReleasedBeforeDeadline_Succeeds_AndPaidReplayAfterDeadlineDoesNotDuplicateEvent()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            var startTime = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
            var deadline = startTime.AddMinutes(30);
            var validPaidAt = startTime.AddMinutes(10);
            var fakeTime = new FakeTimeProvider(startTime.AddMinutes(10));

            Guid orderId = Guid.NewGuid();
            string invoiceRef;
            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var setupSvc = CreateInvoiceService(setupCtx, MerchantOptions, fakeTime);
                var createRes = await setupSvc.CreateInvoiceAsync(new CreateInvoiceRequest(
                    orderId, "ORD-LOCK-WAIT-OK", Guid.NewGuid(), "VND", deadline,
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-lock-wait-ok"));
                invoiceRef = createRes.Value.InvoiceRef;
            }

            // 1. Connection 1 acquires and holds row lock on the PENDING invoice
            await using var holdConn = new NpgsqlConnection(connString);
            await holdConn.OpenAsync();
            int holderPid;
            await using (var pidCmd = holdConn.CreateCommand())
            {
                pidCmd.CommandText = "SELECT pg_backend_pid();";
                holderPid = (int)(await pidCmd.ExecuteScalarAsync())!;
            }

            await using var holdTx = await holdConn.BeginTransactionAsync();
            await using (var lockCmd = holdConn.CreateCommand())
            {
                lockCmd.Transaction = holdTx;
                lockCmd.CommandText = @"SELECT ""Id"" FROM invoices WHERE ""InvoiceRef"" = @ref FOR UPDATE;";
                lockCmd.Parameters.AddWithValue("ref", invoiceRef);
                await lockCmd.ExecuteScalarAsync();
            }

            // 2. Connection 2 starts RecordPaymentReceivedAsync BEFORE deadline
            var recordTask = Task.Run(async () =>
            {
                await using var payCtx = new PaymentServiceDbContext(options);
                var paySvc = CreateInvoiceService(payCtx, MerchantOptions, fakeTime);
                return await paySvc.RecordPaymentReceivedAsync(
                    invoiceRef, "PAY-LOCK-WAIT-OK", 500_000m, "VND", validPaidAt);
            });

            // 3. Confirm Connection 2 is actively blocked on PostgreSQL lock
            await WaitUntilBlockedByPidAsync(connString, holderPid);

            // 4. Advance time slightly (still before deadline = startTime + 30m) and release lock
            fakeTime.Advance(TimeSpan.FromMinutes(5)); // now = startTime + 15m < deadline
            await holdTx.RollbackAsync();

            var payResult = await recordTask;
            Assert.True(payResult.IsSuccess);

            await using var verifyCtx = new PaymentServiceDbContext(options);
            var dbInvoice = await verifyCtx.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceRef == invoiceRef);
            Assert.Equal(InvoiceStatus.Paid, dbInvoice.Status);
            Assert.Equal("PAY-LOCK-WAIT-OK", dbInvoice.PaymentRef);
            Assert.Equal(1, await verifyCtx.OutboxMessages.CountAsync(o => o.AggregateId == orderId.ToString("D")));

            // 5. Advance time past deadline and replay RecordPaymentReceivedAsync: must still succeed without adding outbox
            fakeTime.Advance(TimeSpan.FromMinutes(30)); // now = startTime + 45m > deadline
            var replaySvc = CreateInvoiceService(verifyCtx, MerchantOptions, fakeTime);
            var replayRes = await replaySvc.RecordPaymentReceivedAsync(
                invoiceRef, "PAY-LOCK-WAIT-OK", 500_000m, "VND", validPaidAt);
            Assert.True(replayRes.IsSuccess);
            Assert.Equal(1, await verifyCtx.OutboxMessages.CountAsync(o => o.AggregateId == orderId.ToString("D")));
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    private static async Task WaitUntilBlockedByPidAsync(string connString, int holderPid)
    {
        await using var monitorConn = new NpgsqlConnection(connString);
        await monitorConn.OpenAsync();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var cmd = monitorConn.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*)
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND @holderPid = ANY(pg_blocking_pids(pid));";
            cmd.Parameters.AddWithValue("holderPid", holderPid);

            long blockedCount = (long)(await cmd.ExecuteScalarAsync())!;
            if (blockedCount >= 1)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Timed out waiting for a PostgreSQL session to become blocked by PID {holderPid}.");
    }
}
