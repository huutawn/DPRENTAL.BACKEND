using System.Text.Json;
using System.Security.Cryptography;
using System.Globalization;
using Dprental.PaymentService.Application.DTOs.SePay;
using System.Text.Json.Nodes;
using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.DTOs.Events;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Application.Services;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;
using Dprental.PaymentService.Domain.Repositories;
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
    private static InvoiceCreationService CreateCreationService(
        PaymentServiceDbContext ctx,
        IOptions<PaymentMerchantOptions> merchantOptions,
        TimeProvider? timeProvider = null) =>
        CreateCreationService(new EfInvoiceRepository(ctx), merchantOptions, timeProvider);

    private static readonly IPaymentDataEncryptor TestEncryptor =
        new PaymentDataEncryptor(RandomNumberGenerator.GetBytes(32), "test-enc-key");

    private static InvoiceCreationService CreateCreationService(
        IInvoiceRepository repo,
        IOptions<PaymentMerchantOptions> merchantOptions,
        TimeProvider? timeProvider = null)
    {
        var tp = timeProvider ?? TimeProvider.System;
        var lifecycle = new InvoiceLifecycleService(repo, null, TestEncryptor, tp);
        return new InvoiceCreationService(repo, merchantOptions, lifecycle, TestEncryptor, tp);
    }

    private static InvoiceLifecycleService CreateLifecycleService(
        PaymentServiceDbContext ctx,
        TimeProvider? timeProvider = null) =>
        CreateLifecycleService(new EfInvoiceRepository(ctx), timeProvider);

    private static InvoiceLifecycleService CreateLifecycleService(
        IInvoiceRepository repo,
        TimeProvider? timeProvider = null) =>
        new(repo, null, TestEncryptor, timeProvider ?? TimeProvider.System);

    private static InvoicePaymentProcessor CreatePaymentProcessor(
        IInvoiceRepository repo,
        TimeProvider? timeProvider = null,
        IOptionsMonitor<PaymentMerchantOptions>? merchantOptions = null,
        IOptionsMonitor<SePayOptions>? sePayOptions = null)
    {
        var tp = timeProvider ?? TimeProvider.System;
        var mo = merchantOptions ?? new TestOptionsMonitor<PaymentMerchantOptions>(MerchantOptions.Value);
        var so = sePayOptions ?? new TestOptionsMonitor<SePayOptions>(new SePayOptions
        {
            ApiKey = "test-sepay-key",
            Gateway = "MBBank",
            AccountNumber = "0383799632",
            GatewayMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MBBank"] = "MBBank",
                ["MB"] = "MBBank"
            }
        });
        return new InvoicePaymentProcessor(repo, mo, so, null, TestEncryptor, tp);
    }
    private static SePayWebhookPayload CreatePayload(
        long id,
        string code,
        decimal amount,
        DateTimeOffset? txDate = null,
        string gateway = "MBBank",
        string account = "0383799632",
        string transferType = "in",
        string? content = null)
    {
        var date = txDate ?? DateTimeOffset.UtcNow;
        return new SePayWebhookPayload(
            Id: id,
            Gateway: gateway,
            TransactionDate: date.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            AccountNumber: account,
            SubAccount: null,
            Code: code,
            Content: content ?? code,
            TransferType: transferType,
            Description: "Payment",
            TransferAmount: amount,
            Accumulated: 10_000_000m,
            ReferenceCode: $"REF-{id}");
    }


    private sealed class TestOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;
        public T Get(string? name) => currentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
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
            var service = CreateCreationService(ctx, MerchantOptions, fakeTime);

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
            var service = CreateCreationService(ctx, MerchantOptions, fakeTime);

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

            // Record payment with +07:00 offset TransactionDate via processor
            var paidAtPlus7 = new DateTimeOffset(2026, 10, 12, 17, 15, 0, offsetPlus7);
            var dbInvoiceBefore = await ctx.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceRef == createRes.Value.InvoiceRef);
            var processor = CreatePaymentProcessor(new EfInvoiceRepository(ctx), fakeTime);
            var vt = CreatePayload(1001, dbInvoiceBefore.PaymentCode, 1_500_000m, txDate: paidAtPlus7);
            var payRes = await processor.ProcessAsync(vt);
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
            var service = CreateCreationService(ctx, MerchantOptions, fakeTime);

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
            var service = CreateCreationService(ctx, MerchantOptions, fakeTime);

            // Case 1: PaidAt before deadline -> succeeds, emits valid InvoicePaid outbox, and replays even after deadline
            Guid order1 = Guid.NewGuid();
            var inv1 = (await service.CreateInvoiceAsync(new CreateInvoiceRequest(
                order1, "ORD-DL-BEFORE", Guid.NewGuid(), "VND", deadline,
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 300_000m, 0m)],
                "op-dl-1"))).Value;
            var dbInv1 = await ctx.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceRef == inv1.InvoiceRef);
            var processor1 = CreatePaymentProcessor(new EfInvoiceRepository(ctx), fakeTime);
            var vt1 = CreatePayload(2001, dbInv1.PaymentCode, 300_000m, txDate: deadline.AddSeconds(-1));
            var payBefore = await processor1.ProcessAsync(vt1);
            Assert.True(payBefore.IsSuccess);
            var tx1 = await ctx.PaymentTransactions.AsNoTracking().SingleAsync(t => t.ProviderTransactionId == "2001");
            Assert.Equal(PaymentTransactionStatus.Matched, tx1.Status);

            // Case 2: PaidAt == deadline -> late payment, sets MANUAL_REFUND_REQUIRED, emits 1 InvoicePaid outbox
            Guid order2 = Guid.NewGuid();
            var inv2 = (await service.CreateInvoiceAsync(new CreateInvoiceRequest(
                order2, "ORD-DL-EXACT", Guid.NewGuid(), "VND", deadline,
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 300_000m, 0m)],
                "op-dl-2"))).Value;

            var dbInv2 = await ctx.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceRef == inv2.InvoiceRef);
            var processor2 = CreatePaymentProcessor(new EfInvoiceRepository(ctx), fakeTime);
            var vt2 = CreatePayload(2002, dbInv2.PaymentCode, 300_000m, txDate: deadline);
            var payAtDeadline = await processor2.ProcessAsync(vt2);
            Assert.True(payAtDeadline.IsSuccess);
            var tx2 = await ctx.PaymentTransactions.AsNoTracking().SingleAsync(t => t.ProviderTransactionId == "2002");
            Assert.Equal(PaymentTransactionStatus.ManualRefundRequired, tx2.Status);
            var dbInv2After = await ctx.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceRef == inv2.InvoiceRef);
            Assert.Equal(InvoiceStatus.Expired, dbInv2After.Status);
            Assert.Equal(1, await ctx.OutboxMessages.CountAsync(o => o.AggregateId == order2.ToString("D")));

            // Case 3: Current time has passed deadline -> late payment, sets MANUAL_REFUND_REQUIRED, emits 1 InvoicePaid outbox
            Guid order3 = Guid.NewGuid();
            var inv3 = (await service.CreateInvoiceAsync(new CreateInvoiceRequest(
                order3, "ORD-DL-AFTER", Guid.NewGuid(), "VND", deadline,
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 300_000m, 0m)],
                "op-dl-3"))).Value;

            fakeTime.Advance(TimeSpan.FromMinutes(35)); // now = startTime + 35m > deadline

            var dbInv3 = await ctx.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceRef == inv3.InvoiceRef);
            var processor3 = CreatePaymentProcessor(new EfInvoiceRepository(ctx), fakeTime);
            var vt3 = CreatePayload(2003, dbInv3.PaymentCode, 300_000m, txDate: fakeTime.GetUtcNow());
            var payAfterDeadline = await processor3.ProcessAsync(vt3);
            Assert.True(payAfterDeadline.IsSuccess);
            var tx3 = await ctx.PaymentTransactions.AsNoTracking().SingleAsync(t => t.ProviderTransactionId == "2003");
            Assert.Equal(PaymentTransactionStatus.ManualRefundRequired, tx3.Status);
            var dbInv3After = await ctx.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceRef == inv3.InvoiceRef);
            Assert.Equal(InvoiceStatus.Expired, dbInv3After.Status);
            Assert.Equal(1, await ctx.OutboxMessages.CountAsync(o => o.AggregateId == order3.ToString("D")));

            // Replay on already-PAID invoice (inv1) after deadline still succeeds idempotently
            var paidReplay = await processor1.ProcessAsync(vt1);
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

            var service = CreateCreationService(ctx, MerchantOptions);
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

            var service = CreateCreationService(ctx, MerchantOptions);
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
                var svc = CreateCreationService(threadCtx, MerchantOptions);
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

            var service = CreateCreationService(ctx, MerchantOptions);
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
            var service = CreateCreationService(ctx, MerchantOptions, fakeTime);

            Guid orderId = Guid.NewGuid();
            var req = new CreateInvoiceRequest(
                orderId, "ORD-EXP-001", Guid.NewGuid(), "VND", fakeTime.GetUtcNow().AddMinutes(30),
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 200_000m, 0m)],
                "op-create-exp");

            var createRes = await service.CreateInvoiceAsync(req);
            Assert.True(createRes.IsSuccess);

            // Fast forward time past deadline
            fakeTime.Advance(TimeSpan.FromHours(1));

            var lifecycle = CreateLifecycleService(ctx, fakeTime);
            var cancelReq = new CancelInvoiceRequest(orderId, createRes.Value.InvoiceRef, "Expired by timeout", "op-cancel-exp");
            var cancelRes = await lifecycle.CancelInvoiceAsync(createRes.Value.InvoiceRef, cancelReq);

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
                var setupSvc = CreateCreationService(setupCtx, MerchantOptions, setupTime);
                var createRes = await setupSvc.CreateInvoiceAsync(new CreateInvoiceRequest(
                    orderId, "ORD-GET-RACE-001", renterId, "VND", deadline,
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-get-race-create"));
                invoiceRef = createRes.Value.InvoiceRef;
                string paymentCode = (await setupCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef)).PaymentCode;

                // Prepare GET service at a time just past deadline so it attempts on-read expiry after reading PENDING
                await using var getCtx = new PaymentServiceDbContext(options);
                var getTime = new FakeTimeProvider(deadline.AddSeconds(5));
                var getRepo = new TestInvoiceRepositoryDecorator(new EfInvoiceRepository(getCtx));
                var getSvc = CreateLifecycleService(getRepo, getTime);

                // When GET has already read PENDING (AsNoTracking) and is about to acquire the row lock,
                // commit the payment in a separate DbContext!
                getRepo.OnBeforeFindInvoiceWithRowLock = async ct =>
                {
                    await using var payCtx = new PaymentServiceDbContext(options);
                    var payTime = new FakeTimeProvider(deadline.AddSeconds(-10));
                    var proc = CreatePaymentProcessor(new EfInvoiceRepository(payCtx), payTime);
                    var vt = CreatePayload(4001, paymentCode, 500_000m, txDate: deadline.AddSeconds(-10));
                    var payRes = await proc.ProcessAsync(vt, ct);
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
                Assert.Equal("SEPAY:4001", dbInvoice.PaymentRef);
                Assert.Null(dbInvoice.ExpiredAt);
            }
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
            string code1;
            string code2;

            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var setupSvc = CreateCreationService(setupCtx, MerchantOptions);
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
                code1 = (await setupCtx.Invoices.SingleAsync(i => i.InvoiceRef == invRef1)).PaymentCode;
                code2 = (await setupCtx.Invoices.SingleAsync(i => i.InvoiceRef == invRef2)).PaymentCode;
            }

            var paidAt = now.AddMinutes(1);

            var task1 = Task.Run(async () =>
            {
                await using var ctx1 = new PaymentServiceDbContext(options);
                var proc1 = CreatePaymentProcessor(new EfInvoiceRepository(ctx1));
                var vt1 = CreatePayload(5001, code1, 500_000m, txDate: paidAt);
                return await proc1.ProcessAsync(vt1);
            });

            var task2 = Task.Run(async () =>
            {
                await using var ctx2 = new PaymentServiceDbContext(options);
                var proc2 = CreatePaymentProcessor(new EfInvoiceRepository(ctx2));
                var vt2 = CreatePayload(5001, code2, 500_000m, txDate: paidAt);
                return await proc2.ProcessAsync(vt2);
            });

            var results = await Task.WhenAll(task1, task2);

            Assert.Equal(1, results.Count(r => r.IsSuccess));
            var failed = Assert.Single(results, r => r.IsFailure);
            Assert.Equal(ErrorType.Conflict, failed.Error.Type);

            await using var verifyCtx = new PaymentServiceDbContext(options);
            var invoices = await verifyCtx.Invoices
                .AsNoTracking()
                .Where(i => i.InvoiceRef == invRef1 || i.InvoiceRef == invRef2)
                .ToListAsync();

            Assert.Equal(1, invoices.Count(i => i.Status == InvoiceStatus.Paid && i.PaymentRef == "SEPAY:5001"));
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
            string paymentCode;
            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var svc = CreateCreationService(setupCtx, MerchantOptions);
                var createRes = await svc.CreateInvoiceAsync(new CreateInvoiceRequest(
                    orderId, "ORD-RACE-001", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 300_000m, 100_000m)],
                    "op-create-race"));
                invoiceRef = createRes.Value.InvoiceRef;
                paymentCode = (await setupCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef)).PaymentCode;
            }

            // Task A tries to Cancel
            var cancelTask = Task.Run(async () =>
            {
                await using var ctxA = new PaymentServiceDbContext(options);
                var svcA = CreateLifecycleService(ctxA);
                return await svcA.CancelInvoiceAsync(
                    invoiceRef,
                    new CancelInvoiceRequest(orderId, invoiceRef, "Renter cancel", "op-cancel-race"));
            });

            // Task B tries to process payment
            var payTask = Task.Run(async () =>
            {
                await using var ctxB = new PaymentServiceDbContext(options);
                var procB = CreatePaymentProcessor(new EfInvoiceRepository(ctxB));
                var vt = CreatePayload(6001, paymentCode, 400_000m, txDate: DateTimeOffset.UtcNow);
                return await procB.ProcessAsync(vt);
            });

            var cancelResult = await cancelTask;
            var payResult = await payTask;

            await using var verifyCtx = new PaymentServiceDbContext(options);
            var finalInvoice = await verifyCtx.Invoices.AsNoTracking().FirstAsync(i => i.InvoiceRef == invoiceRef);

            Assert.True(finalInvoice.Status is InvoiceStatus.Paid or InvoiceStatus.Cancelled);

            if (finalInvoice.Status == InvoiceStatus.Paid)
            {
                Assert.True(payResult.IsSuccess);
                if (cancelResult.IsFailure)
                {
                    Assert.Equal("INVOICE_ALREADY_PAID", cancelResult.Error.Code);
                }
            }
            else if (finalInvoice.Status == InvoiceStatus.Cancelled)
            {
                Assert.True(cancelResult.IsSuccess);
                Assert.True(payResult.IsSuccess);
                Assert.Equal("SEPAY:6001", finalInvoice.PaymentRef);
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
            string paymentCode;
            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var setupSvc = CreateCreationService(setupCtx, MerchantOptions, fakeTime);
                var createRes = await setupSvc.CreateInvoiceAsync(new CreateInvoiceRequest(
                    orderId, "ORD-LOCK-WAIT-EXP", Guid.NewGuid(), "VND", deadline,
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-lock-wait-exp"));
                invoiceRef = createRes.Value.InvoiceRef;
                paymentCode = (await setupCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef)).PaymentCode;
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

            // 2. Connection 2 starts payment processing BEFORE deadline with valid PaidAt
            var recordTask = Task.Run(async () =>
            {
                await using var payCtx = new PaymentServiceDbContext(options);
                var proc = CreatePaymentProcessor(new EfInvoiceRepository(payCtx), fakeTime);
                var vt = CreatePayload(7001, paymentCode, 500_000m, txDate: validPaidAt);
                return await proc.ProcessAsync(vt);
            });

            // 3. Confirm Connection 2 is actively blocked on PostgreSQL lock held by holderPid
            await WaitUntilBlockedByPidAsync(connString, holderPid);

            // 4. Advance FakeTimeProvider past deadline while Connection 2 is waiting on the lock, then release lock without changing invoice
            fakeTime.Advance(TimeSpan.FromMinutes(25)); // now = startTime + 35m > deadline
            await holdTx.RollbackAsync();

            var payResult = await recordTask;
            Assert.True(payResult.IsSuccess);

            // 5. Assert DB status EXPIRED, PaymentRef set, transaction is MANUAL_REFUND_REQUIRED, and 1 outbox message
            await using var verifyCtx = new PaymentServiceDbContext(options);
            var dbInvoice = await verifyCtx.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceRef == invoiceRef);
            Assert.Equal(InvoiceStatus.Expired, dbInvoice.Status);
            Assert.Equal("SEPAY:7001", dbInvoice.PaymentRef);
            Assert.Equal(validPaidAt, dbInvoice.PaidAt);
            Assert.Equal(1, await verifyCtx.OutboxMessages.CountAsync(o => o.AggregateId == orderId.ToString("D")));

            var txRecord = await verifyCtx.PaymentTransactions.AsNoTracking().SingleAsync(t => t.ProviderTransactionId == "7001");
            Assert.Equal(PaymentTransactionStatus.ManualRefundRequired, txRecord.Status);
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
            string paymentCode;
            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var setupSvc = CreateCreationService(setupCtx, MerchantOptions, fakeTime);
                var createRes = await setupSvc.CreateInvoiceAsync(new CreateInvoiceRequest(
                    orderId, "ORD-LOCK-WAIT-OK", Guid.NewGuid(), "VND", deadline,
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-lock-wait-ok"));
                invoiceRef = createRes.Value.InvoiceRef;
                paymentCode = (await setupCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef)).PaymentCode;
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

            var vt8 = CreatePayload(8001, paymentCode, 500_000m, txDate: validPaidAt);

            // 2. Connection 2 starts payment processing BEFORE deadline
            var recordTask = Task.Run(async () =>
            {
                await using var payCtx = new PaymentServiceDbContext(options);
                var proc = CreatePaymentProcessor(new EfInvoiceRepository(payCtx), fakeTime);
                return await proc.ProcessAsync(vt8);
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
            Assert.Equal("SEPAY:8001", dbInvoice.PaymentRef);
            Assert.Equal(1, await verifyCtx.OutboxMessages.CountAsync(o => o.AggregateId == orderId.ToString("D")));

            // 5. Advance time past deadline and replay payment: must still succeed without adding outbox
            fakeTime.Advance(TimeSpan.FromMinutes(30)); // now = startTime + 45m > deadline
            var replayProc = CreatePaymentProcessor(new EfInvoiceRepository(verifyCtx), fakeTime);
            var replayRes = await replayProc.ProcessAsync(vt8);
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
