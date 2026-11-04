using System.Text.Json;
using Dprental.NotificationService.Api.Tests.Fixtures;
using Dprental.NotificationService.Application.Clients;
using Dprental.NotificationService.Application.Email;
using Dprental.NotificationService.Application.Email.Workers;
using Dprental.NotificationService.Application.Email.Services;
using Dprental.NotificationService.Application.Kafka.Contracts;
using Dprental.NotificationService.Application.Kafka.Handlers;
using Dprental.NotificationService.Application.Templates;
using Dprental.NotificationService.Common;
using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Domain.IRepositories;
using Dprental.NotificationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.E2E;

public sealed class P0ReproFailureTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static NotificationServiceDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new NotificationServiceDbContext(options);
    }

    [Fact]
    public void Verified_CanonicalKafkaEnvelope_DeserializesRealProducerJson()
    {
        var messageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var json = ProducerFixtures.RentalOrderCreated(messageId, orderId, renterId);

        var envelope = JsonSerializer.Deserialize<KafkaMessageEnvelope>(json, JsonOptions);

        Assert.NotNull(envelope);
        Assert.Equal(messageId, envelope.MessageId);
        Assert.Equal("RentalOrderCreated", envelope.MessageType);
        Assert.Equal("RentalService", envelope.Producer);
        Assert.Equal(1, envelope.Version);
    }

    [Fact]
    public void Verified_Handler_SuccessfullyParsesRentalOrderIdAndItems()
    {
        var messageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var json = ProducerFixtures.RentalOrderCreated(messageId, orderId, renterId);

        using var doc = JsonDocument.Parse(json);
        var envelope = new KafkaMessageEnvelope
        {
            MessageId = messageId,
            MessageType = "RentalOrderCreated",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = doc.RootElement.GetProperty("payload")
        };

        var handler = new RentalOrderCreatedHandler();
        var result = handler.ParseIntent(envelope);

        Assert.True(result.IsSuccess);
        Assert.Equal(orderId, result.Value.OrderId);
        Assert.Equal(renterId, result.Value.RenterId);
        Assert.Equal(1, result.Value.OrderVersion);
        Assert.False(result.Value.IsTerminal);
    }

    [Fact]
    public void Verified_RentalOrderPaidHandler_IsNotTerminal()
    {
        var orderId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var json = $$"""
        {
            "rentalOrderId": "{{orderId}}",
            "renterId": "{{renterId}}",
            "orderVersion": 2,
            "paidAmount": 1500000,
            "invoiceRef": "INV-1"
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var envelope = new KafkaMessageEnvelope
        {
            MessageId = Guid.NewGuid(),
            MessageType = "RentalOrderPaid",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = doc.RootElement
        };

        var handler = new RentalOrderPaidHandler();
        var result = handler.ParseIntent(envelope);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsTerminal, "PAID must not be marked terminal so subsequent lifecycle events can process");
        Assert.Null(result.Value.TerminalState);
    }

    [Fact]
    public async Task Repro_AuthOutage_WorkerMarksPendingJobAsSuppressedInsteadOfRetrying()
    {
        var db = CreateDb();
        var deliveryRepo = new DeliveryJobRepository(db);
        var prefRepo = new PreferenceRepository(db);

        var userId = Guid.NewGuid();
        var notifId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var notif = new Notification
        {
            Id = notifId,
            RecipientUserId = userId,
            NotificationType = "ORDER_CREATED",
            Category = "ORDER",
            Severity = "INFO",
            Title = "Test",
            Body = "Test",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            SourceEventId = Guid.NewGuid(),
            SourceProducer = "RentalService",
            SourceEventType = "RentalOrderCreated",
            EventVersion = 1,
            TemplateCode = "TMPL_01",
            TemplateVersion = "1",
            RenderDataJson = "{}",
            OccurredAt = now,
            CreatedAt = now
        };
        await db.Notifications.AddAsync(notif);

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notifId,
            Channel = "EMAIL",
            RecipientUserId = userId,
            Status = "Pending",
            MaxAttempts = 5,
            AttemptCount = 0,
            NextAttemptAt = now.AddMinutes(-1),
            CreatedAt = now,
            UpdatedAt = now
        };
        await db.DeliveryJobs.AddAsync(job);
        await db.SaveChangesAsync();

        // Simulate AuthService being down (returns failure)
        var authClientMock = new Mock<IAuthContactClient>();
        authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<IReadOnlyList<AuthContactDto>>(Error.Failure("AUTH_DOWN", "AuthService unavailable")));

        var emailSenderMock = new Mock<IEmailSender>();
        var services = new ServiceCollection();
        services.AddScoped<IDeliveryJobRepository>(_ => deliveryRepo);
        services.AddScoped<IPreferenceRepository>(_ => prefRepo);
        services.AddScoped<IAuthContactClient>(_ => authClientMock.Object);
        services.AddScoped<INotificationTemplateRenderer>(_ => new NotificationTemplateRenderer());
        services.AddScoped<IEmailSender>(_ => emailSenderMock.Object);
        services.AddScoped<IAggregateWatermarkRepository>(_ => new AggregateWatermarkRepository(db));
        services.AddScoped<IUnitOfWork>(_ => new UnitOfWork(db));
        services.AddScoped<IEmailDeliveryService, EmailDeliveryService>();
        var sp = services.BuildServiceProvider();

        var worker = new EmailDeliveryWorker(sp, NullLogger<EmailDeliveryWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StartAsync(cts.Token);
        var timeout = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < timeout)
        {
            var checkJob = await db.DeliveryJobs.FindAsync(job.Id);
            if (checkJob != null && checkJob.Status != "Pending" && checkJob.Status != "Processing")
            {
                break;
            }
            await Task.Delay(50);
        }
        await worker.StopAsync(CancellationToken.None);

        var updatedJob = await db.DeliveryJobs.FindAsync(job.Id);
        Assert.NotNull(updatedJob);
        // Verified P0 fix: Job becomes RetryScheduled instead of Suppressed during Auth outage
        Assert.Equal("RetryScheduled", updatedJob.Status);
        Assert.Equal(1, updatedJob.AttemptCount);
        Assert.True(updatedJob.NextAttemptAt > now);
    }
}
