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
}
