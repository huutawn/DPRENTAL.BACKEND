using Dprental.NotificationService.Application.Clients;
using Dprental.NotificationService.Application.Email;
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
        emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
        emailSenderMock.Setup(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        emailSenderMock.Setup(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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

        // Ensure email was NEVER sent
        emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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

        emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
        var sp = services.BuildServiceProvider();

        var worker = new EmailDeliveryWorker(sp, NullLogger<EmailDeliveryWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        // Verified: send was NEVER called because RecordDispatchStartAsync returned false
        emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
