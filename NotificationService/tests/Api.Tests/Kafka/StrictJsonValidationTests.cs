using System.Text.Json;
using Dprental.NotificationService.Application.IServices;
using Dprental.NotificationService.Application.Kafka.Contracts;
using Dprental.NotificationService.Application.Kafka.Handlers;
using Dprental.NotificationService.Application.Kafka.Quarantine;
using Dprental.NotificationService.Application.Kafka.Services;
using Dprental.NotificationService.Application.Recipients;
using Dprental.NotificationService.Application.Templates;
using Dprental.NotificationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Kafka;

public sealed class StrictJsonValidationTests
{
    private static NotificationServiceDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new NotificationServiceDbContext(options);
    }

    private static (NotificationIngestionService Service, Mock<IKafkaDeadLetterPublisher> Dlq) CreateTestService(NotificationServiceDbContext db)
    {
        var uow = new UnitOfWork(db);
        var inboxRepo = new InboxReceiptRepository(db);
        var notifRepo = new NotificationRepository(db);
        var deliveryJobRepo = new DeliveryJobRepository(db);
        var prefRepo = new PreferenceRepository(db);
        var watermarkRepo = new AggregateWatermarkRepository(db);
        var recipientResolver = new RecipientResolutionService();
        var templateRenderer = new NotificationTemplateRenderer();
        var dlqMock = new Mock<IKafkaDeadLetterPublisher>();
        var realtimeMock = new Mock<INotificationRealtimeNotifier>();

        var handlers = new IKafkaEventHandler[]
        {
            new RentalOrderCreatedHandler(),
            new RentalOrderPaidHandler(),
            new RefundCompletedFinancialHandler()
        };

        var service = new NotificationIngestionService(
            handlers,
            recipientResolver,
            templateRenderer,
            inboxRepo,
            notifRepo,
            deliveryJobRepo,
            prefRepo,
            watermarkRepo,
            uow,
            dlqMock.Object,
            realtimeMock.Object,
            NullLogger<NotificationIngestionService>.Instance);

        return (service, dlqMock);
    }

    [Fact]
    public void RentalOrderCreatedHandler_StringForNumberAmount_Rejected()
    {
        var handler = new RentalOrderCreatedHandler();
        using var doc = JsonDocument.Parse("""
        {
            "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
            "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
            "totalPayableAmount": "1500000",
            "orderVersion": 1,
            "paymentDeadline": "2026-11-04T08:00:00Z"
        }
        """);

        var envelope = new KafkaMessageEnvelope
        {
            MessageId = Guid.NewGuid(),
            MessageType = "RentalOrderCreated",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = doc.RootElement
        };

        var result = handler.ParseIntent(envelope);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_NUMBER_KIND", result.Error.Code);
    }

    [Fact]
    public void RentalOrderCreatedHandler_StringForNumberVersion_Rejected()
    {
        var handler = new RentalOrderCreatedHandler();
        using var doc = JsonDocument.Parse("""
        {
            "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
            "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
            "totalPayableAmount": 1500000,
            "orderVersion": "1",
            "paymentDeadline": "2026-11-04T08:00:00Z"
        }
        """);

        var envelope = new KafkaMessageEnvelope
        {
            MessageId = Guid.NewGuid(),
            MessageType = "RentalOrderCreated",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = doc.RootElement
        };

        var result = handler.ParseIntent(envelope);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_NUMBER_KIND", result.Error.Code);
    }

    [Fact]
    public void RentalOrderCreatedHandler_NumberForStringGuid_Rejected()
    {
        var handler = new RentalOrderCreatedHandler();
        using var doc = JsonDocument.Parse("""
        {
            "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
            "renterId": 12345678,
            "totalPayableAmount": 1500000,
            "orderVersion": 1,
            "paymentDeadline": "2026-11-04T08:00:00Z"
        }
        """);

        var envelope = new KafkaMessageEnvelope
        {
            MessageId = Guid.NewGuid(),
            MessageType = "RentalOrderCreated",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = doc.RootElement
        };

        var result = handler.ParseIntent(envelope);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_GUID_KIND", result.Error.Code);
    }

    [Fact]
    public void RentalOrderCreatedHandler_EmptyGuid_Rejected()
    {
        var handler = new RentalOrderCreatedHandler();
        using var doc = JsonDocument.Parse("""
        {
            "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
            "renterId": "00000000-0000-0000-0000-000000000000",
            "totalPayableAmount": 1500000,
            "orderVersion": 1,
            "paymentDeadline": "2026-11-04T08:00:00Z"
        }
        """);

        var envelope = new KafkaMessageEnvelope
        {
            MessageId = Guid.NewGuid(),
            MessageType = "RentalOrderCreated",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = doc.RootElement
        };

        var result = handler.ParseIntent(envelope);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_GUID_VALUE", result.Error.Code);
    }

    [Theory]
    [InlineData("2026-11-04T08:00:00+07:00")]
    [InlineData("2026-11-04T08:00:00-05:00")]
    [InlineData("not-a-timestamp")]
    public void RentalOrderCreatedHandler_NonUtcTimestamp_Rejected(string invalidTimestamp)
    {
        var handler = new RentalOrderCreatedHandler();
        using var doc = JsonDocument.Parse($$"""
        {
            "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
            "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
            "totalPayableAmount": 1500000,
            "orderVersion": 1,
            "paymentDeadline": "{{invalidTimestamp}}"
        }
        """);

        var envelope = new KafkaMessageEnvelope
        {
            MessageId = Guid.NewGuid(),
            MessageType = "RentalOrderCreated",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = doc.RootElement
        };

        var result = handler.ParseIntent(envelope);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_UTC_TIMESTAMP", result.Error.Code);
    }

    [Fact]
    public void RentalOrderCreatedHandler_ValidUtcTimestampAndFields_Succeeds()
    {
        var handler = new RentalOrderCreatedHandler();
        using var doc = JsonDocument.Parse("""
        {
            "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
            "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
            "totalPayableAmount": 1500000,
            "orderVersion": 1,
            "paymentDeadline": "2026-11-04T08:00:00Z"
        }
        """);

        var envelope = new KafkaMessageEnvelope
        {
            MessageId = Guid.NewGuid(),
            MessageType = "RentalOrderCreated",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = doc.RootElement
        };

        var result = handler.ParseIntent(envelope);
        Assert.True(result.IsSuccess);
        Assert.Equal(Guid.Parse("a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d"), result.Value.AggregateId);
        Assert.Equal(Guid.Parse("b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e"), result.Value.RenterId);
        Assert.Equal(1, result.Value.OrderVersion);
    }

    [Fact]
    public async Task Ingestion_MessageWithNonUtcTimestamp_QuarantinesToDlq()
    {
        using var db = CreateInMemoryDbContext();
        var (service, dlqMock) = CreateTestService(db);

        var messageId = Guid.NewGuid();
        var raw = $$"""
        {
            "messageId": "{{messageId}}",
            "messageType": "RentalOrderCreated",
            "version": 1,
            "producer": "RentalService",
            "aggregateId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
            "occurredAt": "2026-11-04T08:00:00Z",
            "payload": {
                "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
                "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
                "totalPayableAmount": 1500000,
                "orderVersion": 1,
                "paymentDeadline": "2026-11-04T08:00:00+07:00"
            }
        }
        """;

        var outcome = await service.IngestMessageAsync(raw, "rental.events.v1", 0, 1);
        Assert.Equal(IngestionOutcome.Quarantined, outcome);

        dlqMock.Verify(d => d.PublishToDeadLetterQueueAsync(
            raw,
            messageId.ToString(),
            It.IsAny<string>(),
            "INVALID_UTC_TIMESTAMP",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ingestion_MessageWithStringForAmount_QuarantinesToDlq()
    {
        using var db = CreateInMemoryDbContext();
        var (service, dlqMock) = CreateTestService(db);

        var messageId = Guid.NewGuid();
        var raw = $$"""
        {
            "messageId": "{{messageId}}",
            "messageType": "RentalOrderPaid",
            "version": 1,
            "producer": "RentalService",
            "aggregateId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
            "occurredAt": "2026-11-04T08:00:00Z",
            "payload": {
                "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
                "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
                "paidAmount": "1500000",
                "orderVersion": 2,
                "paidAt": "2026-11-04T08:15:00Z"
            }
        }
        """;

        var outcome = await service.IngestMessageAsync(raw, "rental.events.v1", 0, 2);
        Assert.Equal(IngestionOutcome.Quarantined, outcome);

        dlqMock.Verify(d => d.PublishToDeadLetterQueueAsync(
            raw,
            messageId.ToString(),
            It.IsAny<string>(),
            "INVALID_NUMBER_KIND",
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
