using System.Text.Json;
using Dprental.NotificationService.Api.Tests.Fixtures;
using Dprental.NotificationService.Common;
using Dprental.NotificationService.Application.IServices;
using Dprental.NotificationService.Application.Kafka.Contracts;
using Dprental.NotificationService.Application.Kafka.Handlers;
using Dprental.NotificationService.Application.Kafka.Quarantine;
using Dprental.NotificationService.Application.Kafka.Services;
using Dprental.NotificationService.Application.Kafka.Validation;
using Dprental.NotificationService.Application.Recipients;
using Dprental.NotificationService.Application.Templates;
using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Domain.IRepositories;
using Dprental.NotificationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Kafka;

public sealed class NotificationIngestionTests
{
    private static NotificationServiceDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new NotificationServiceDbContext(options);
    }

    private static (NotificationIngestionService Service, NotificationServiceDbContext Db, Mock<IKafkaDeadLetterPublisher> Dlq, Mock<INotificationRealtimeNotifier> Realtime) CreateTestIngestionService()
    {
        var db = CreateInMemoryDbContext();
        var uow = new UnitOfWork(db);
        var inboxRepo = new InboxReceiptRepository(db);
        var notifRepo = new NotificationRepository(db);
        var deliveryRepo = new DeliveryJobRepository(db);
        var prefRepo = new PreferenceRepository(db);
        var watermarkRepo = new AggregateWatermarkRepository(db);

        var recipientResolver = new RecipientResolutionService();
        var templateRenderer = new NotificationTemplateRenderer();

        var handlers = new IKafkaEventHandler[]
        {
            new RentalOrderCreatedHandler(),
            new RentalOrderPaidHandler(),
            new RentalSupplierConfirmationRequestedHandler(),
            new RentalOrderExpiredHandler(),
            new RentalOrderCancelledHandler(),
            new RentalItemHandoverCompletedHandler(),
            new RentalHandoverOverdueHandler(),
            new ReturnConfirmationOverdueHandler(),
            new RentalOrderCompletedHandler(),
            new LatePaymentRefundRequestedHandler()
        };

        var dlqMock = new Mock<IKafkaDeadLetterPublisher>();
        var realtimeMock = new Mock<INotificationRealtimeNotifier>();

        var service = new NotificationIngestionService(
            handlers,
            recipientResolver,
            templateRenderer,
            inboxRepo,
            notifRepo,
            deliveryRepo,
            prefRepo,
            watermarkRepo,
            uow,
            dlqMock.Object,
            realtimeMock.Object,
            NullLogger<NotificationIngestionService>.Instance);

        return (service, db, dlqMock, realtimeMock);
    }

    [Fact]
    public async Task Ingestion_ValidRentalOrderPaid_ShouldAtomicallyPersistAndNotify()
    {
        var (service, db, dlq, realtime) = CreateTestIngestionService();

        var orderId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        var payload = new
        {
            orderId = orderId,
            orderCode = "RNT-20261102-0001",
            orderVersion = 2,
            renterId = renterId,
            paidAmount = 1500000m,
            invoiceRef = "INV-001",
            paidAt = DateTimeOffset.UtcNow
        };

        var envelope = new KafkaMessageEnvelope
        {
            MessageId = eventId,
            MessageType = "RentalOrderPaid",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload)
        };

        var rawMessage = JsonSerializer.Serialize(envelope);

        var outcome = await service.IngestMessageAsync(rawMessage, "rental.events.v1", 0, 100);
        Assert.Equal(IngestionOutcome.Processed, outcome);

        // Verify receipt
        var receipt = await db.InboxReceipts.FirstOrDefaultAsync(r => r.MessageId == eventId);
        Assert.NotNull(receipt);
        Assert.Equal("Processed", receipt.Status);

        // Verify notification
        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.SourceEventId == eventId);
        Assert.NotNull(notification);
        Assert.Equal(renterId, notification.RecipientUserId);
        Assert.Equal("ORDER_PAID", notification.NotificationType);
        Assert.Contains("1.500.000", notification.Body);

        // Verify email delivery job created (ORDER_PAID enables email)
        var deliveryJob = await db.DeliveryJobs.FirstOrDefaultAsync(j => j.NotificationId == notification.Id);
        Assert.NotNull(deliveryJob);
        Assert.Equal("Pending", deliveryJob.Status);
        Assert.Equal("EMAIL", deliveryJob.Channel);

        // Verify watermark updated to terminal PAID
        var watermark = await db.AggregateWatermarks.FirstOrDefaultAsync(w => w.AggregateId == orderId && w.RecipientUserId == renterId);
        Assert.NotNull(watermark);
        Assert.Equal(2, watermark.LastOrderVersion);
        Assert.False(watermark.IsTerminal);
        Assert.Null(watermark.TerminalState);

        // Verify realtime invalidation was triggered
        realtime.Verify(r => r.NotifyInboxChangedAsync(
            renterId,
            notification.Id,
            "CREATED",
            "PAYMENT",
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ingestion_DuplicateMessage_ShouldBeSkippedIdempotently()
    {
        var (service, db, dlq, realtime) = CreateTestIngestionService();

        var eventId = Guid.NewGuid();
        var payload = new
        {
            orderId = Guid.NewGuid(),
            orderCode = "RNT-001",
            orderVersion = 1,
            renterId = Guid.NewGuid(),
            totalPayableAmount = 1000000m,
            paymentDeadline = "2026-11-02T10:00:00Z"
        };

        var envelope = new KafkaMessageEnvelope
        {
            MessageId = eventId,
            MessageType = "RentalOrderCreated",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload)
        };
        var rawMessage = JsonSerializer.Serialize(envelope);

        var first = await service.IngestMessageAsync(rawMessage, "rental.events.v1", 0, 101);
        Assert.Equal(IngestionOutcome.Processed, first);

        var second = await service.IngestMessageAsync(rawMessage, "rental.events.v1", 0, 102);
        Assert.Equal(IngestionOutcome.Duplicate, second);

        // Assert notification count is exactly 1
        var count = await db.Notifications.CountAsync(n => n.SourceEventId == eventId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Ingestion_UnknownEventFromRentalService_ShouldBeSkippedWithoutQuarantine()
    {
        var (service, db, dlq, realtime) = CreateTestIngestionService();

        var envelope = new KafkaMessageEnvelope
        {
            MessageId = Guid.NewGuid(),
            MessageType = "RentalDisputeOpened",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { someField = "value" })
        };
        var rawMessage = JsonSerializer.Serialize(envelope);

        var outcome = await service.IngestMessageAsync(rawMessage, "rental.events.v1", 0, 103);
        Assert.Equal(IngestionOutcome.Ignored, outcome);

        dlq.Verify(d => d.PublishToDeadLetterQueueAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ingestion_MalformedJson_ShouldPublishToDlqWithTopicPartitionOffsetKey()
    {
        var (service, db, dlq, realtime) = CreateTestIngestionService();

        const string malformed = "{ this is not json }";
        var outcome = await service.IngestMessageAsync(malformed, "rental.events.v1", 2, 555);
        Assert.Equal(IngestionOutcome.Quarantined, outcome);

        dlq.Verify(d => d.PublishToDeadLetterQueueAsync(
            malformed,
            "rental.events.v1:2:555",
            It.IsAny<string>(),
            "INVALID_JSON",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ingestion_SameVersionSibling_ProcessedIndependently()
    {
        var (service, db, dlq, realtime) = CreateTestIngestionService();

        var orderId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        // Sibling 1
        var event1Id = Guid.NewGuid();
        var payload1 = new
        {
            orderId = orderId,
            orderCode = "RNT-SIBLING",
            orderVersion = 3,
            renterId = renterId,
            actualHandoverAt = DateTimeOffset.UtcNow.ToString("O")
        };
        var env1 = new KafkaMessageEnvelope
        {
            MessageId = event1Id,
            MessageType = "RentalItemHandoverCompleted",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload1)
        };

        // Sibling 2 sharing orderVersion = 3
        var event2Id = Guid.NewGuid();
        var payload2 = new
        {
            orderId = orderId,
            orderCode = "RNT-SIBLING",
            orderVersion = 3,
            supplierId = supplierId,
            renterId = renterId,
            expectedStartDate = DateTimeOffset.UtcNow.ToString("O")
        };
        var env2 = new KafkaMessageEnvelope
        {
            MessageId = event2Id,
            MessageType = "RentalHandoverOverdue",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload2)
        };

        var outcome1 = await service.IngestMessageAsync(JsonSerializer.Serialize(env1), "rental.events.v1", 0, 201);
        var outcome2 = await service.IngestMessageAsync(JsonSerializer.Serialize(env2), "rental.events.v1", 0, 202);

        Assert.Equal(IngestionOutcome.Processed, outcome1);
        Assert.Equal(IngestionOutcome.Processed, outcome2);

        Assert.True(await db.InboxReceipts.AnyAsync(r => r.MessageId == event1Id));
        Assert.True(await db.InboxReceipts.AnyAsync(r => r.MessageId == event2Id));
    }

    [Fact]
    public void KafkaMessageEnvelopeAndAuthContact_Contract_SerializesAndDeserializes()
    {
        var webOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string json = """
        {
            "messageId": "550e8400-e29b-41d4-a716-446655440000",
            "messageType": "RentalOrderCreated",
            "producer": "RentalService",
            "version": 1,
            "occurredAt": "2026-11-02T10:00:00Z",
            "payload": { "id": 1 }
        }
        """;
        var env = JsonSerializer.Deserialize<KafkaMessageEnvelope>(json, webOptions);
        Assert.NotNull(env);
        Assert.Equal(Guid.Parse("550e8400-e29b-41d4-a716-446655440000"), env.MessageId);
        Assert.Equal("RentalOrderCreated", env.MessageType);
        Assert.Equal("RentalService", env.Producer);
        Assert.Equal(1, env.Version);

        string contactJson = """
        {
            "contacts": [
                { "userId": "00000000-0000-0000-0000-000000000001", "email": "test@example.com", "isActive": true, "isEmailVerified": true }
            ]
        }
        """;
        var contactBatch = JsonSerializer.Deserialize<Dprental.NotificationService.Application.Clients.AuthContactBatchResponse>(contactJson, webOptions);
        Assert.NotNull(contactBatch);
        Assert.Single(contactBatch.Contacts);
        Assert.Equal("test@example.com", contactBatch.Contacts[0].Email);
        Assert.True(contactBatch.Contacts[0].IsActive);
        Assert.True(contactBatch.Contacts[0].IsEmailVerified);
    }

    [Fact]
    public async Task Ingestion_All10MvpEvents_FromProducerFixtures_Succeeds()
    {
        var (service, db, dlq, realtime) = CreateTestIngestionService();

        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var returnRequestId = Guid.NewGuid();

        var fixtures = new[]
        {
            ProducerFixtures.RentalOrderCreated(Guid.NewGuid(), orderId, renterId),
            ProducerFixtures.RentalOrderPaid(Guid.NewGuid(), orderId, renterId),
            ProducerFixtures.RentalSupplierConfirmationRequested(Guid.NewGuid(), orderId, itemId, supplierId),
            ProducerFixtures.RentalItemHandoverCompleted(Guid.NewGuid(), orderId, itemId, renterId, supplierId),
            ProducerFixtures.RentalHandoverOverdue(Guid.NewGuid(), orderId, itemId, renterId, supplierId),
            ProducerFixtures.ReturnConfirmationOverdue(Guid.NewGuid(), orderId, itemId, returnRequestId, renterId, supplierId),
            ProducerFixtures.LatePaymentRefundRequested(Guid.NewGuid(), orderId, renterId),
            ProducerFixtures.RentalOrderCompleted(Guid.NewGuid(), orderId, renterId)
        };

        for (var i = 0; i < fixtures.Length; i++)
        {
            var outcome = await service.IngestMessageAsync(fixtures[i], "rental.events.v1", 0, i + 1);
            Assert.Equal(IngestionOutcome.Processed, outcome);
        }

        var receiptCount = await db.InboxReceipts.CountAsync();
        Assert.Equal(fixtures.Length, receiptCount);
    }

    [Fact]
    public async Task Ingestion_TemplateRenderFailure_QuarantinesAndDoesNotCommitReceipt()
    {
        var db = CreateInMemoryDbContext();
        var uow = new UnitOfWork(db);
        var inboxRepo = new InboxReceiptRepository(db);
        var notifRepo = new NotificationRepository(db);
        var deliveryRepo = new DeliveryJobRepository(db);
        var prefRepo = new PreferenceRepository(db);
        var watermarkRepo = new AggregateWatermarkRepository(db);
        var recipientResolver = new RecipientResolutionService();

        // Mock template renderer that fails
        var templateRendererMock = new Mock<INotificationTemplateRenderer>();
        templateRendererMock.Setup(t => t.Render(It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), It.IsAny<bool>()))
            .Returns(Result.Failure<RenderedNotification>(Error.Failure("RENDER_FAILED", "Render error")));

        var dlqMock = new Mock<IKafkaDeadLetterPublisher>();
        var realtimeMock = new Mock<INotificationRealtimeNotifier>();

        var service = new NotificationIngestionService(
            new IKafkaEventHandler[] { new RentalOrderCreatedHandler() },
            recipientResolver,
            templateRendererMock.Object,
            inboxRepo,
            notifRepo,
            deliveryRepo,
            prefRepo,
            watermarkRepo,
            uow,
            dlqMock.Object,
            realtimeMock.Object,
            NullLogger<NotificationIngestionService>.Instance);

        var messageId = Guid.NewGuid();
        var raw = ProducerFixtures.RentalOrderCreated(messageId, Guid.NewGuid(), Guid.NewGuid());

        var outcome = await service.IngestMessageAsync(raw, "rental.events.v1", 0, 10);

        Assert.Equal(IngestionOutcome.Quarantined, outcome);
        // Must publish to DLQ
        dlqMock.Verify(d => d.PublishToDeadLetterQueueAsync(
            raw,
            messageId.ToString(),
            "Render error",
            "RENDER_FAILED",
            It.IsAny<CancellationToken>()), Times.Once);

        // Must NOT commit receipt Processed
        var receipt = await db.InboxReceipts.FirstOrDefaultAsync(r => r.MessageId == messageId);
        Assert.Null(receipt);

        // Must NOT save any notification
        var count = await db.Notifications.CountAsync(n => n.SourceEventId == messageId);
        Assert.Equal(0, count);
    }
}
