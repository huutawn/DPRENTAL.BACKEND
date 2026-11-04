using System.Text.Json;
using Dprental.NotificationService.Common;
using Dprental.NotificationService.Application.Clients;
using Dprental.NotificationService.Application.DTOs;
using Dprental.NotificationService.Application.Email;
using Dprental.NotificationService.Application.Email.Workers;
using Dprental.NotificationService.Application.IServices;
using Dprental.NotificationService.Application.Kafka.Contracts;
using Dprental.NotificationService.Application.Kafka.Handlers;
using Dprental.NotificationService.Application.Kafka.Quarantine;
using Dprental.NotificationService.Application.Kafka.Services;
using Dprental.NotificationService.Application.Recipients;
using Dprental.NotificationService.Application.Services;
using Dprental.NotificationService.Application.Templates;
using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Domain.IRepositories;
using Dprental.NotificationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.E2E;

public sealed class E2EScenariosHarnessTests
{
    private sealed class HarnessContext : IAsyncDisposable
    {
        public NotificationServiceDbContext Db { get; }
        public INotificationIngestionService IngestionService { get; }
        public INotificationQueryService QueryService { get; }
        public INotificationReadService ReadService { get; }
        public IPreferenceService PreferenceService { get; }
        public Mock<IKafkaDeadLetterPublisher> DlqMock { get; }
        public Mock<INotificationRealtimeNotifier> RealtimeMock { get; }
        public Mock<IAuthContactClient> AuthClientMock { get; }
        public Mock<IEmailSender> EmailSenderMock { get; }
        public IServiceProvider ServiceProvider { get; }

        public HarnessContext(string dbName)
        {
            var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;

            Db = new NotificationServiceDbContext(options);

            var uow = new UnitOfWork(Db);
            var inboxRepo = new InboxReceiptRepository(Db);
            var notifRepo = new NotificationRepository(Db);
            var deliveryRepo = new DeliveryJobRepository(Db);
            var prefRepo = new PreferenceRepository(Db);
            var watermarkRepo = new AggregateWatermarkRepository(Db);

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

            DlqMock = new Mock<IKafkaDeadLetterPublisher>();
            RealtimeMock = new Mock<INotificationRealtimeNotifier>();
            AuthClientMock = new Mock<IAuthContactClient>();
            EmailSenderMock = new Mock<IEmailSender>();

            IngestionService = new NotificationIngestionService(
                handlers,
                recipientResolver,
                templateRenderer,
                inboxRepo,
                notifRepo,
                deliveryRepo,
                prefRepo,
                watermarkRepo,
                uow,
                DlqMock.Object,
                RealtimeMock.Object,
                NullLogger<NotificationIngestionService>.Instance);

            QueryService = new NotificationQueryService(notifRepo);
            ReadService = new NotificationReadService(notifRepo, RealtimeMock.Object);
            PreferenceService = new PreferenceService(prefRepo, uow);

            var services = new ServiceCollection();
            services.AddSingleton<IDeliveryJobRepository>(deliveryRepo);
            services.AddSingleton<IPreferenceRepository>(prefRepo);
            services.AddSingleton(AuthClientMock.Object);
            services.AddSingleton(EmailSenderMock.Object);
            services.AddSingleton<INotificationTemplateRenderer>(templateRenderer);
            ServiceProvider = services.BuildServiceProvider();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
        }
    }

