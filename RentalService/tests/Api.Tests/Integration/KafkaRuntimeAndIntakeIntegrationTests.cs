using System.Text.Json;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.IServices.Integration;
using Dprental.RentalService.Application.IServices.Payments;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Payments;
using Dprental.RentalService.Common;
using CommonError = Dprental.RentalService.Common.Error;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Moq;
using Confluent.Kafka;
using Dprental.RentalService.Application.Kafka.Workers;
using Dprental.RentalService.Application.Kafka.DependencyInjection;
using Microsoft.Extensions.Options;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Producers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Dprental.RentalService.Api.Tests.Domain;

namespace Dprental.RentalService.Api.Tests.Integration;

public sealed class KafkaRuntimeAndIntakeIntegrationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 8, 15, 30, TimeSpan.Zero);

    private static RentalServiceDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new RentalServiceDbContext(options);
    }

    private static (RentalOrder Order, RentalItem Item) CreatePaidOrderWithItem(
        Guid supplierId,
        DateTimeOffset? deadline = null)
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        var assignments = new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) };
        var item = new InitialRentalItemDto(
            itemId,
            Guid.NewGuid(),
            supplierId,
            1,
            new DateOnly(2026, 10, 15),
            new DateOnly(2026, 10, 18),
            "Camera Sony",
            null,
            "Pickup",
            200_000m,
            500_000m,
            "grp-01",
            assignments,
            TestListingSnapshots.Default);

        var order = RentalOrder.Create(
            orderId,
            $"ORD-KAF-{Guid.NewGuid():N}"[..12],
            renterId,
            "VND",
            "Pickup",
            "Customer",
            "0909123456",
            "Address",
            null,
            "terms-1",
            "doc-1",
            "hash-1",
            "op-create",
            FixedNow,
            [item]).Value;

        order.MarkAsPaid("INV-001", "PAY-001", FixedNow, FixedNow);

        string holdOpKey = RentalOperationExecutor.ComputeOperationKey("ExtendHold", $"{order.Id}:{order.Items.First().Id}");
        var holdReceipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            holdOpKey,
            "ExtendHold",
            "hash-hold",
            isSuccess: true,
            errorCode: null,
            errorMessage: null,
            createdAt: FixedNow,
            status: RentalOperationReceipt.StatusSucceeded,
            targetItemId: order.Items.First().Id);
        order.AddOperationReceipt(holdReceipt);

        if (deadline.HasValue)
        {
            var itemEntity = order.Items.First();
            typeof(RentalItem).GetProperty(nameof(RentalItem.SupplierConfirmationDeadline))?.SetValue(itemEntity, deadline.Value);
        }

        return (order, order.Items.First());
    }

    private static PaymentEventsDispatcher CreateDispatcher(
        RentalServiceDbContext db,
        IPaymentWorkflowService paymentWorkflow,
        IRefundCompletedConsumer refundConsumer)
    {
        var outboxRepo = new OutboxRepository(db);
        var options = Options.Create(new KafkaOptions
        {
            PaymentEventsTopic = "payment.events.v1",
            PaymentDlqTopic = "payment.events.v1.dlq"
        });
        return new PaymentEventsDispatcher(
            new InboxRepository(db),
            new RentalOrderRepository(db),
            new FakeOrderLockManager(),
            paymentWorkflow,
            refundConsumer,
            new KafkaOutboxWriter(outboxRepo),
            db,
            options,
            NullLogger<PaymentEventsDispatcher>.Instance);
    }
    private sealed class TrackingPaymentWorkflow : IPaymentWorkflowService
    {
        public bool ApplyCalled { get; set; }
        public Task<Result> ApplyInvoicePaidStagedAsync(RentalOrder order, KafkaMessageEnvelope<InvoicePaidPayload> envelope, RentalInboxMessage inboxMessage, CancellationToken cancellationToken = default)
        {
            ApplyCalled = true;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ProcessInvoicePaidAsync(KafkaMessageEnvelope<InvoicePaidPayload> envelope, CancellationToken cancellationToken = default)
        {
            ApplyCalled = true;
            return Task.FromResult(Result.Success());
        }
    }


    private sealed class TrackingRefundConsumer : IRefundCompletedConsumer
    {
        public bool ApplyCalled { get; set; }
        public Task<Result> ApplyRefundCompletedStagedAsync(RentalOrder order, KafkaMessageEnvelope<RefundCompletedPayload> envelope, RentalInboxMessage inboxMessage, CancellationToken cancellationToken = default)
        {
            ApplyCalled = true;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ProcessRefundCompletedAsync(KafkaMessageEnvelope<RefundCompletedPayload> envelope, CancellationToken cancellationToken = default)
        {
            ApplyCalled = true;
            return Task.FromResult(Result.Success());
        }
    }

    [Fact]
    public async Task KafkaIntake_ValidInvoicePaid_PersistsReceivedInbox_DoesNotRunWorkflowSynchronously()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var workflow = new TrackingPaymentWorkflow();
        var refundConsumer = new TrackingRefundConsumer();
        var dispatcher = CreateDispatcher(db, workflow, refundConsumer);

        Guid messageId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();
        string validJson = $$"""
        {
            "messageId": "{{messageId}}",
            "messageType": "InvoicePaid",
            "version": 1,
            "producer": "payment-service",
            "aggregateId": "{{orderId}}",
            "occurredAt": "2026-10-08T08:15:30Z",
            "correlationId": "corr-intake-1",
            "payload": {
                "rentalOrderId": "{{orderId}}",
                "renterId": "{{Guid.NewGuid()}}",
                "invoiceRef": "INV-INTAKE-1",
                "paymentRef": "PAY-INTAKE-1",
                "paidAmount": 2000000,
                "currency": "VND",
                "paidAt": "2026-10-08T08:15:30Z"
            }
        }
        """;

        var result = await dispatcher.ReceiveMessageAsync(validJson, "payment.events.v1", 0, 105, CancellationToken.None);

        Assert.True(result.IsSuccess);
        // Workflow MUST NOT run synchronously on intake!
        Assert.False(workflow.ApplyCalled);

        // Inbox MUST be persisted as Received with Kafka topic, partition, offset
        var inbox = await db.RentalInboxMessages.FirstOrDefaultAsync(m => m.MessageId == messageId);
        Assert.NotNull(inbox);
        Assert.Equal("Received", inbox.Status);
        Assert.Equal("payment.events.v1", inbox.KafkaTopic);
        Assert.Equal(0, inbox.KafkaPartition);
        Assert.Equal(105, inbox.KafkaOffset);
    }

    [Fact]
    public async Task KafkaIntake_SameMessageId_DifferentPayload_RoutesToPaymentDlqTopic()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var workflow = new TrackingPaymentWorkflow();
        var refundConsumer = new TrackingRefundConsumer();
        var dispatcher = CreateDispatcher(db, workflow, refundConsumer);

        Guid messageId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();
        string firstJson = $$"""
        {
            "messageId": "{{messageId}}",
            "messageType": "InvoicePaid",
            "version": 1,
            "producer": "payment-service",
            "aggregateId": "{{orderId}}",
            "occurredAt": "2026-10-08T08:15:30Z",
            "payload": {
                "rentalOrderId": "{{orderId}}",
                "renterId": "{{Guid.NewGuid()}}",
                "invoiceRef": "INV-1",
                "paymentRef": "PAY-1",
                "paidAmount": 1000000,
                "currency": "VND",
                "paidAt": "2026-10-08T08:15:30Z"
            }
        }
        """;

        var firstResult = await dispatcher.ReceiveMessageAsync(firstJson, "payment.events.v1", 0, 1, CancellationToken.None);
        Assert.True(firstResult.IsSuccess);

        // Second message has SAME messageId but DIFFERENT paid amount -> conflict!
        string conflictingJson = $$"""
        {
            "messageId": "{{messageId}}",
            "messageType": "InvoicePaid",
            "version": 1,
            "producer": "payment-service",
            "aggregateId": "{{orderId}}",
            "occurredAt": "2026-10-08T08:15:30Z",
            "payload": {
                "rentalOrderId": "{{orderId}}",
                "renterId": "{{Guid.NewGuid()}}",
                "invoiceRef": "INV-1",
                "paymentRef": "PAY-1",
                "paidAmount": 9999999,
                "currency": "VND",
                "paidAt": "2026-10-08T08:15:30Z"
            }
        }
        """;

        var secondResult = await dispatcher.ReceiveMessageAsync(conflictingJson, "payment.events.v1", 0, 2, CancellationToken.None);
        Assert.True(secondResult.IsSuccess);
        Assert.Equal(KafkaIntakeDisposition.DlqStored, secondResult.Value);

        // Must be routed to payment.events.v1.dlq
        var dlqMessage = await db.RentalOutboxMessages.FirstOrDefaultAsync(m => m.MessageType == "DeadLetter");
        Assert.NotNull(dlqMessage);
        Assert.Equal("payment.events.v1.dlq", dlqMessage.Topic);
    }

    [Fact]
    public async Task KafkaIntake_MalformedMessage_UsesDeterministicDlqId_DoesNotDuplicateOnRedelivery()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var workflow = new TrackingPaymentWorkflow();
        var refundConsumer = new TrackingRefundConsumer();
        var dispatcher = CreateDispatcher(db, workflow, refundConsumer);

        string malformed = "this is malformed non-json record";

        var res1 = await dispatcher.ReceiveMessageAsync(malformed, "payment.events.v1", 1, 42, CancellationToken.None);
        Assert.True(res1.IsSuccess);
        Assert.Equal(KafkaIntakeDisposition.DlqStored, res1.Value);

        int dlqCountAfterFirst = await db.RentalOutboxMessages.CountAsync(m => m.MessageType == "DeadLetter");
        Assert.Equal(1, dlqCountAfterFirst);

        // Redelivery of SAME record with SAME topic, partition, offset -> should not create duplicate DLQ outbox message
        var res2 = await dispatcher.ReceiveMessageAsync(malformed, "payment.events.v1", 1, 42, CancellationToken.None);
        Assert.True(res2.IsSuccess);
        Assert.Equal(KafkaIntakeDisposition.DlqStored, res2.Value);
        int dlqCountAfterSecond = await db.RentalOutboxMessages.CountAsync(m => m.MessageType == "DeadLetter");
        Assert.Equal(1, dlqCountAfterSecond);
    }

    [Fact]
    public async Task SupplierAccept_RetryScheduled_Returns503_DoesNotCallProviderBeforeSchedule()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid supplierId = Guid.NewGuid();
        var (order, item) = CreatePaidOrderWithItem(supplierId);

        string key = "idemp-retry-scheduled-key";
        string structuredFingerprint = RentalOperationExecutor.ComputeFingerprint(new { Action = "SupplierAccept", ItemId = item.Id, SupplierId = supplierId });

        // Existing receipt is Processing, with NextRetryAt 30 minutes in the future
        var receipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            key,
            "SupplierAccept",
            structuredFingerprint,
            isSuccess: false,
            errorCode: "TIMEOUT",
            errorMessage: "Timed out previously",
            createdAt: FixedNow,
            status: RentalOperationReceipt.StatusProcessing,
            retryCount: 1,
            nextRetryAt: FixedNow.AddMinutes(30),
            targetItemId: item.Id);
        order.AddOperationReceipt(receipt);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        bool providerCalled = false;
        var inventoryClient = new StubInventoryClient
        {
            ConfirmHandler = (_, _) =>
            {
                providerCalled = true;
                return Task.FromResult(Result.Success(new ConfirmAllocationResponse("REC", "grp", FixedNow, false, [])));
            }
        };

        var time = new TestTimeProvider(FixedNow);
        var executor = new RentalOperationExecutor(
            new RentalOrderRepository(db),
            new OperationReceiptRepository(db),
            new OutboxRepository(db),
            new FakeOrderLockManager(),
            inventoryClient,
            new StubPaymentClient(),
            db,
            new ConfigurationBuilder().Build(),
            NullLogger<RentalOperationExecutor>.Instance,
            time);

        var result = await executor.ExecuteSupplierAcceptAsync(order.Id, item.Id, supplierId, key);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.ServiceUnavailable, result.Error.Type);
        Assert.Equal("RETRY_SCHEDULED", result.Error.Code);
        Assert.False(providerCalled, "Provider MUST NOT be called before scheduled retry time!");
    }

    [Fact]
    public async Task SupplierAccept_UncertainExhausted_ReturnsConflict_RequiresAdminReplay()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid supplierId = Guid.NewGuid();
        var (order, item) = CreatePaidOrderWithItem(supplierId);

        string key = "idemp-uncertain-exhausted-key";
        string structuredFingerprint = RentalOperationExecutor.ComputeFingerprint(new { Action = "SupplierAccept", ItemId = item.Id, SupplierId = supplierId });

        var receipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            key,
            "SupplierAccept",
            structuredFingerprint,
            isSuccess: false,
            errorCode: "UNCERTAIN_EXHAUSTED",
            errorMessage: "Exhausted retries with uncertain delivery",
            createdAt: FixedNow,
            status: RentalOperationReceipt.StatusFailed,
            retryCount: 10,
            targetItemId: item.Id,
            errorClassification: RentalOperationReceipt.ClassificationUncertainExhausted,
            isOutcomeUncertain: true);
        order.AddOperationReceipt(receipt);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var executor = new RentalOperationExecutor(
            new RentalOrderRepository(db),
            new OperationReceiptRepository(db),
            new OutboxRepository(db),
            new FakeOrderLockManager(),
            new StubInventoryClient(),
            new StubPaymentClient(),
            db,
            new ConfigurationBuilder().Build(),
            NullLogger<RentalOperationExecutor>.Instance,
            new TestTimeProvider(FixedNow));

        var result = await executor.ExecuteSupplierAcceptAsync(order.Id, item.Id, supplierId, key);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Equal("UNCERTAIN_EXHAUSTED", result.Error.Code);
    }

    [Fact]
    public async Task Consumer_IntakeFailureAtOffsetN_DoesNotCommit_AndSeeksBackToN()
    {
        string topic = "payment.events.v1";
        var tp = new TopicPartition(topic, 0);
        var tpo = new TopicPartitionOffset(tp, new Offset(10));

        var mockConsumer = new Mock<IConsumer<string, string>>();
        mockConsumer.Setup(c => c.Assignment).Returns(new List<TopicPartition> { tp });

        var consumeResult = new ConsumeResult<string, string>
        {
            Topic = topic,
            Partition = 0,
            Offset = 10,
            Message = new Message<string, string>
            {
                Key = "order-1",
                Value = "{}"
            }
        };

        mockConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns(consumeResult);
        mockConsumer.Setup(c => c.Seek(It.IsAny<TopicPartitionOffset>()));
        mockConsumer.Setup(c => c.Commit(It.IsAny<ConsumeResult<string, string>>()));

        // Dispatcher that returns a failure (e.g. database error)
        var mockDispatcher = new Mock<IPaymentEventsDispatcher>();
        mockDispatcher.Setup(d => d.ReceiveMessageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<KafkaIntakeDisposition>(CommonError.Failure("DB_ERROR", "Simulated DB failure")));

        var services = new ServiceCollection();
        services.AddScoped<IPaymentEventsDispatcher>(_ => mockDispatcher.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var options = Options.Create(new KafkaOptions
        {
            Enabled = true,
            BootstrapServers = "127.0.0.1:9092",
            PaymentEventsTopic = topic
        });

        using var worker = new KafkaPaymentEventsConsumerWorker(
            scopeFactory,
            mockConsumer.Object,
            options,
            NullLogger<KafkaPaymentEventsConsumerWorker>.Instance);

        var result = await worker.ConsumeNextMessageAsync(CancellationToken.None);

        Assert.True(result.IsFailure);
        // Verified: Seek was called with Offset 10!
        mockConsumer.Verify(c => c.Seek(It.Is<TopicPartitionOffset>(x => x.Offset.Value == 10)), Times.Once);
        // Verified: Commit was NEVER called! Offset N is NOT committed, preventing N+1 from advancing past N!
        mockConsumer.Verify(c => c.Commit(It.IsAny<ConsumeResult<string, string>>()), Times.Never);
    }

    [Fact]
    public async Task Consumer_MalformedJsonTypes_AllRouteToDlq_DoNotBreakLoop()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var dispatcher = CreateDispatcher(db, new TrackingPaymentWorkflow(), new TrackingRefundConsumer());

        string[] invalidPayloads = new[]
        {
            "[1, 2, 3]", // Root is array instead of object
            """{"messageType": 123, "messageId": "00000000-0000-0000-0000-000000000001"}""", // messageType is number
            """{"messageType": "InvoicePaid", "messageId": true}""", // messageId is boolean
            """{"messageType": "InvoicePaid", "messageId": "00000000-0000-0000-0000-000000000001", "version": "v1"}""", // version is string
            """{"messageType": "InvoicePaid", "messageId": "00000000-0000-0000-0000-000000000001", "version": 1, "producer": 99}""", // producer is number
            """{"messageType": "InvoicePaid", "messageId": "00000000-0000-0000-0000-000000000001", "version": 1, "producer": "payment-service", "aggregateId": "not-a-guid"}""", // aggregateId invalid
            """{"messageType": "InvoicePaid", "messageId": "00000000-0000-0000-0000-000000000001", "version": 1, "producer": "payment-service", "aggregateId": "00000000-0000-0000-0000-000000000002", "payload": "just a string"}""" // payload is string
        };

        for (int i = 0; i < invalidPayloads.Length; i++)
        {
            var res = await dispatcher.ReceiveMessageAsync(invalidPayloads[i], "payment.events.v1", 0, 500 + i);
            Assert.True(res.IsSuccess);
            Assert.Equal(KafkaIntakeDisposition.DlqStored, res.Value);
        }

        int dlqCount = await db.RentalOutboxMessages.CountAsync(m => m.MessageType == "DeadLetter");
        Assert.Equal(invalidPayloads.Length, dlqCount);
    }

    [Fact]
    public async Task Consumer_Cancellation_DoesNotRouteToDlqOrCommit()
    {
        string topic = "payment.events.v1";
        var tp = new TopicPartition(topic, 0);

        var mockConsumer = new Mock<IConsumer<string, string>>();
        mockConsumer.Setup(c => c.Assignment).Returns(new List<TopicPartition> { tp });
        mockConsumer.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns(new ConsumeResult<string, string>
        {
            Topic = topic,
            Partition = 0,
            Offset = 20,
            Message = new Message<string, string> { Key = "k", Value = "{}" }
        });

        var mockDispatcher = new Mock<IPaymentEventsDispatcher>();
        var services = new ServiceCollection();
        services.AddScoped<IPaymentEventsDispatcher>(_ => mockDispatcher.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var options = Options.Create(new KafkaOptions { Enabled = true, BootstrapServers = "127.0.0.1:9092", PaymentEventsTopic = topic });
        using var worker = new KafkaPaymentEventsConsumerWorker(scopeFactory, mockConsumer.Object, options, NullLogger<KafkaPaymentEventsConsumerWorker>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Already cancelled

        var result = await worker.ConsumeNextMessageAsync(cts.Token);

        Assert.True(result.IsFailure);
        mockConsumer.Verify(c => c.Commit(It.IsAny<ConsumeResult<string, string>>()), Times.Never);
        mockDispatcher.Verify(d => d.ReceiveMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task KafkaWorkers_WhenDisabled_DoNotProcessBacklogOrClaimOrTouchOutbox()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);

        // Seed pending outbox message
        Guid outboxId = Guid.NewGuid();
        var outboxMsg = new RentalOutboxMessage(
            outboxId,
            "rental.events.v1",
            "key-1",
            "TestEvent",
            "{}",
            DateTimeOffset.UtcNow);
        db.RentalOutboxMessages.Add(outboxMsg);

        // Seed pending inbox message
        Guid inboxId = Guid.NewGuid();
        var inboxMsg = new RentalInboxMessage(
            inboxId,
            Guid.NewGuid(),
            "InvoicePaid",
            "payment-service",
            Guid.NewGuid().ToString("D"),
            null,
            null,
            "{}",
            DateTimeOffset.UtcNow);
        db.RentalInboxMessages.Add(inboxMsg);

        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<RentalServiceDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddScoped<IInboxRepository, InboxRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<RentalServiceDbContext>());
        services.AddScoped<IRentalOrderRepository, RentalOrderRepository>();
        services.AddScoped<IOrderLockManager, FakeOrderLockManager>();
        services.AddSingleton<IPaymentWorkflowService>(new TrackingPaymentWorkflow());
        services.AddSingleton<IRefundCompletedConsumer>(new TrackingRefundConsumer());

        // AddKafka with Enabled = false (disabled)
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Kafka:Enabled", "false")
            })
            .Build();
        services.AddKafka(config);

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var disabledOptions = Options.Create(new KafkaOptions { Enabled = false });

        var publisherWorker = new OutboxPublisherWorker(scopeFactory, disabledOptions, NullLogger<OutboxPublisherWorker>.Instance);
        var inboxWorker = new InboxProcessingWorker(scopeFactory, disabledOptions, NullLogger<InboxProcessingWorker>.Instance);
        var consumerWorker = new KafkaPaymentEventsConsumerWorker(scopeFactory, disabledOptions, NullLogger<KafkaPaymentEventsConsumerWorker>.Instance);

        int published = await publisherWorker.ProcessBatchAsync(CancellationToken.None);
        int processed = await inboxWorker.ProcessBatchAsync(CancellationToken.None);
        var consumeResult = await consumerWorker.ConsumeNextMessageAsync(CancellationToken.None);

        Assert.Equal(0, published);
        Assert.Equal(0, processed);
        Assert.True(consumeResult.IsSuccess);

        // Verify Outbox message was NOT claimed, status was NOT changed, retry count NOT incremented
        await using var verifyDb = CreateDbContext(dbName);
        var verifiedOutbox = await verifyDb.RentalOutboxMessages.FirstAsync(m => m.Id == outboxId);
        Assert.Equal("Pending", verifiedOutbox.Status);
        Assert.Null(verifiedOutbox.LockedBy);
        Assert.Equal(0, verifiedOutbox.RetryCount);

        // Verify Inbox message was NOT processed, status was NOT changed, retry count NOT incremented
        var verifiedInbox = await verifyDb.RentalInboxMessages.FirstAsync(m => m.Id == inboxId);
        Assert.Equal(RentalInboxMessage.StatusReceived, verifiedInbox.Status);
        Assert.Equal(0, verifiedInbox.RetryCount);
    }

    private sealed class StubInventoryClient : IInventoryRentalClient
    {
        public Func<string, ConfirmAllocationRequest, Task<Result<ConfirmAllocationResponse>>>? ConfirmHandler { get; set; }
        public Task<Result<ConfirmAllocationResponse>> ConfirmAllocationAsync(string allocationGroupId, ConfirmAllocationRequest request, CancellationToken ct = default) =>
            ConfirmHandler != null ? ConfirmHandler(allocationGroupId, request) : Task.FromResult(Result.Success(new ConfirmAllocationResponse("REC", allocationGroupId, FixedNow, false, [])));
        public Task<Result<ExtendHoldResponse>> ExtendHoldAsync(string allocationGroupId, ExtendHoldRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ReleaseAllocationResponse>> ReleaseAllocationAsync(string allocationGroupId, ReleaseAllocationRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ListingRentalSnapshotsResponse>> GetListingRentalSnapshotsAsync(ListingRentalSnapshotsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<ListingAvailabilityBatchResponse>> CheckListingAvailabilityBatchAsync(ListingAvailabilityBatchRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<ListingRentalSnapshotDto>> GetListingRentalSnapshotAsync(Guid listingId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(ListingAvailabilityCheckRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CreateHoldResponse>> CreateHoldAsync(CreateHoldRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CancelAllocationResponse>> CancelAllocationAsync(string allocationGroupId, CancelAllocationRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<MarkAssetsOverdueResponse>> MarkAssetsOverdueAsync(string allocationGroupId, MarkAssetsOverdueRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<MarkAssetsReturnedResponse>> MarkAssetsReturnedAsync(string allocationGroupId, MarkAssetsReturnedRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<BlockDamagedAssetsResponse>> BlockDamagedAssetsAsync(string allocationGroupId, BlockDamagedAssetsRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubPaymentClient : IPaymentRentalClient
    {
        public Task<Result<RequestRefundResponse>> RequestRefundAsync(RequestRefundRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(CreateInvoiceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CancelInvoiceResponse>> CancelInvoiceAsync(CancelInvoiceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<SettleRentalResponse>> SettleRentalAsync(SettleRentalRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;
        public TestTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
