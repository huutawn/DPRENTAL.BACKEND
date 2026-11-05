using System.Text.Json;
using Dprental.NotificationService.Application.Clients;
using Dprental.NotificationService.Application.Email;
using Dprental.NotificationService.Application.Email.Services;
using Dprental.NotificationService.Application.Email.Workers;
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

namespace Dprental.NotificationService.Api.Tests.Email;

[Trait("Category", "Unit")]
public sealed class EmailDeliveryWorkerTests
{
    private static NotificationServiceDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new NotificationServiceDbContext(options);
    }

    [Fact]
    public async Task EmailWorker_WhenContactUnverifiedOrInactive_ShouldSuppressJob()
    {
        using var db = CreateInMemoryDbContext();
        var deliveryRepo = new DeliveryJobRepository(db);
        var prefRepo = new PreferenceRepository(db);

        var recipientId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();

        await db.Notifications.AddAsync(new Notification
        {
            Id = notificationId,
            RecipientUserId = recipientId,
            NotificationType = "ORDER_PAID",
            Category = "PAYMENT",
            Severity = "SUCCESS",
            Title = "Paid",
            Body = "Paid body",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            SourceEventId = Guid.NewGuid(),
            SourceProducer = "RentalService",
            SourceEventType = "RentalOrderPaid",
            TemplateCode = "ORDER_PAID",
            TemplateVersion = "1.0",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            Channel = "EMAIL",
            RecipientUserId = recipientId,
            Status = "Pending",
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.DeliveryJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var authClientMock = new Mock<IAuthContactClient>();
        // Return contact with isEmailVerified = false
        authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
            {
                new AuthContactDto(recipientId, "unverified@example.com", IsActive: true, IsEmailVerified: false)
            }));

        var emailSenderMock = new Mock<IEmailSender>();
        var templateRenderer = new NotificationTemplateRenderer();

        var services = new ServiceCollection();
        services.AddSingleton<IDeliveryJobRepository>(deliveryRepo);
        services.AddSingleton<IPreferenceRepository>(prefRepo);
        services.AddSingleton(authClientMock.Object);
        services.AddSingleton(emailSenderMock.Object);
        services.AddSingleton<INotificationTemplateRenderer>(templateRenderer);
        services.AddSingleton<IAggregateWatermarkRepository>(new AggregateWatermarkRepository(db));
        services.AddSingleton<IUnitOfWork>(new UnitOfWork(db));
        services.AddSingleton<IEmailDeliveryService, EmailDeliveryService>();
        var sp = services.BuildServiceProvider();

        var worker = new EmailDeliveryWorker(sp, NullLogger<EmailDeliveryWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        var updatedJob = await db.DeliveryJobs.FindAsync(job.Id);
        Assert.NotNull(updatedJob);
        Assert.Equal("Suppressed", updatedJob.Status);
        Assert.Equal("INACTIVE_OR_UNVERIFIED_EMAIL", updatedJob.LastErrorCode);

        // Verify email was NOT sent
        emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmailWorker_WhenPostDataError_ShouldTransitionToUncertain_WithoutBlindRetry()
    {
        using var db = CreateInMemoryDbContext();
        var deliveryRepo = new DeliveryJobRepository(db);
        var prefRepo = new PreferenceRepository(db);

        var recipientId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();

        await db.Notifications.AddAsync(new Notification
        {
            Id = notificationId,
            RecipientUserId = recipientId,
            NotificationType = "ORDER_PAID",
            Category = "PAYMENT",
            Severity = "SUCCESS",
            Title = "Paid",
            Body = "Paid body",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            SourceEventId = Guid.NewGuid(),
            SourceProducer = "RentalService",
            SourceEventType = "RentalOrderPaid",
            TemplateCode = "ORDER_PAID",
            TemplateVersion = "1.0",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            Channel = "EMAIL",
            RecipientUserId = recipientId,
            Status = "Pending",
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.DeliveryJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var authClientMock = new Mock<IAuthContactClient>();
        authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
            {
                new AuthContactDto(recipientId, "verified@example.com", IsActive: true, IsEmailVerified: true)
            }));

        var emailSenderMock = new Mock<IEmailSender>();
        // Simulate PostDataError (dropped after DATA phase)
        emailSenderMock.Setup(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailSendResult(
                Outcome: EmailSendOutcome.PostDataError,
                ProviderMessageId: null,
                ErrorCode: "SMTP_TIMEOUT_OR_DROPPED",
                ErrorDetail: "Connection dropped after DATA"));

        var templateRenderer = new NotificationTemplateRenderer();

        var services = new ServiceCollection();
        services.AddSingleton<IDeliveryJobRepository>(deliveryRepo);
        services.AddSingleton<IPreferenceRepository>(prefRepo);
        services.AddSingleton(authClientMock.Object);
        services.AddSingleton(emailSenderMock.Object);
        services.AddSingleton<INotificationTemplateRenderer>(templateRenderer);
        services.AddSingleton<IAggregateWatermarkRepository>(new AggregateWatermarkRepository(db));
        services.AddSingleton<IUnitOfWork>(new UnitOfWork(db));
        services.AddSingleton<IEmailDeliveryService, EmailDeliveryService>();
        var sp = services.BuildServiceProvider();

        var worker = new EmailDeliveryWorker(sp, NullLogger<EmailDeliveryWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        var updatedJob = await db.DeliveryJobs.FindAsync(job.Id);
        Assert.NotNull(updatedJob);

        // BẤT BIẾN: Chuyển sang Uncertain, KHÔNG tự động blind retry
        Assert.Equal("Uncertain", updatedJob.Status);
        Assert.Equal("SMTP_TIMEOUT_OR_DROPPED", updatedJob.LastErrorCode);
        Assert.NotNull(updatedJob.DispatchStartedAt);
    }

    [Fact]
    public async Task EmailWorker_WhenSuccessful_ShouldTransitionToSucceeded()
    {
        using var db = CreateInMemoryDbContext();
        var deliveryRepo = new DeliveryJobRepository(db);
        var prefRepo = new PreferenceRepository(db);

        var recipientId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();

        await db.Notifications.AddAsync(new Notification
        {
            Id = notificationId,
            RecipientUserId = recipientId,
            NotificationType = "ORDER_PAID",
            Category = "PAYMENT",
            Severity = "SUCCESS",
            Title = "Paid",
            Body = "Paid body",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            SourceEventId = Guid.NewGuid(),
            SourceProducer = "RentalService",
            SourceEventType = "RentalOrderPaid",
            TemplateCode = "ORDER_PAID",
            TemplateVersion = "1.0",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            Channel = "EMAIL",
            RecipientUserId = recipientId,
            Status = "Pending",
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.DeliveryJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var authClientMock = new Mock<IAuthContactClient>();
        authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
            {
                new AuthContactDto(recipientId, "verified@example.com", IsActive: true, IsEmailVerified: true)
            }));

        var emailSenderMock = new Mock<IEmailSender>();
        emailSenderMock.Setup(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailSendResult(
                Outcome: EmailSendOutcome.Success,
                ProviderMessageId: "provider_12345",
                ErrorCode: null,
                ErrorDetail: null));

        var templateRenderer = new NotificationTemplateRenderer();

        var services = new ServiceCollection();
        services.AddSingleton<IDeliveryJobRepository>(deliveryRepo);
        services.AddSingleton<IPreferenceRepository>(prefRepo);
        services.AddSingleton(authClientMock.Object);
        services.AddSingleton(emailSenderMock.Object);
        services.AddSingleton<INotificationTemplateRenderer>(templateRenderer);
        services.AddSingleton<IAggregateWatermarkRepository>(new AggregateWatermarkRepository(db));
        services.AddSingleton<IUnitOfWork>(new UnitOfWork(db));
        services.AddSingleton<IEmailDeliveryService, EmailDeliveryService>();
        var sp = services.BuildServiceProvider();

        var worker = new EmailDeliveryWorker(sp, NullLogger<EmailDeliveryWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        var updatedJob = await db.DeliveryJobs.FindAsync(job.Id);
        Assert.NotNull(updatedJob);
        Assert.Equal("Succeeded", updatedJob.Status);
        Assert.Equal("provider_12345", updatedJob.ProviderMessageId);
        Assert.NotNull(updatedJob.SentAt);
    }

    [Fact]
    public async Task EmailWorker_WhenAuthClientFails_ShouldScheduleRetry_NotSuppress()
    {
        using var db = CreateInMemoryDbContext();
        var deliveryRepo = new DeliveryJobRepository(db);
        var prefRepo = new PreferenceRepository(db);

        var recipientId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await db.Notifications.AddAsync(new Notification
        {
            Id = notificationId,
            RecipientUserId = recipientId,
            NotificationType = "ORDER_PAID",
            Category = "PAYMENT",
            Severity = "SUCCESS",
            Title = "Paid",
            Body = "Paid body",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            SourceEventId = Guid.NewGuid(),
            SourceProducer = "RentalService",
            SourceEventType = "RentalOrderPaid",
            TemplateCode = "ORDER_PAID",
            TemplateVersion = "1.0",
            OccurredAt = now,
            CreatedAt = now
        });

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            Channel = "EMAIL",
            RecipientUserId = recipientId,
            Status = "Pending",
            MaxAttempts = 5,
            AttemptCount = 0,
            NextAttemptAt = now.AddMinutes(-1),
            CreatedAt = now,
            UpdatedAt = now
        };
        await db.DeliveryJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var authClientMock = new Mock<IAuthContactClient>();
        authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<IReadOnlyList<AuthContactDto>>(Error.Failure("AUTH_OUTAGE", "AuthService 503 Service Unavailable")));

        var emailSenderMock = new Mock<IEmailSender>();
        var templateRenderer = new NotificationTemplateRenderer();

        var services = new ServiceCollection();
        services.AddSingleton<IDeliveryJobRepository>(deliveryRepo);
        services.AddSingleton<IPreferenceRepository>(prefRepo);
        services.AddSingleton(authClientMock.Object);
        services.AddSingleton(emailSenderMock.Object);
        services.AddSingleton<INotificationTemplateRenderer>(templateRenderer);
        services.AddSingleton<IAggregateWatermarkRepository>(new AggregateWatermarkRepository(db));
        services.AddSingleton<IUnitOfWork>(new UnitOfWork(db));
        services.AddSingleton<IEmailDeliveryService, EmailDeliveryService>();
        var sp = services.BuildServiceProvider();

        var worker = new EmailDeliveryWorker(sp, NullLogger<EmailDeliveryWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        var updatedJob = await db.DeliveryJobs.FindAsync(job.Id);
        Assert.NotNull(updatedJob);
        Assert.Equal("RetryScheduled", updatedJob.Status);
        Assert.Equal(1, updatedJob.AttemptCount);
        Assert.True(updatedJob.NextAttemptAt > now);
        Assert.Equal("AUTH_OUTAGE", updatedJob.LastErrorCode);
        Assert.Null(updatedJob.DispatchStartedAt);

        // Ensure email was NEVER sent
        emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmailWorker_WhenUserNotFoundInAuth_ShouldSuppressWithUserNotFound()
    {
        using var db = CreateInMemoryDbContext();
        var deliveryRepo = new DeliveryJobRepository(db);
        var prefRepo = new PreferenceRepository(db);

        var recipientId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await db.Notifications.AddAsync(new Notification
        {
            Id = notificationId,
            RecipientUserId = recipientId,
            NotificationType = "ORDER_PAID",
            Category = "PAYMENT",
            Severity = "SUCCESS",
            Title = "Paid",
            Body = "Paid body",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            SourceEventId = Guid.NewGuid(),
            SourceProducer = "RentalService",
            SourceEventType = "RentalOrderPaid",
            TemplateCode = "ORDER_PAID",
            TemplateVersion = "1.0",
            OccurredAt = now,
            CreatedAt = now
        });

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            Channel = "EMAIL",
            RecipientUserId = recipientId,
            Status = "Pending",
            NextAttemptAt = now.AddMinutes(-1),
            CreatedAt = now,
            UpdatedAt = now
        };
        await db.DeliveryJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var authClientMock = new Mock<IAuthContactClient>();
        // Auth returns Success, but the user is not in the list (e.g. user was deleted)
        authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(Array.Empty<AuthContactDto>()));

        var emailSenderMock = new Mock<IEmailSender>();
        var templateRenderer = new NotificationTemplateRenderer();

        var services = new ServiceCollection();
        services.AddSingleton<IDeliveryJobRepository>(deliveryRepo);
        services.AddSingleton<IPreferenceRepository>(prefRepo);
        services.AddSingleton(authClientMock.Object);
        services.AddSingleton(emailSenderMock.Object);
        services.AddSingleton<INotificationTemplateRenderer>(templateRenderer);
        services.AddSingleton<IAggregateWatermarkRepository>(new AggregateWatermarkRepository(db));
        services.AddSingleton<IUnitOfWork>(new UnitOfWork(db));
        services.AddSingleton<IEmailDeliveryService, EmailDeliveryService>();
        var sp = services.BuildServiceProvider();

        var worker = new EmailDeliveryWorker(sp, NullLogger<EmailDeliveryWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        var updatedJob = await db.DeliveryJobs.FindAsync(job.Id);
        Assert.NotNull(updatedJob);
        Assert.Equal("Suppressed", updatedJob.Status);
        Assert.Equal("USER_NOT_FOUND", updatedJob.LastErrorCode);

        emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmailWorker_WhenRecordDispatchStartFails_ShouldStopImmediatelyWithoutSending()
    {
        using var db = CreateInMemoryDbContext();
        var prefRepo = new PreferenceRepository(db);

        var recipientId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var notif = new Notification
        {
            Id = notificationId,
            RecipientUserId = recipientId,
            NotificationType = "ORDER_PAID",
            Category = "PAYMENT",
            Severity = "SUCCESS",
            Title = "Paid",
            Body = "Paid body",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            SourceEventId = Guid.NewGuid(),
            SourceProducer = "RentalService",
            SourceEventType = "RentalOrderPaid",
            TemplateCode = "ORDER_PAID",
            TemplateVersion = "1.0",
            OccurredAt = now,
            CreatedAt = now
        };
        await db.Notifications.AddAsync(notif);

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            Notification = notif,
            Channel = "EMAIL",
            RecipientUserId = recipientId,
            Status = "Processing",
            LeaseToken = "worker_token",
            LeaseExpiresAt = now.AddMinutes(5),
            NextAttemptAt = now.AddMinutes(-1),
            CreatedAt = now,
            UpdatedAt = now
        };

        var deliveryRepoMock = new Mock<IDeliveryJobRepository>();
        deliveryRepoMock.Setup(r => r.ClaimPendingJobsAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { job });
        // Simulate lease lost or expired: RecordDispatchStartAsync returns false
        deliveryRepoMock.Setup(r => r.RecordDispatchStartAsync(job.Id, job.LeaseToken, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var authClientMock = new Mock<IAuthContactClient>();
        authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
            {
                new AuthContactDto(recipientId, "verified@example.com", IsActive: true, IsEmailVerified: true)
            }));

        var emailSenderMock = new Mock<IEmailSender>();
        var templateRenderer = new NotificationTemplateRenderer();

        var services = new ServiceCollection();
        services.AddSingleton<IDeliveryJobRepository>(deliveryRepoMock.Object);
        services.AddSingleton<IPreferenceRepository>(prefRepo);
        services.AddSingleton(authClientMock.Object);
        services.AddSingleton(emailSenderMock.Object);
        services.AddSingleton<INotificationTemplateRenderer>(templateRenderer);
        services.AddSingleton<IAggregateWatermarkRepository>(new AggregateWatermarkRepository(db));
        services.AddSingleton<IUnitOfWork>(new UnitOfWork(db));
        services.AddSingleton<IEmailDeliveryService, EmailDeliveryService>();
        var sp = services.BuildServiceProvider();

        var worker = new EmailDeliveryWorker(sp, NullLogger<EmailDeliveryWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        // Verified: send was NEVER called because RecordDispatchStartAsync returned false
        emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmailDeliveryService_WhenPreDataError4xx_ResetsDispatchStartedAtAndSchedulesRetry()
    {
        using var db = CreateInMemoryDbContext();
        var deliveryRepo = new DeliveryJobRepository(db);
        var prefRepo = new PreferenceRepository(db);

        var recipientId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await db.Notifications.AddAsync(new Notification
        {
            Id = notificationId,
            RecipientUserId = recipientId,
            NotificationType = "ORDER_PAID",
            Category = "PAYMENT",
            Severity = "SUCCESS",
            Title = "Paid",
            Body = "Paid body",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            SourceEventId = Guid.NewGuid(),
            SourceProducer = "RentalService",
            SourceEventType = "RentalOrderPaid",
            TemplateCode = "ORDER_PAID",
            TemplateVersion = "1.0",
            OccurredAt = now,
            CreatedAt = now
        });

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            Channel = "EMAIL",
            RecipientUserId = recipientId,
            Status = "Pending",
            MaxAttempts = 5,
            AttemptCount = 0,
            NextAttemptAt = now.AddMinutes(-1),
            CreatedAt = now,
            UpdatedAt = now
        };
        await db.DeliveryJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var authClientMock = new Mock<IAuthContactClient>();
        authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
            {
                new AuthContactDto(recipientId, "user@example.com", IsActive: true, IsEmailVerified: true)
            }));

        var emailSenderMock = new Mock<IEmailSender>();
        emailSenderMock.Setup(s => s.SendEmailAsync(job.Id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailSendResult(
                Outcome: EmailSendOutcome.PreDataError,
                ProviderMessageId: null,
                ErrorCode: "TRANSIENT_SMTP_4XX",
                ErrorDetail: "421 Service not available"));

        var service = new EmailDeliveryService(
            deliveryRepo,
            prefRepo,
            authClientMock.Object,
            new NotificationTemplateRenderer(),
            emailSenderMock.Object,
            new AggregateWatermarkRepository(db),
            new UnitOfWork(db),
            NullLogger<EmailDeliveryService>.Instance);

        var processed = await service.DeliverPendingJobsAsync("worker-test", CancellationToken.None);
        Assert.Equal(1, processed);

        var updatedJob = await db.DeliveryJobs.FindAsync(job.Id);
        Assert.NotNull(updatedJob);
        Assert.Equal("RetryScheduled", updatedJob.Status);
        Assert.Equal("TRANSIENT_SMTP_4XX", updatedJob.LastErrorCode);
        // DispatchStartedAt must be reset to null when safely retrying!
        Assert.Null(updatedJob.DispatchStartedAt);
        Assert.Equal(1, updatedJob.AttemptCount);
    }

    [Fact]
    public async Task EmailDeliveryService_WhenPermanentRejection5xx_TransitionsToFailed()
    {
        using var db = CreateInMemoryDbContext();
        var deliveryRepo = new DeliveryJobRepository(db);
        var prefRepo = new PreferenceRepository(db);

        var recipientId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await db.Notifications.AddAsync(new Notification
        {
            Id = notificationId,
            RecipientUserId = recipientId,
            NotificationType = "ORDER_PAID",
            Category = "PAYMENT",
            Severity = "SUCCESS",
            Title = "Paid",
            Body = "Paid body",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            SourceEventId = Guid.NewGuid(),
            SourceProducer = "RentalService",
            SourceEventType = "RentalOrderPaid",
            TemplateCode = "ORDER_PAID",
            TemplateVersion = "1.0",
            OccurredAt = now,
            CreatedAt = now
        });

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            Channel = "EMAIL",
            RecipientUserId = recipientId,
            Status = "Pending",
            MaxAttempts = 5,
            AttemptCount = 0,
            NextAttemptAt = now.AddMinutes(-1),
            CreatedAt = now,
            UpdatedAt = now
        };
        await db.DeliveryJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var authClientMock = new Mock<IAuthContactClient>();
        authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
            {
                new AuthContactDto(recipientId, "user@example.com", IsActive: true, IsEmailVerified: true)
            }));

        var emailSenderMock = new Mock<IEmailSender>();
        emailSenderMock.Setup(s => s.SendEmailAsync(job.Id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailSendResult(
                Outcome: EmailSendOutcome.PermanentRejection,
                ProviderMessageId: null,
                ErrorCode: "PERMANENT_SMTP_FAILURE",
                ErrorDetail: "550 User unknown"));

        var service = new EmailDeliveryService(
            deliveryRepo,
            prefRepo,
            authClientMock.Object,
            new NotificationTemplateRenderer(),
            emailSenderMock.Object,
            new AggregateWatermarkRepository(db),
            new UnitOfWork(db),
            NullLogger<EmailDeliveryService>.Instance);

        var processed = await service.DeliverPendingJobsAsync("worker-test", CancellationToken.None);
        Assert.Equal(1, processed);

        var updatedJob = await db.DeliveryJobs.FindAsync(job.Id);
        Assert.NotNull(updatedJob);
        Assert.Equal("Failed", updatedJob.Status);
        Assert.Equal("PERMANENT_SMTP_FAILURE", updatedJob.LastErrorCode);
    }

    [Fact]
    public async Task EmailDeliveryService_WhenAttemptsExceedMax_TransitionsToFailed()
    {
        using var db = CreateInMemoryDbContext();
        var deliveryRepo = new DeliveryJobRepository(db);
        var prefRepo = new PreferenceRepository(db);

        var recipientId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await db.Notifications.AddAsync(new Notification
        {
            Id = notificationId,
            RecipientUserId = recipientId,
            NotificationType = "ORDER_PAID",
            Category = "PAYMENT",
            Severity = "SUCCESS",
            Title = "Paid",
            Body = "Paid body",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            SourceEventId = Guid.NewGuid(),
            SourceProducer = "RentalService",
            SourceEventType = "RentalOrderPaid",
            TemplateCode = "ORDER_PAID",
            TemplateVersion = "1.0",
            OccurredAt = now,
            CreatedAt = now
        });

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            Channel = "EMAIL",
            RecipientUserId = recipientId,
            Status = "RetryScheduled",
            MaxAttempts = 5,
            AttemptCount = 4, // Next claim will be attempt 5!
            NextAttemptAt = now.AddMinutes(-1),
            CreatedAt = now,
            UpdatedAt = now
        };
        await db.DeliveryJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var authClientMock = new Mock<IAuthContactClient>();
        authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
            {
                new AuthContactDto(recipientId, "user@example.com", IsActive: true, IsEmailVerified: true)
            }));

        var emailSenderMock = new Mock<IEmailSender>();
        emailSenderMock.Setup(s => s.SendEmailAsync(job.Id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailSendResult(
                Outcome: EmailSendOutcome.PreDataError,
                ProviderMessageId: null,
                ErrorCode: "TRANSIENT_SMTP_4XX",
                ErrorDetail: "421 Service busy"));

        var service = new EmailDeliveryService(
            deliveryRepo,
            prefRepo,
            authClientMock.Object,
            new NotificationTemplateRenderer(),
            emailSenderMock.Object,
            new AggregateWatermarkRepository(db),
            new UnitOfWork(db),
            NullLogger<EmailDeliveryService>.Instance);

        var processed = await service.DeliverPendingJobsAsync("worker-test", CancellationToken.None);
        Assert.Equal(1, processed);

        var updatedJob = await db.DeliveryJobs.FindAsync(job.Id);
        Assert.NotNull(updatedJob);
        // Attempt 5 reached, so PreDataError transitions to Failed instead of RetryScheduled
        Assert.Equal("Failed", updatedJob.Status);
        Assert.Equal("MAX_RETRIES_EXCEEDED", updatedJob.LastErrorCode);
    }

    [Fact]
    public async Task DeliveryJobRepository_RecoverExpiredLeases_WhenAttemptsExceeded_ShouldTransitionToFailed()
    {
        using var context = CreateInMemoryDbContext();
        var repo = new DeliveryJobRepository(context);

        var notificationId = Guid.NewGuid();
        context.Notifications.Add(new Notification
        {
            Id = notificationId,
            RecipientUserId = Guid.NewGuid(),
            NotificationType = "ORDER_PAID",
            Category = "ORDER",
            Severity = "INFO",
            Title = "Title",
            Body = "Body",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            SourceEventId = Guid.NewGuid(),
            SourceProducer = "RentalService",
            SourceEventType = "RentalOrderPaid",
            TemplateCode = "ORDER_PAID",
            TemplateVersion = "1.0",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            Channel = "EMAIL",
            RecipientUserId = Guid.NewGuid(),
            Status = "Pending",
            AttemptCount = 4,
            MaxAttempts = 5,
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await repo.AddAsync(job);
        await context.SaveChangesAsync();

        var claimed = await repo.ClaimPendingJobsAsync("worker-1", TimeSpan.FromMinutes(5), batchSize: 10);
        Assert.Single(claimed);
        Assert.Equal(5, claimed[0].AttemptCount); // Reached 5 attempts

        // Expire lease before dispatch started
        claimed[0].LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        claimed[0].DispatchStartedAt = null;
        await context.SaveChangesAsync();

        var recovered = await repo.RecoverExpiredLeasesAsync();
        Assert.Equal(1, recovered);

        var jobAfterRecovery = await context.DeliveryJobs.FindAsync(job.Id);
        Assert.NotNull(jobAfterRecovery);
        // Max 5 attempts reached, so recovery transitions to Failed
        Assert.Equal("Failed", jobAfterRecovery.Status);
        Assert.Equal("MAX_ATTEMPTS_EXCEEDED", jobAfterRecovery.LastErrorCode);
    }

    [Fact]
    public async Task EmailDeliveryService_WhenLeaseExpiresBeforeDispatch_AbortsImmediatelyWithoutCallingSmtp()
    {
        using var db = CreateInMemoryDbContext();
        var prefRepo = new PreferenceRepository(db);

        var recipientId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var notif = new Notification
        {
            Id = notificationId,
            RecipientUserId = recipientId,
            NotificationType = "ORDER_PAID",
            Category = "PAYMENT",
            Severity = "SUCCESS",
            Title = "Paid",
            Body = "Paid body",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            SourceEventId = Guid.NewGuid(),
            SourceProducer = "RentalService",
            SourceEventType = "RentalOrderPaid",
            TemplateCode = "ORDER_PAID",
            TemplateVersion = "1.0",
            OccurredAt = now,
            CreatedAt = now
        };

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            Notification = notif,
            Channel = "EMAIL",
            RecipientUserId = recipientId,
            Status = "Processing",
            LeaseToken = "worker_token",
            LeaseExpiresAt = now.AddSeconds(-1), // Expired before dispatch!
            NextAttemptAt = now.AddMinutes(-1),
            CreatedAt = now,
            UpdatedAt = now
        };

        var deliveryRepoMock = new Mock<IDeliveryJobRepository>();
        deliveryRepoMock.Setup(r => r.ClaimPendingJobsAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { job });

        var authClientMock = new Mock<IAuthContactClient>();
        authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
            {
                new AuthContactDto(recipientId, "verified@example.com", IsActive: true, IsEmailVerified: true)
            }));

        var emailSenderMock = new Mock<IEmailSender>();

        var service = new EmailDeliveryService(
            deliveryRepoMock.Object,
            prefRepo,
            authClientMock.Object,
            new NotificationTemplateRenderer(),
            emailSenderMock.Object,
            new AggregateWatermarkRepository(db),
            new UnitOfWork(db),
            NullLogger<EmailDeliveryService>.Instance);

        var processed = await service.DeliverPendingJobsAsync("worker-test", CancellationToken.None);
        Assert.Equal(1, processed);

        // Verified: send was NEVER called because lease expired
        emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        deliveryRepoMock.Verify(r => r.RecordDispatchStartAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmailDeliveryService_WhenJobStatusNotProcessing_AbortsImmediatelyWithoutCallingSmtp()
    {
        using var db = CreateInMemoryDbContext();
        var prefRepo = new PreferenceRepository(db);

        var recipientId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var notif = new Notification
        {
            Id = notificationId,
            RecipientUserId = recipientId,
            NotificationType = "ORDER_PAID",
            Category = "PAYMENT",
            Severity = "SUCCESS",
            Title = "Paid",
            Body = "Paid body",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            SourceEventId = Guid.NewGuid(),
            SourceProducer = "RentalService",
            SourceEventType = "RentalOrderPaid",
            TemplateCode = "ORDER_PAID",
            TemplateVersion = "1.0",
            OccurredAt = now,
            CreatedAt = now
        };

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            Notification = notif,
            Channel = "EMAIL",
            RecipientUserId = recipientId,
            Status = "Pending", // Not processing!
            LeaseToken = "worker_token",
            LeaseExpiresAt = now.AddMinutes(5),
            NextAttemptAt = now.AddMinutes(-1),
            CreatedAt = now,
            UpdatedAt = now
        };

        var deliveryRepoMock = new Mock<IDeliveryJobRepository>();
        deliveryRepoMock.Setup(r => r.ClaimPendingJobsAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { job });

        var authClientMock = new Mock<IAuthContactClient>();
        authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
            {
                new AuthContactDto(recipientId, "verified@example.com", IsActive: true, IsEmailVerified: true)
            }));

        var emailSenderMock = new Mock<IEmailSender>();

        var service = new EmailDeliveryService(
            deliveryRepoMock.Object,
            prefRepo,
            authClientMock.Object,
            new NotificationTemplateRenderer(),
            emailSenderMock.Object,
            new AggregateWatermarkRepository(db),
            new UnitOfWork(db),
            NullLogger<EmailDeliveryService>.Instance);

        var processed = await service.DeliverPendingJobsAsync("worker-test", CancellationToken.None);
        Assert.Equal(1, processed);

        emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        deliveryRepoMock.Verify(r => r.RecordDispatchStartAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmailDeliveryWorker_WhenSuppliedWithService_PollsAndCallsDeliverPendingJobs()
    {
        var serviceMock = new Mock<IEmailDeliveryService>();
        serviceMock.Setup(s => s.DeliverPendingJobsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var worker = new EmailDeliveryWorker(serviceMock.Object, NullLogger<EmailDeliveryWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await worker.StartAsync(cts.Token);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);

        serviceMock.Verify(s => s.DeliverPendingJobsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task EmailDeliveryService_OrderCompleted_IsNotSuppressedWhenOrderWatermarkIsCompleted()
    {
        using var db = CreateInMemoryDbContext();
        var deliveryRepo = new DeliveryJobRepository(db);
        var prefRepo = new PreferenceRepository(db);
        var watermarkRepo = new AggregateWatermarkRepository(db);
        var uow = new UnitOfWork(db);

        var recipientId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();

        await db.Notifications.AddAsync(new Notification
        {
            Id = notificationId,
            RecipientUserId = recipientId,
            OrderId = orderId,
            NotificationType = "ORDER_COMPLETED",
            Category = "ORDER",
            Severity = "SUCCESS",
            Title = "Order Completed",
            Body = "Order completed",
            ResourceType = "RentalOrder",
            ResourceId = orderId.ToString(),
            EventVersion = 5,
            RenderDataJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["displayOrderCode"] = "ORD-COMPLETED-01",
                ["completedAt"] = DateTimeOffset.UtcNow
            }),
            TemplateCode = "ORDER_COMPLETED",
            TemplateVersion = "1.0",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            Channel = "EMAIL",
            RecipientUserId = recipientId,
            Status = "Pending",
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.DeliveryJobs.AddAsync(job);

        await db.AggregateWatermarks.AddAsync(new NotificationAggregateWatermark
        {
            Scope = NotificationWatermarkScope.RentalOrder,
            AggregateId = orderId,
            RecipientUserId = recipientId,
            LastOrderVersion = 5,
            IsTerminal = true,
            TerminalState = "COMPLETED",
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();

        var authClientMock = new Mock<IAuthContactClient>();
        authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
            {
                new AuthContactDto(recipientId, "renter@example.com", IsActive: true, IsEmailVerified: true)
            }));

        var emailSenderMock = new Mock<IEmailSender>();
        emailSenderMock.Setup(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailSendResult(Outcome: EmailSendOutcome.Success, ProviderMessageId: "prov-123", ErrorCode: null, ErrorDetail: null));

        var service = new EmailDeliveryService(
            deliveryRepo,
            prefRepo,
            authClientMock.Object,
            new NotificationTemplateRenderer(),
            emailSenderMock.Object,
            watermarkRepo,
            uow,
            NullLogger<EmailDeliveryService>.Instance);

        var processed = await service.DeliverPendingJobsAsync("worker-1", CancellationToken.None);
        Assert.Equal(1, processed);

        var updatedJob = await db.DeliveryJobs.FindAsync(job.Id);
        Assert.NotNull(updatedJob);
        Assert.Equal("Succeeded", updatedJob.Status);

        emailSenderMock.Verify(s => s.SendEmailAsync(job.Id, "renter@example.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EmailDeliveryService_RefundCompleted_IsNotSuppressedWhenOrderWatermarkIsCancelled()
    {
        using var db = CreateInMemoryDbContext();
        var deliveryRepo = new DeliveryJobRepository(db);
        var prefRepo = new PreferenceRepository(db);
        var watermarkRepoMock = new Mock<IAggregateWatermarkRepository>();
        var uow = new UnitOfWork(db);

        var recipientId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var refundId = Guid.NewGuid();

        await db.Notifications.AddAsync(new Notification
        {
            Id = notificationId,
            RecipientUserId = recipientId,
            OrderId = orderId,
            NotificationType = "REFUND_COMPLETED",
            Category = "PAYMENT",
            Severity = "SUCCESS",
            Title = "Refund Completed",
            Body = "Refund completed",
            ResourceType = "Refund",
            ResourceId = refundId.ToString(),
            EventVersion = 4,
            RenderDataJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["displayOrderCode"] = "ORD-REFUND-01",
                ["amount"] = 500000m,
                ["currency"] = "VND",
                ["completedAt"] = DateTimeOffset.UtcNow,
                ["refundRef"] = "REF-9999",
                ["refundKind"] = "FULL_CANCELLATION",
                ["deepLinkUrl"] = $"/orders/{orderId}"
            }),
            TemplateCode = "REFUND_COMPLETED",
            TemplateVersion = "1.0",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            Channel = "EMAIL",
            RecipientUserId = recipientId,
            Status = "Pending",
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.DeliveryJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var authClientMock = new Mock<IAuthContactClient>();
        authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
            {
                new AuthContactDto(recipientId, "renter@example.com", IsActive: true, IsEmailVerified: true)
            }));

        var emailSenderMock = new Mock<IEmailSender>();
        emailSenderMock.Setup(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailSendResult(Outcome: EmailSendOutcome.Success, ProviderMessageId: "prov-refund-123", ErrorCode: null, ErrorDetail: null));

        var service = new EmailDeliveryService(
            deliveryRepo,
            prefRepo,
            authClientMock.Object,
            new NotificationTemplateRenderer(),
            emailSenderMock.Object,
            watermarkRepoMock.Object,
            uow,
            NullLogger<EmailDeliveryService>.Instance);

        var processed = await service.DeliverPendingJobsAsync("worker-1", CancellationToken.None);
        Assert.Equal(1, processed);

        var updatedJob = await db.DeliveryJobs.FindAsync(job.Id);
        Assert.NotNull(updatedJob);
        Assert.Equal("Succeeded", updatedJob.Status);

        // Verify email was sent
        emailSenderMock.Verify(s => s.SendEmailAsync(job.Id, "renter@example.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        // Invariant: Financial notifications must NOT acquire advisory lock and must NOT read rental order watermark
        watermarkRepoMock.Verify(w => w.AcquireAdvisoryLockAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        watermarkRepoMock.Verify(w => w.GetWatermarkAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        watermarkRepoMock.Verify(w => w.GetAggregateStateAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmailDeliveryService_SupplierConfirmationRequired_IsSuppressedWhenOrderWatermarkIsCancelled()
    {
        using var db = CreateInMemoryDbContext();
        var deliveryRepo = new DeliveryJobRepository(db);
        var prefRepo = new PreferenceRepository(db);
        var watermarkRepo = new AggregateWatermarkRepository(db);
        var uow = new UnitOfWork(db);

        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();

        await db.Notifications.AddAsync(new Notification
        {
            Id = notificationId,
            RecipientUserId = supplierId,
            OrderId = orderId,
            NotificationType = "SUPPLIER_CONFIRMATION_REQUIRED",
            Category = "ORDER",
            Severity = "ACTION_REQUIRED",
            Title = "Supplier Confirmation Required",
            Body = "Please confirm",
            ResourceType = "RentalOrder",
            ResourceId = orderId.ToString(),
            EventVersion = 2,
            RenderDataJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["displayOrderCode"] = "ORD-CANCELLED-01",
                ["supplierConfirmationDeadline"] = DateTimeOffset.UtcNow.AddHours(2)
            }),
            TemplateCode = "SUPPLIER_CONFIRMATION_REQUIRED",
            TemplateVersion = "1.0",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            Channel = "EMAIL",
            RecipientUserId = supplierId,
            Status = "Pending",
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.DeliveryJobs.AddAsync(job);

        await db.AggregateWatermarks.AddAsync(new NotificationAggregateWatermark
        {
            Scope = NotificationWatermarkScope.RentalOrder,
            AggregateId = orderId,
            RecipientUserId = supplierId,
            LastOrderVersion = 3,
            IsTerminal = true,
            TerminalState = "CANCELLED",
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();

        var authClientMock = new Mock<IAuthContactClient>();
        authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
            {
                new AuthContactDto(supplierId, "supplier@example.com", IsActive: true, IsEmailVerified: true)
            }));

        var emailSenderMock = new Mock<IEmailSender>();

        var service = new EmailDeliveryService(
            deliveryRepo,
            prefRepo,
            authClientMock.Object,
            new NotificationTemplateRenderer(),
            emailSenderMock.Object,
            watermarkRepo,
            uow,
            NullLogger<EmailDeliveryService>.Instance);

        var processed = await service.DeliverPendingJobsAsync("worker-1", CancellationToken.None);
        Assert.Equal(1, processed);

        var updatedJob = await db.DeliveryJobs.FindAsync(job.Id);
        Assert.NotNull(updatedJob);
        Assert.Equal("Suppressed", updatedJob.Status);
        Assert.Equal("STALE_ACTION_SUPPRESSED", updatedJob.LastErrorCode);

        // Verify email was NOT sent
        emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmailDeliveryService_WhenPreferenceDisabled_PreventsEmailDispatch()
    {
        using var db = CreateInMemoryDbContext();
        var deliveryRepo = new DeliveryJobRepository(db);
        var prefRepo = new PreferenceRepository(db);
        var watermarkRepo = new AggregateWatermarkRepository(db);
        var uow = new UnitOfWork(db);

        var recipientId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();

        await db.Notifications.AddAsync(new Notification
        {
            Id = notificationId,
            RecipientUserId = recipientId,
            NotificationType = "ORDER_PAID",
            Category = "PAYMENT",
            Severity = "SUCCESS",
            Title = "Order Paid",
            Body = "Order paid",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            EventVersion = 1,
            RenderDataJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["displayOrderCode"] = "ORD-PREF-01",
                ["paidAmount"] = 100000m,
                ["invoiceRef"] = "INV-001"
            }),
            TemplateCode = "ORDER_PAID",
            TemplateVersion = "1.0",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // User disabled PAYMENT email preference
        await db.Preferences.AddAsync(new NotificationPreference
        {
            Id = Guid.NewGuid(),
            UserId = recipientId,
            Category = "PAYMENT",
            EmailEnabled = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            Channel = "EMAIL",
            RecipientUserId = recipientId,
            Status = "Pending",
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.DeliveryJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var authClientMock = new Mock<IAuthContactClient>();
        authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
            {
                new AuthContactDto(recipientId, "user@example.com", IsActive: true, IsEmailVerified: true)
            }));

        var emailSenderMock = new Mock<IEmailSender>();

        var service = new EmailDeliveryService(
            deliveryRepo,
            prefRepo,
            authClientMock.Object,
            new NotificationTemplateRenderer(),
            emailSenderMock.Object,
            watermarkRepo,
            uow,
            NullLogger<EmailDeliveryService>.Instance);

        var processed = await service.DeliverPendingJobsAsync("worker-1", CancellationToken.None);
        Assert.Equal(1, processed);

        var updatedJob = await db.DeliveryJobs.FindAsync(job.Id);
        Assert.NotNull(updatedJob);
        Assert.Equal("Suppressed", updatedJob.Status);
        Assert.Equal("PREFERENCE_DISABLED", updatedJob.LastErrorCode);

        // Verify email was NOT sent
        emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmailDeliveryService_WhenContactInactive_PreventsEmailDispatch()
    {
        using var db = CreateInMemoryDbContext();
        var deliveryRepo = new DeliveryJobRepository(db);
        var prefRepo = new PreferenceRepository(db);
        var watermarkRepo = new AggregateWatermarkRepository(db);
        var uow = new UnitOfWork(db);

        var recipientId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();

        await db.Notifications.AddAsync(new Notification
        {
            Id = notificationId,
            RecipientUserId = recipientId,
            NotificationType = "ORDER_COMPLETED",
            Category = "ORDER",
            Severity = "SUCCESS",
            Title = "Order Completed",
            Body = "Order completed",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            EventVersion = 5,
            RenderDataJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["displayOrderCode"] = "ORD-INACTIVE-01",
                ["completedAt"] = DateTimeOffset.UtcNow
            }),
            TemplateCode = "ORDER_COMPLETED",
            TemplateVersion = "1.0",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var job = new NotificationDeliveryJob
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            Channel = "EMAIL",
            RecipientUserId = recipientId,
            Status = "Pending",
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.DeliveryJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var authClientMock = new Mock<IAuthContactClient>();
        // Contact is INACTIVE
        authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
            {
                new AuthContactDto(recipientId, "inactive@example.com", IsActive: false, IsEmailVerified: true)
            }));

        var emailSenderMock = new Mock<IEmailSender>();

        var service = new EmailDeliveryService(
            deliveryRepo,
            prefRepo,
            authClientMock.Object,
            new NotificationTemplateRenderer(),
            emailSenderMock.Object,
            watermarkRepo,
            uow,
            NullLogger<EmailDeliveryService>.Instance);

        var processed = await service.DeliverPendingJobsAsync("worker-1", CancellationToken.None);
        Assert.Equal(1, processed);

        var updatedJob = await db.DeliveryJobs.FindAsync(job.Id);
        Assert.NotNull(updatedJob);
        Assert.Equal("Suppressed", updatedJob.Status);
        Assert.Equal("INACTIVE_OR_UNVERIFIED_EMAIL", updatedJob.LastErrorCode);

        // Verify email was NOT sent
        emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
