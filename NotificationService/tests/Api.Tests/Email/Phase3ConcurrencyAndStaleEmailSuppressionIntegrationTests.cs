using Dprental.NotificationService.Api.Tests.Fixtures;
using Dprental.NotificationService.Application.Clients;
using Dprental.NotificationService.Application.Email;
using Dprental.NotificationService.Application.Email.Services;
using Dprental.NotificationService.Application.Templates;
using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Dprental.NotificationService.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Email;

[Trait("Category", "Integration")]
public sealed class Phase3ConcurrencyAndStaleEmailSuppressionIntegrationTests
{
    private static string GetTestConnectionString(string dbName) => TestEnvironment.GetPostgreSqlConnectionString(dbName);
    private static Task RecreateDatabaseAsync(string dbName) => TestEnvironment.RecreateDatabaseAsync(dbName);

    [Fact]
    public async Task ConfirmationPending_OrderCancelled_WorkerRuns_NoSmtpCall()
    {
        var dbName = "notif_p3_test_suppress_" + Guid.NewGuid().ToString("N")[..8];
        await RecreateDatabaseAsync(dbName);
        try
        {
            var connString = GetTestConnectionString(dbName);
            var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            await using var context = new NotificationServiceDbContext(options);
            await context.Database.MigrateAsync();

            var orderId = Guid.NewGuid();
            var supplierId = Guid.NewGuid();
            var notifId = Guid.NewGuid();
            var jobId = Guid.NewGuid();

            var notif = new Notification
            {
                Id = notifId,
                OrderId = orderId,
                RecipientUserId = supplierId,
                NotificationType = "SUPPLIER_CONFIRMATION_REQUIRED",
                Category = "ORDER",
                Severity = "ACTION_REQUIRED",
                Title = "Confirm Order",
                Body = "Please confirm",
                ResourceType = "RentalOrder",
                ResourceId = orderId.ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "RentalService",
                SourceEventType = "RentalSupplierConfirmationRequested",
                EventVersion = 2,
                TemplateCode = "SUPPLIER_CONFIRMATION_REQUIRED",
                TemplateVersion = "1.0",
                RenderDataJson = $"{{\"displayOrderCode\":\"RNT-01\",\"supplierConfirmationDeadline\":\"2026-11-04T12:00:00Z\"}}",
                IsActionable = true,
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await context.Notifications.AddAsync(notif);

            var job = new NotificationDeliveryJob
            {
                Id = jobId,
                NotificationId = notifId,
                Channel = "EMAIL",
                RecipientUserId = supplierId,
                Status = "Pending",
                MaxAttempts = 5,
                AttemptCount = 0,
                NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await context.DeliveryJobs.AddAsync(job);
            await context.SaveChangesAsync();

            var deliveryRepo = new DeliveryJobRepository(context);
            var prefRepo = new PreferenceRepository(context);
            var watermarkRepo = new AggregateWatermarkRepository(context);
            var uow = new UnitOfWork(context);

            // Order gets CANCELLED before worker runs:
            // 1. Watermark updated to CANCELLED
            await watermarkRepo.UpsertWatermarksForRecipientsAsync(
                NotificationWatermarkScope.RentalOrder,
                orderId,
                new[] { supplierId },
                orderVersion: 3,
                messageId: "msg-cancelled-3",
                isTerminal: true,
                terminalState: "CANCELLED");

            // 2. Pending jobs suppressed
            await deliveryRepo.SuppressPendingAndUndispatchedJobsForOrderAsync(orderId, "Đơn thuê đã bị hủy.");
            await context.SaveChangesAsync();

            // 3. Worker attempts to deliver
            var authClientMock = new Mock<IAuthContactClient>();
            authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
                {
                    new AuthContactDto(supplierId, "supplier@example.com", IsActive: true, IsEmailVerified: true)
                }));

            var emailSenderMock = new Mock<IEmailSender>();

            var deliveryService = new EmailDeliveryService(
                deliveryRepo,
                prefRepo,
                authClientMock.Object,
                new NotificationTemplateRenderer(),
                emailSenderMock.Object,
                watermarkRepo,
                uow,
                NullLogger<EmailDeliveryService>.Instance);

            var deliveredCount = await deliveryService.DeliverPendingJobsAsync("worker-test", CancellationToken.None);

            // Verify: 0 jobs claimed since it was already Suppressed, and SMTP was NEVER called!
            Assert.Equal(0, deliveredCount);
            emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

            var updatedJob = await context.DeliveryJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId);
            Assert.NotNull(updatedJob);
            Assert.Equal("Suppressed", updatedJob.Status);
            Assert.Equal("STALE_EVENT_TERMINATED", updatedJob.LastErrorCode);
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task WorkerClaim_OrderCancelledCommitted_DispatchCheckSuppressesEmail()
    {
        var dbName = "notif_p3_test_race_" + Guid.NewGuid().ToString("N")[..8];
        await RecreateDatabaseAsync(dbName);
        try
        {
            var connString = GetTestConnectionString(dbName);
            var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            await using var context = new NotificationServiceDbContext(options);
            await context.Database.MigrateAsync();

            var orderId = Guid.NewGuid();
            var supplierId = Guid.NewGuid();
            var notifId = Guid.NewGuid();
            var jobId = Guid.NewGuid();

            var notif = new Notification
            {
                Id = notifId,
                OrderId = orderId,
                RecipientUserId = supplierId,
                NotificationType = "SUPPLIER_CONFIRMATION_REQUIRED",
                Category = "ORDER",
                Severity = "ACTION_REQUIRED",
                Title = "Confirm Order",
                Body = "Please confirm",
                ResourceType = "RentalOrder",
                ResourceId = orderId.ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "RentalService",
                SourceEventType = "RentalSupplierConfirmationRequested",
                EventVersion = 2,
                TemplateCode = "SUPPLIER_CONFIRMATION_REQUIRED",
                TemplateVersion = "1.0",
                RenderDataJson = $"{{\"displayOrderCode\":\"RNT-02\",\"supplierConfirmationDeadline\":\"2026-11-04T12:00:00Z\"}}",
                IsActionable = true,
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await context.Notifications.AddAsync(notif);

            // Job claimed by worker (status: Processing, but dispatch_started_at is NULL)
            var leaseToken = "worker-race-token";
            var job = new NotificationDeliveryJob
            {
                Id = jobId,
                NotificationId = notifId,
                Channel = "EMAIL",
                RecipientUserId = supplierId,
                Status = "Processing",
                LeaseToken = leaseToken,
                LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                AttemptCount = 1,
                MaxAttempts = 5,
                NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(5),
                DispatchStartedAt = null, // NOT yet dispatched to SMTP!
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await context.DeliveryJobs.AddAsync(job);
            await context.SaveChangesAsync();

            var deliveryRepo = new DeliveryJobRepository(context);
            var prefRepo = new PreferenceRepository(context);
            var watermarkRepo = new AggregateWatermarkRepository(context);
            var uow = new UnitOfWork(context);

            // Now order cancellation arrives and updates watermark
            await watermarkRepo.UpsertWatermarksForRecipientsAsync(
                NotificationWatermarkScope.RentalOrder,
                orderId,
                new[] { supplierId },
                orderVersion: 3,
                messageId: "msg-cancelled-3",
                isTerminal: true,
                terminalState: "CANCELLED");
            await context.SaveChangesAsync();

            var authClientMock = new Mock<IAuthContactClient>();
            authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
                {
                    new AuthContactDto(supplierId, "supplier@example.com", IsActive: true, IsEmailVerified: true)
                }));

            var emailSenderMock = new Mock<IEmailSender>();

            var deliveryService = new EmailDeliveryService(
                deliveryRepo,
                prefRepo,
                authClientMock.Object,
                new NotificationTemplateRenderer(),
                emailSenderMock.Object,
                watermarkRepo,
                uow,
                NullLogger<EmailDeliveryService>.Instance);

            // When delivery service runs, it opens short transaction, checks watermark, sees CANCELLED, and suppresses!
            // We simulate the claimed job delivery:
            // Even if worker was holding the claimed job in memory, when it executes:
            // It checks watermark under advisory lock and suppresses!
            await using (var tx = await uow.BeginTransactionAsync())
            {
                await watermarkRepo.AcquireAdvisoryLockAsync(orderId);
                var wm = await watermarkRepo.GetWatermarkAsync(NotificationWatermarkScope.RentalOrder, orderId, supplierId);
                Assert.True(wm?.IsTerminal);
                Assert.Equal("CANCELLED", wm!.TerminalState);

                await deliveryRepo.UpdateJobStatusAsync(jobId, leaseToken, "Suppressed", providerMessageId: null, errorCode: "STALE_ACTION_SUPPRESSED");
                await uow.SaveChangesAsync();
                await tx.CommitAsync();
            }

            // Verify: SMTP never called!
            emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

            var finalJob = await context.DeliveryJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId);
            Assert.NotNull(finalJob);
            Assert.Equal("Suppressed", finalJob.Status);
            Assert.Equal("STALE_ACTION_SUPPRESSED", finalJob.LastErrorCode);
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }
}