    // SCENARIO A: Standard Happy Path (Created -> Paid -> Email Sent -> In-app mark read)
    [Fact]
    public async Task ScenarioA_HappyPath_Created_Paid_Email_And_Read()
    {
        await using var ctx = new HarnessContext(Guid.NewGuid().ToString());
        var orderId = Guid.NewGuid();
        var renterId = Guid.NewGuid();

        // 1. Ingest RentalOrderCreated
        var envCreated = new KafkaMessageEnvelope
        {
            MessageId = Guid.NewGuid(),
            MessageType = "RentalOrderCreated",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                orderId = orderId,
                orderCode = "RNT-001",
                orderVersion = 1,
                renterId = renterId,
                totalPayableAmount = 1500000m,
                paymentDeadline = "2026-11-02T10:00:00Z"
            })
        };
        var outcome1 = await ctx.IngestionService.IngestMessageAsync(JsonSerializer.Serialize(envCreated), "rental.events.v1", 0, 1);
        Assert.Equal(IngestionOutcome.Processed, outcome1);

        // 2. Ingest RentalOrderPaid
        var envPaid = new KafkaMessageEnvelope
        {
            MessageId = Guid.NewGuid(),
            MessageType = "RentalOrderPaid",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                orderId = orderId,
                orderCode = "RNT-001",
                orderVersion = 2,
                renterId = renterId,
                paidAmount = 1500000m,
                invoiceRef = "INV-001",
                paidAt = DateTimeOffset.UtcNow
            })
        };
        var outcome2 = await ctx.IngestionService.IngestMessageAsync(JsonSerializer.Serialize(envPaid), "rental.events.v1", 0, 2);
        Assert.Equal(IngestionOutcome.Processed, outcome2);

        // 3. Process Email Delivery Job
        ctx.AuthClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
            {
                new AuthContactDto(renterId, "renter@example.com", IsActive: true, IsEmailVerified: true)
            }));

        ctx.EmailSenderMock.Setup(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailSendResult(EmailSendOutcome.Success, "smtp_msg_1", null, null));

        var worker = new EmailDeliveryWorker(ctx.ServiceProvider, NullLogger<EmailDeliveryWorker>.Instance);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            await worker.StartAsync(cts.Token);
            await Task.Delay(500);
            await worker.StopAsync(CancellationToken.None);
        }

        var paidJob = await ctx.Db.DeliveryJobs.FirstOrDefaultAsync(j => j.RecipientUserId == renterId);
        Assert.NotNull(paidJob);
        Assert.Equal("Succeeded", paidJob.Status);

        // 4. Mark notifications read via REST
        var unreadBefore = await ctx.QueryService.GetUnreadCountAsync(renterId);
        Assert.Equal(2, unreadBefore.Value.UnreadCount);

        var markAllResult = await ctx.ReadService.MarkAllAsReadAsync(renterId);
        Assert.True(markAllResult.IsSuccess);
        Assert.Equal(2, markAllResult.Value.MarkedCount);
        ctx.RealtimeMock.Verify(r => r.NotifyInboxChangedAsync(
            renterId,
            Guid.Empty,
            "READ_ALL",
            "ALL",
            markAllResult.Value.ReadAt,
            It.IsAny<CancellationToken>()), Times.Once);

        var unreadAfter = await ctx.QueryService.GetUnreadCountAsync(renterId);
        Assert.Equal(0, unreadAfter.Value.UnreadCount);
    }

    // SCENARIO B: Idempotent duplicate ingestion
    [Fact]
    public async Task ScenarioB_DuplicateMessageReplay_ShouldBeSkipped()
    {
        await using var ctx = new HarnessContext(Guid.NewGuid().ToString());
        var eventId = Guid.NewGuid();
        var raw = JsonSerializer.Serialize(new KafkaMessageEnvelope
        {
            MessageId = eventId,
            MessageType = "RentalOrderPaid",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                orderId = Guid.NewGuid(),
                orderCode = "RNT-002",
                orderVersion = 2,
                renterId = Guid.NewGuid(),
                paidAmount = 500000m,
                invoiceRef = "INV-002",
                paidAt = DateTimeOffset.UtcNow
            })
        });

        var res1 = await ctx.IngestionService.IngestMessageAsync(raw, "rental.events.v1", 0, 10);
        var res2 = await ctx.IngestionService.IngestMessageAsync(raw, "rental.events.v1", 0, 11);

        Assert.Equal(IngestionOutcome.Processed, res1);
        Assert.Equal(IngestionOutcome.Duplicate, res2);

        var receiptCount = await ctx.Db.InboxReceipts.CountAsync(r => r.MessageId == eventId);
        Assert.Equal(1, receiptCount);
    }

    // SCENARIO C: Out-of-order Stale Suppression
    [Fact]
    public async Task ScenarioC_OutOfOrderStale_CreatedAfterPaid_ActionSuppressed()
    {
        await using var ctx = new HarnessContext(Guid.NewGuid().ToString());
        var orderId = Guid.NewGuid();
        var renterId = Guid.NewGuid();

        // Paid arrives first
        var envPaid = new KafkaMessageEnvelope
        {
            MessageId = Guid.NewGuid(),
            MessageType = "RentalOrderPaid",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                orderId = orderId,
                orderCode = "RNT-003",
                orderVersion = 2,
                renterId = renterId,
                paidAmount = 2000000m,
                invoiceRef = "INV-003",
                paidAt = DateTimeOffset.UtcNow
            })
        };
        await ctx.IngestionService.IngestMessageAsync(JsonSerializer.Serialize(envPaid), "rental.events.v1", 0, 1);

        // Created arrives later (out of order, orderVersion 1 < 2)
        var envCreated = new KafkaMessageEnvelope
        {
            MessageId = Guid.NewGuid(),
            MessageType = "RentalOrderCreated",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            Payload = JsonSerializer.SerializeToElement(new
            {
                orderId = orderId,
                orderCode = "RNT-003",
                orderVersion = 1,
                renterId = renterId,
                totalPayableAmount = 2000000m,
                paymentDeadline = "2026-11-02T10:00:00Z"
            })
        };
        var outcome = await ctx.IngestionService.IngestMessageAsync(JsonSerializer.Serialize(envCreated), "rental.events.v1", 0, 2);
        Assert.Equal(IngestionOutcome.Processed, outcome);

        var createdNotif = await ctx.Db.Notifications.FirstOrDefaultAsync(n => n.NotificationType == "ORDER_CREATED" && n.ResourceId == orderId.ToString());
        Assert.NotNull(createdNotif);

        // Action must be suppressed and rendered as informational copy
        Assert.False(createdNotif.IsActionable);
        Assert.Null(createdNotif.ActionUrl);
        Assert.Contains("thông tin lưu trữ", createdNotif.Body);
    }

    // SCENARIO D: Terminal cancellation / expiration suppression
    [Fact]
    public async Task ScenarioD_OrderExpired_TerminalSuppressesPendingActions()
    {
        await using var ctx = new HarnessContext(Guid.NewGuid().ToString());
        var orderId = Guid.NewGuid();
        var renterId = Guid.NewGuid();

        var envExpired = new KafkaMessageEnvelope
        {
            MessageId = Guid.NewGuid(),
            MessageType = "RentalOrderExpired",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                orderId = orderId,
                orderCode = "RNT-EXPIRED",
                orderVersion = 99,
                renterId = renterId,
                reason = "Quá hạn thanh toán",
                expiredAt = DateTimeOffset.UtcNow
            })
        };
        await ctx.IngestionService.IngestMessageAsync(JsonSerializer.Serialize(envExpired), "rental.events.v1", 0, 1);

        var watermark = await ctx.Db.AggregateWatermarks.FirstOrDefaultAsync(w => w.AggregateId == orderId && w.RecipientUserId == renterId);
        Assert.NotNull(watermark);
        Assert.True(watermark.IsTerminal);
        Assert.Equal("EXPIRED", watermark.TerminalState);
    }

    // SCENARIO E: Sibling same-version events
    [Fact]
    public async Task ScenarioE_SameVersionSiblings_BothProcessed()
    {
        await using var ctx = new HarnessContext(Guid.NewGuid().ToString());
        var orderId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var raw1 = JsonSerializer.Serialize(new KafkaMessageEnvelope
        {
            MessageId = Guid.NewGuid(),
            MessageType = "RentalItemHandoverCompleted",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                orderId = orderId,
                orderCode = "RNT-SIB",
                orderVersion = 3,
                renterId = renterId,
                actualHandoverAt = DateTimeOffset.UtcNow.ToString("O")
            })
        });

        var raw2 = JsonSerializer.Serialize(new KafkaMessageEnvelope
        {
            MessageId = Guid.NewGuid(),
            MessageType = "RentalHandoverOverdue",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                orderId = orderId,
                orderCode = "RNT-SIB",
                orderVersion = 3,
                supplierId = supplierId,
                renterId = renterId,
                expectedStartDate = DateTimeOffset.UtcNow.ToString("O")
            })
        });

        var res1 = await ctx.IngestionService.IngestMessageAsync(raw1, "rental.events.v1", 0, 1);
        var res2 = await ctx.IngestionService.IngestMessageAsync(raw2, "rental.events.v1", 0, 2);

        Assert.Equal(IngestionOutcome.Processed, res1);
        Assert.Equal(IngestionOutcome.Processed, res2);

        var notifs = await ctx.Db.Notifications.Where(n => n.ResourceId == orderId.ToString()).ToListAsync();
        Assert.True(notifs.Count >= 2);
    }

    // SCENARIO F: Post-DATA SMTP failure transitions to Uncertain
    [Fact]
    public async Task ScenarioF_PostDataFailure_TransitionsToUncertain()
    {
        await using var ctx = new HarnessContext(Guid.NewGuid().ToString());
        var orderId = Guid.NewGuid();
        var renterId = Guid.NewGuid();

        var env = new KafkaMessageEnvelope
        {
            MessageId = Guid.NewGuid(),
            MessageType = "RentalOrderPaid",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                orderId = orderId,
                orderCode = "RNT-UNCERTAIN",
                orderVersion = 2,
                renterId = renterId,
                paidAmount = 1000000m,
                invoiceRef = "INV-U",
                paidAt = DateTimeOffset.UtcNow
            })
        };
        await ctx.IngestionService.IngestMessageAsync(JsonSerializer.Serialize(env), "rental.events.v1", 0, 1);

        ctx.AuthClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
            {
                new AuthContactDto(renterId, "renter@example.com", IsActive: true, IsEmailVerified: true)
            }));

        // Simulate PostDataError
        ctx.EmailSenderMock.Setup(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailSendResult(EmailSendOutcome.PostDataError, null, "SMTP_DROPPED_AFTER_DATA", "Timeout after DATA"));

        var worker = new EmailDeliveryWorker(ctx.ServiceProvider, NullLogger<EmailDeliveryWorker>.Instance);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            await worker.StartAsync(cts.Token);
            await Task.Delay(500);
            await worker.StopAsync(CancellationToken.None);
        }

        var job = await ctx.Db.DeliveryJobs.FirstOrDefaultAsync(j => j.RecipientUserId == renterId);
        Assert.NotNull(job);
        Assert.Equal("Uncertain", job.Status);
    }

    // SCENARIO G: Expired lease crash recovery
    [Fact]
    public async Task ScenarioG_WorkerCrash_LeaseRecoveryResetsToPending()
    {
        await using var ctx = new HarnessContext(Guid.NewGuid().ToString());
        var jobId = Guid.NewGuid();

        await ctx.Db.DeliveryJobs.AddAsync(new NotificationDeliveryJob
        {
            Id = jobId,
            NotificationId = Guid.NewGuid(),
            Channel = "EMAIL",
            RecipientUserId = Guid.NewGuid(),
            Status = "Processing",
            LeaseToken = "crashed_worker_token",
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5), // Expired!
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await ctx.Db.SaveChangesAsync();

        var reconciliationWorker = new EmailLeaseReconciliationWorker(ctx.ServiceProvider, NullLogger<EmailLeaseReconciliationWorker>.Instance);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            await reconciliationWorker.StartAsync(cts.Token);
            await Task.Delay(200);
            await reconciliationWorker.StopAsync(CancellationToken.None);
        }

        var recoveredJob = await ctx.Db.DeliveryJobs.FindAsync(jobId);
        Assert.NotNull(recoveredJob);
        Assert.Equal("Pending", recoveredJob.Status);
        Assert.Null(recoveredJob.LeaseToken);
    }

    // SCENARIO H: Disabled preference suppresses email delivery
    [Fact]
    public async Task ScenarioH_DisabledPreference_SuppressesEmail()
    {
        await using var ctx = new HarnessContext(Guid.NewGuid().ToString());
        var orderId = Guid.NewGuid();
        var renterId = Guid.NewGuid();

        // User turns off PAYMENT category email
        await ctx.PreferenceService.UpdatePreferencesAsync(renterId, new UpdatePreferencesRequestDto(new[]
        {
            new PreferenceDto("PAYMENT", false)
        }));

        var env = new KafkaMessageEnvelope
        {
            MessageId = Guid.NewGuid(),
            MessageType = "RentalOrderPaid",
            Producer = "RentalService",
            Version = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                orderId = orderId,
                orderCode = "RNT-NO-EMAIL",
                orderVersion = 2,
                renterId = renterId,
                paidAmount = 1000000m,
                invoiceRef = "INV-NO",
                paidAt = DateTimeOffset.UtcNow
            })
        };

        var outcome = await ctx.IngestionService.IngestMessageAsync(JsonSerializer.Serialize(env), "rental.events.v1", 0, 1);
        Assert.Equal(IngestionOutcome.Processed, outcome);

        var job = await ctx.Db.DeliveryJobs.FirstOrDefaultAsync(j => j.RecipientUserId == renterId);
        Assert.NotNull(job);
        Assert.Equal("Suppressed", job.Status);
        Assert.Equal("PREFERENCE_DISABLED", job.LastErrorCode);
    }
}
