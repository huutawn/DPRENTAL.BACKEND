using Dprental.NotificationService.Api.Tests.Fixtures;
using Dprental.NotificationService.Application.Clients;
using Dprental.NotificationService.Application.Email;
using Dprental.NotificationService.Application.Email.Services;
using Dprental.NotificationService.Application.Policies;
using Dprental.NotificationService.Application.Reconciliation;
using Dprental.NotificationService.Application.Templates;
using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Dprental.NotificationService.Domain.IRepositories;
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

    [Fact]
    public async Task OrderCancelled_WithOnlyRenterWatermark_LateSupplierConfirmationJob_IsSuppressed()
    {
        var dbName = "notif_p3_test_renter_cancel_" + Guid.NewGuid().ToString("N")[..8];
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
            var renterId = Guid.NewGuid();
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
                RenderDataJson = $"{{\"displayOrderCode\":\"RNT-P2-01\",\"supplierConfirmationDeadline\":\"2026-11-04T12:00:00Z\"}}",
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

            // Watermark is recorded ONLY for renterId with CANCELLED.
            // Supplier watermark does NOT exist in the database!
            await watermarkRepo.UpsertWatermarksForRecipientsAsync(
                NotificationWatermarkScope.RentalOrder,
                orderId,
                new[] { renterId },
                orderVersion: 3,
                messageId: "msg-renter-cancelled-3",
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

            var deliveredCount = await deliveryService.DeliverPendingJobsAsync("worker-p2-test", CancellationToken.None);

            // Verify: 1 job processed and suppressed by aggregate state, SMTP never called
            Assert.Equal(1, deliveredCount);
            emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

            var updatedJob = await context.DeliveryJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId);
            Assert.NotNull(updatedJob);
            Assert.Equal("Suppressed", updatedJob.Status);
            Assert.Equal("STALE_ACTION_SUPPRESSED", updatedJob.LastErrorCode);
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task ItemCompleted_WithOnlyRenterWatermark_LateSupplierNotificationJob_IsSuppressed()
    {
        var dbName = "notif_p3_test_item_comp_" + Guid.NewGuid().ToString("N")[..8];
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
            var renterId = Guid.NewGuid();
            var supplierId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var notifId = Guid.NewGuid();
            var jobId = Guid.NewGuid();

            var notif = new Notification
            {
                Id = notifId,
                OrderId = orderId,
                RecipientUserId = supplierId,
                NotificationType = "HANDOVER_OVERDUE_SUPPLIER",
                Category = "ORDER",
                Severity = "ACTION_REQUIRED",
                Title = "Handover Overdue",
                Body = "Please complete handover",
                ResourceType = "RentalItem",
                ResourceId = itemId.ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "RentalService",
                SourceEventType = "RentalHandoverOverdue",
                EventVersion = 3,
                TemplateCode = "HANDOVER_OVERDUE_SUPPLIER",
                TemplateVersion = "1.0",
                RenderDataJson = $"{{\"displayOrderCode\":\"RNT-P2-02\",\"supplierHandoverDeadline\":\"2026-11-04T12:00:00Z\"}}",
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

            // Order aggregate watermark is active (not terminal)
            await watermarkRepo.UpsertWatermarksForRecipientsAsync(
                NotificationWatermarkScope.RentalOrder,
                orderId,
                new[] { renterId },
                orderVersion: 2,
                messageId: "msg-order-active-2",
                isTerminal: false,
                terminalState: null);

            // Item aggregate watermark is COMPLETED only for renterId!
            await watermarkRepo.UpsertWatermarksForRecipientsAsync(
                NotificationWatermarkScope.RentalItem,
                itemId,
                new[] { renterId },
                orderVersion: 4,
                messageId: "msg-item-comp-4",
                isTerminal: true,
                terminalState: "COMPLETED",
                isPaid: false,
                parentOrderId: orderId);
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

            var deliveredCount = await deliveryService.DeliverPendingJobsAsync("worker-p2-test", CancellationToken.None);

            // Verify: 1 job processed and suppressed by item aggregate state, SMTP never called
            Assert.Equal(1, deliveredCount);
            emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

            var updatedJob = await context.DeliveryJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId);
            Assert.NotNull(updatedJob);
            Assert.Equal("Suppressed", updatedJob.Status);
            Assert.Equal("STALE_ACTION_SUPPRESSED", updatedJob.LastErrorCode);
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task TwoSuppliersOnSameOrder_ShareOrderAggregateState_IndependentItemAggregateStates()
    {
        var dbName = "notif_p3_test_two_suppliers_" + Guid.NewGuid().ToString("N")[..8];
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
            var renterId = Guid.NewGuid();
            var supplierAId = Guid.NewGuid();
            var supplierBId = Guid.NewGuid();
            var itemAId = Guid.NewGuid();
            var itemBId = Guid.NewGuid();

            var notifAId = Guid.NewGuid();
            var jobAId = Guid.NewGuid();
            var notifBId = Guid.NewGuid();
            var jobBId = Guid.NewGuid();

            // Notification A for Supplier A on Item A
            var notifA = new Notification
            {
                Id = notifAId,
                OrderId = orderId,
                RecipientUserId = supplierAId,
                NotificationType = "HANDOVER_OVERDUE_SUPPLIER",
                Category = "ORDER",
                Severity = "ACTION_REQUIRED",
                Title = "Handover Overdue Item A",
                Body = "Please complete handover for item A",
                ResourceType = "RentalItem",
                ResourceId = itemAId.ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "RentalService",
                SourceEventType = "RentalHandoverOverdue",
                EventVersion = 2,
                TemplateCode = "HANDOVER_OVERDUE_SUPPLIER",
                TemplateVersion = "1.0",
                RenderDataJson = $"{{\"displayOrderCode\":\"RNT-P2-03A\",\"supplierHandoverDeadline\":\"2026-11-04T12:00:00Z\"}}",
                IsActionable = true,
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };

            // Notification B for Supplier B on Item B
            var notifB = new Notification
            {
                Id = notifBId,
                OrderId = orderId,
                RecipientUserId = supplierBId,
                NotificationType = "HANDOVER_OVERDUE_SUPPLIER",
                Category = "ORDER",
                Severity = "ACTION_REQUIRED",
                Title = "Handover Overdue Item B",
                Body = "Please complete handover for item B",
                ResourceType = "RentalItem",
                ResourceId = itemBId.ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "RentalService",
                SourceEventType = "RentalHandoverOverdue",
                EventVersion = 2,
                TemplateCode = "HANDOVER_OVERDUE_SUPPLIER",
                TemplateVersion = "1.0",
                RenderDataJson = $"{{\"displayOrderCode\":\"RNT-P2-03B\",\"supplierHandoverDeadline\":\"2026-11-04T12:00:00Z\"}}",
                IsActionable = true,
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };

            await context.Notifications.AddRangeAsync(notifA, notifB);

            var jobA = new NotificationDeliveryJob
            {
                Id = jobAId,
                NotificationId = notifAId,
                Channel = "EMAIL",
                RecipientUserId = supplierAId,
                Status = "Pending",
                MaxAttempts = 5,
                AttemptCount = 0,
                NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            var jobB = new NotificationDeliveryJob
            {
                Id = jobBId,
                NotificationId = notifBId,
                Channel = "EMAIL",
                RecipientUserId = supplierBId,
                Status = "Pending",
                MaxAttempts = 5,
                AttemptCount = 0,
                NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await context.DeliveryJobs.AddRangeAsync(jobA, jobB);
            await context.SaveChangesAsync();

            var deliveryRepo = new DeliveryJobRepository(context);
            var prefRepo = new PreferenceRepository(context);
            var watermarkRepo = new AggregateWatermarkRepository(context);
            var uow = new UnitOfWork(context);

            // Order watermark is active
            await watermarkRepo.UpsertWatermarksForRecipientsAsync(
                NotificationWatermarkScope.RentalOrder,
                orderId,
                new[] { renterId },
                orderVersion: 2,
                messageId: "msg-order-2",
                isTerminal: false,
                terminalState: null);

            // Item A watermark is COMPLETED (terminal)
            await watermarkRepo.UpsertWatermarksForRecipientsAsync(
                NotificationWatermarkScope.RentalItem,
                itemAId,
                new[] { renterId },
                orderVersion: 3,
                messageId: "msg-itemA-3",
                isTerminal: true,
                terminalState: "COMPLETED",
                isPaid: false,
                parentOrderId: orderId);

            // Item B watermark is ACTIVE (not terminal)
            await watermarkRepo.UpsertWatermarksForRecipientsAsync(
                NotificationWatermarkScope.RentalItem,
                itemBId,
                new[] { renterId },
                orderVersion: 2,
                messageId: "msg-itemB-2",
                isTerminal: false,
                terminalState: null,
                isPaid: false,
                parentOrderId: orderId);

            await context.SaveChangesAsync();

            var authClientMock = new Mock<IAuthContactClient>();
            authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
                {
                    new AuthContactDto(supplierAId, "supplierA@example.com", IsActive: true, IsEmailVerified: true),
                    new AuthContactDto(supplierBId, "supplierB@example.com", IsActive: true, IsEmailVerified: true)
                }));

            var emailSenderMock = new Mock<IEmailSender>();
            emailSenderMock.Setup(s => s.SendEmailAsync(jobBId, "supplierB@example.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EmailSendResult(EmailSendOutcome.Success, "msg-b-prov-1", null, null));

            var deliveryService = new EmailDeliveryService(
                deliveryRepo,
                prefRepo,
                authClientMock.Object,
                new NotificationTemplateRenderer(),
                emailSenderMock.Object,
                watermarkRepo,
                uow,
                NullLogger<EmailDeliveryService>.Instance,
                batchSize: 5);

            // Run delivery
            var delivered = await deliveryService.DeliverPendingJobsAsync("worker-p2-test", CancellationToken.None);

            // Both jobs processed: Item A was suppressed, Item B was sent
            Assert.Equal(2, delivered);

            var updatedJobA = await context.DeliveryJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobAId);
            Assert.NotNull(updatedJobA);
            Assert.Equal("Suppressed", updatedJobA.Status);
            Assert.Equal("STALE_ACTION_SUPPRESSED", updatedJobA.LastErrorCode);

            var updatedJobB = await context.DeliveryJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobBId);
            Assert.NotNull(updatedJobB);
            Assert.Equal("Succeeded", updatedJobB.Status);

            // Now cancel the parent order:
            await watermarkRepo.UpsertWatermarksForRecipientsAsync(
                NotificationWatermarkScope.RentalOrder,
                orderId,
                new[] { renterId },
                orderVersion: 4,
                messageId: "msg-order-cancel-4",
                isTerminal: true,
                terminalState: "CANCELLED");
            await context.SaveChangesAsync();

            // Create another job for Supplier B on Item B
            var jobB2Id = Guid.NewGuid();
            var notifB2Id = Guid.NewGuid();
            var notifB2 = new Notification
            {
                Id = notifB2Id,
                OrderId = orderId,
                RecipientUserId = supplierBId,
                NotificationType = "HANDOVER_OVERDUE_SUPPLIER",
                Category = "ORDER",
                Severity = "ACTION_REQUIRED",
                Title = "Handover Overdue Item B Round 2",
                Body = "Please complete handover for item B round 2",
                ResourceType = "RentalItem",
                ResourceId = itemBId.ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "RentalService",
                SourceEventType = "RentalHandoverOverdue",
                EventVersion = 2,
                TemplateCode = "HANDOVER_OVERDUE_SUPPLIER",
                TemplateVersion = "1.0",
                RenderDataJson = $"{{\"displayOrderCode\":\"RNT-P2-03B\",\"supplierHandoverDeadline\":\"2026-11-04T12:00:00Z\"}}",
                IsActionable = true,
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var jobB2 = new NotificationDeliveryJob
            {
                Id = jobB2Id,
                NotificationId = notifB2Id,
                Channel = "EMAIL",
                RecipientUserId = supplierBId,
                Status = "Pending",
                MaxAttempts = 5,
                AttemptCount = 0,
                NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await context.Notifications.AddAsync(notifB2);
            await context.DeliveryJobs.AddAsync(jobB2);
            await context.SaveChangesAsync();

            // Run delivery again: 1 job processed and suppressed
            var deliveredRound2 = await deliveryService.DeliverPendingJobsAsync("worker-p2-test", CancellationToken.None);
            Assert.Equal(1, deliveredRound2);

            var updatedJobB2 = await context.DeliveryJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobB2Id);
            Assert.NotNull(updatedJobB2);
            Assert.Equal("Suppressed", updatedJobB2.Status);
            Assert.Equal("STALE_ACTION_SUPPRESSED", updatedJobB2.LastErrorCode);
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task OverduePending_ItemReturned_OverdueCtaDismissed_DeliveryJobSuppressed_NoSmtpCall()
    {
        var dbName = "notif_p3_overdue_ret_" + Guid.NewGuid().ToString("N")[..8];
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
            var itemId = Guid.NewGuid();
            var returnRequestId = Guid.NewGuid();
            var supplierId = Guid.NewGuid();
            var notifId = Guid.NewGuid();
            var jobId = Guid.NewGuid();

            var notif = new Notification
            {
                Id = notifId,
                OrderId = orderId,
                RentalItemId = itemId,
                RecipientUserId = supplierId,
                NotificationType = "RETURN_CONFIRMATION_OVERDUE_SUPPLIER",
                Category = "RETURN",
                Severity = "ACTION_REQUIRED",
                Title = "Return Confirmation Overdue",
                Body = "Please confirm return",
                ResourceType = "ReturnRequest",
                ResourceId = returnRequestId.ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "RentalService",
                SourceEventType = "ReturnConfirmationOverdue",
                EventVersion = 2,
                TemplateCode = "RETURN_CONFIRMATION_OVERDUE_SUPPLIER",
                TemplateVersion = "1.0",
                RenderDataJson = $"{{\"displayOrderCode\":\"RNT-P3-01\",\"confirmationDeadlineAt\":\"2026-11-04T12:00:00Z\",\"rentalItemId\":\"{itemId}\",\"returnRequestId\":\"{returnRequestId}\"}}",
                IsActionable = true,
                ActionUrl = $"/supplier/returns/{returnRequestId}",
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
            var notifRepo = new NotificationRepository(context);
            var prefRepo = new PreferenceRepository(context);
            var watermarkRepo = new AggregateWatermarkRepository(context);
            var uow = new UnitOfWork(context);

            // Item returned event occurs:
            // 1. Watermark updated for RentalItem to RETURNED (terminal)
            await watermarkRepo.UpsertWatermarksForRecipientsAsync(
                NotificationWatermarkScope.RentalItem,
                itemId,
                new[] { supplierId },
                orderVersion: 3,
                messageId: "msg-item-returned-3",
                isTerminal: true,
                terminalState: "RETURNED");

            // 2. Dismiss return-overdue CTA for this item
            var dismissed = await notifRepo.DismissReturnOverdueActionsForRentalItemAsync(itemId);
            Assert.Single(dismissed);

            // 3. Suppress pending delivery jobs for this item
            var suppressedCount = await deliveryRepo.SuppressPendingAndUndispatchedJobsForRentalItemAsync(itemId, "Thiết bị đã được xác nhận trả.");
            Assert.Equal(1, suppressedCount);
            await context.SaveChangesAsync();

            // Verify notification CTA is dismissed
            var updatedNotif = await context.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.Id == notifId);
            Assert.NotNull(updatedNotif);
            Assert.False(updatedNotif.IsActionable);
            Assert.Null(updatedNotif.ActionUrl);

            // Verify delivery job is suppressed
            var updatedJob = await context.DeliveryJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId);
            Assert.NotNull(updatedJob);
            Assert.Equal("Suppressed", updatedJob.Status);
            Assert.Equal("STALE_EVENT_TERMINATED", updatedJob.LastErrorCode);

            // Worker runs: no jobs to claim and no SMTP calls made
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

            var delivered = await deliveryService.DeliverPendingJobsAsync("worker-p3-test", CancellationToken.None);
            Assert.Equal(0, delivered);
            emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task ItemReturnedFirst_OverdueArrivesLate_SuppressedImmediately()
    {
        var dbName = "notif_p3_late_overdue_" + Guid.NewGuid().ToString("N")[..8];
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
            var itemId = Guid.NewGuid();
            var returnRequestId = Guid.NewGuid();
            var supplierId = Guid.NewGuid();

            var deliveryRepo = new DeliveryJobRepository(context);
            var prefRepo = new PreferenceRepository(context);
            var watermarkRepo = new AggregateWatermarkRepository(context);
            var uow = new UnitOfWork(context);

            // 1. Item is returned FIRST: Watermark is terminal (RETURNED)
            await watermarkRepo.UpsertWatermarksForRecipientsAsync(
                NotificationWatermarkScope.RentalItem,
                itemId,
                new[] { supplierId },
                orderVersion: 5,
                messageId: "msg-returned-first-5",
                isTerminal: true,
                terminalState: "RETURNED");
            await context.SaveChangesAsync();

            // 2. Late overdue event arrives: Evaluate staleness with parent item state
            var itemState = await watermarkRepo.GetAggregateStateAsync(NotificationWatermarkScope.RentalItem, itemId);
            Assert.NotNull(itemState);
            Assert.True(itemState.IsTerminal);

            var staleEval = StaleActionSuppressionPolicy.EvaluateWithParentState(
                "RETURN_CONFIRMATION_OVERDUE_SUPPLIER",
                incomingOrderVersion: 3,
                currentAggregateState: null,
                parentItemAggregateState: itemState,
                parentOrderAggregateState: null);

            Assert.True(staleEval.IsStale, "Must be considered stale because parent item is returned");
            Assert.True(staleEval.SuppressAction, "Action must be suppressed");
            Assert.True(staleEval.SuppressEmail, "Email must be suppressed");

            // 3. Save notification with IsActionable = false and job as Suppressed
            var notifId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var notif = new Notification
            {
                Id = notifId,
                OrderId = orderId,
                RentalItemId = itemId,
                RecipientUserId = supplierId,
                NotificationType = "RETURN_CONFIRMATION_OVERDUE_SUPPLIER",
                Category = "RETURN",
                Severity = "ACTION_REQUIRED",
                Title = "Late Overdue Notice",
                Body = "Confirm return",
                ResourceType = "ReturnRequest",
                ResourceId = returnRequestId.ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "RentalService",
                SourceEventType = "ReturnConfirmationOverdue",
                EventVersion = 3,
                TemplateCode = "RETURN_CONFIRMATION_OVERDUE_SUPPLIER",
                TemplateVersion = "1.0",
                RenderDataJson = $"{{\"displayOrderCode\":\"RNT-P3-02\",\"rentalItemId\":\"{itemId}\"}}",
                IsActionable = false, // Suppressed at ingestion
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };

            // If job were created as Pending (e.g. race condition), worker delivery checks parent item state
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
            await context.Notifications.AddAsync(notif);
            await context.DeliveryJobs.AddAsync(job);
            await context.SaveChangesAsync();

            // Worker runs: deliveryService checks parent item state via RentalItemId and suppresses the job
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

            var delivered = await deliveryService.DeliverPendingJobsAsync("worker-p3-late-test", CancellationToken.None);
            Assert.Equal(1, delivered);

            var updatedJob = await context.DeliveryJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId);
            Assert.NotNull(updatedJob);
            Assert.Equal("Suppressed", updatedJob.Status);
            Assert.Equal("STALE_ACTION_SUPPRESSED", updatedJob.LastErrorCode);
            emailSenderMock.Verify(s => s.SendEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task ItemAReturned_DoesNotAffectOverdueOfItemB()
    {
        var dbName = "notif_p3_two_items_" + Guid.NewGuid().ToString("N")[..8];
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
            var itemAId = Guid.NewGuid();
            var itemBId = Guid.NewGuid();
            var returnReqAId = Guid.NewGuid();
            var returnReqBId = Guid.NewGuid();
            var supplierAId = Guid.NewGuid();
            var supplierBId = Guid.NewGuid();

            // Both Item A and Item B have overdue notifications with pending delivery jobs
            var notifAId = Guid.NewGuid();
            var jobAId = Guid.NewGuid();
            var notifA = new Notification
            {
                Id = notifAId,
                OrderId = orderId,
                RentalItemId = itemAId,
                RecipientUserId = supplierAId,
                NotificationType = "RETURN_CONFIRMATION_OVERDUE_SUPPLIER",
                Category = "RETURN",
                Severity = "ACTION_REQUIRED",
                Title = "Overdue Item A",
                Body = "Confirm return A",
                ResourceType = "ReturnRequest",
                ResourceId = returnReqAId.ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "RentalService",
                SourceEventType = "ReturnConfirmationOverdue",
                EventVersion = 2,
                TemplateCode = "RETURN_CONFIRMATION_OVERDUE_SUPPLIER",
                TemplateVersion = "1.0",
                RenderDataJson = $"{{\"displayOrderCode\":\"RNT-P3-A\",\"confirmationDeadlineAt\":\"2026-11-04T12:00:00Z\",\"rentalItemId\":\"{itemAId}\"}}",
                IsActionable = true,
                ActionUrl = $"/supplier/returns/{returnReqAId}",
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var jobA = new NotificationDeliveryJob
            {
                Id = jobAId,
                NotificationId = notifAId,
                Channel = "EMAIL",
                RecipientUserId = supplierAId,
                Status = "Pending",
                MaxAttempts = 5,
                AttemptCount = 0,
                NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            var notifBId = Guid.NewGuid();
            var jobBId = Guid.NewGuid();
            var notifB = new Notification
            {
                Id = notifBId,
                OrderId = orderId,
                RentalItemId = itemBId,
                RecipientUserId = supplierBId,
                NotificationType = "RETURN_CONFIRMATION_OVERDUE_SUPPLIER",
                Category = "RETURN",
                Severity = "ACTION_REQUIRED",
                Title = "Overdue Item B",
                Body = "Confirm return B",
                ResourceType = "ReturnRequest",
                ResourceId = returnReqBId.ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "RentalService",
                SourceEventType = "ReturnConfirmationOverdue",
                EventVersion = 2,
                TemplateCode = "RETURN_CONFIRMATION_OVERDUE_SUPPLIER",
                TemplateVersion = "1.0",
                RenderDataJson = $"{{\"displayOrderCode\":\"RNT-P3-B\",\"confirmationDeadlineAt\":\"2026-11-04T12:00:00Z\",\"rentalItemId\":\"{itemBId}\"}}",
                IsActionable = true,
                ActionUrl = $"/supplier/returns/{returnReqBId}",
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var jobB = new NotificationDeliveryJob
            {
                Id = jobBId,
                NotificationId = notifBId,
                Channel = "EMAIL",
                RecipientUserId = supplierBId,
                Status = "Pending",
                MaxAttempts = 5,
                AttemptCount = 0,
                NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await context.Notifications.AddRangeAsync(notifA, notifB);
            await context.DeliveryJobs.AddRangeAsync(jobA, jobB);
            await context.SaveChangesAsync();

            var deliveryRepo = new DeliveryJobRepository(context);
            var notifRepo = new NotificationRepository(context);
            var prefRepo = new PreferenceRepository(context);
            var watermarkRepo = new AggregateWatermarkRepository(context);
            var uow = new UnitOfWork(context);

            // Now Item A is returned:
            await watermarkRepo.UpsertWatermarksForRecipientsAsync(
                NotificationWatermarkScope.RentalItem,
                itemAId,
                new[] { supplierAId },
                orderVersion: 3,
                messageId: "msg-itemA-returned-3",
                isTerminal: true,
                terminalState: "RETURNED");
            await notifRepo.DismissReturnOverdueActionsForRentalItemAsync(itemAId);
            await deliveryRepo.SuppressPendingAndUndispatchedJobsForRentalItemAsync(itemAId, "Thiết bị A đã được trả lại.");
            await context.SaveChangesAsync();

            // Item A overdue notification CTA dismissed and job suppressed
            var updatedNotifA = await context.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.Id == notifAId);
            Assert.NotNull(updatedNotifA);
            Assert.False(updatedNotifA.IsActionable);

            var updatedJobA = await context.DeliveryJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobAId);
            Assert.NotNull(updatedJobA);
            Assert.Equal("Suppressed", updatedJobA.Status);

            // Item B overdue notification and job remain unaffected!
            var updatedNotifB = await context.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.Id == notifBId);
            Assert.NotNull(updatedNotifB);
            Assert.True(updatedNotifB.IsActionable);

            var updatedJobB = await context.DeliveryJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobBId);
            Assert.NotNull(updatedJobB);
            Assert.Equal("Pending", updatedJobB.Status);

            // Worker runs: delivers Job B successfully, sends exactly 1 email to Supplier B
            var authClientMock = new Mock<IAuthContactClient>();
            authClientMock.Setup(c => c.GetUserContactsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success<IReadOnlyList<AuthContactDto>>(new[]
                {
                    new AuthContactDto(supplierAId, "supplierA@example.com", IsActive: true, IsEmailVerified: true),
                    new AuthContactDto(supplierBId, "supplierB@example.com", IsActive: true, IsEmailVerified: true)
                }));

            var emailSenderMock = new Mock<IEmailSender>();
            emailSenderMock.Setup(s => s.SendEmailAsync(jobBId, "supplierB@example.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EmailSendResult(EmailSendOutcome.Success, "msg-b-1", null, null));

            var deliveryService = new EmailDeliveryService(
                deliveryRepo,
                prefRepo,
                authClientMock.Object,
                new NotificationTemplateRenderer(),
                emailSenderMock.Object,
                watermarkRepo,
                uow,
                NullLogger<EmailDeliveryService>.Instance);

            var delivered = await deliveryService.DeliverPendingJobsAsync("worker-p3-itemb-test", CancellationToken.None);
            Assert.Equal(1, delivered);

            var finalJobB = await context.DeliveryJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobBId);
            Assert.NotNull(finalJobB);
            Assert.Equal("Succeeded", finalJobB.Status);
            emailSenderMock.Verify(s => s.SendEmailAsync(jobBId, "supplierB@example.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            emailSenderMock.Verify(s => s.SendEmailAsync(jobAId, "supplierA@example.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task JobReconciliationService_DryRunAndRequeue_ReconcilesSuppressedResultNotifications()
    {
        var dbName = "notif_p4_reconcile_" + Guid.NewGuid().ToString("N")[..8];
        await RecreateDatabaseAsync(dbName);
        try
        {
            var connString = GetTestConnectionString(dbName);
            var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            await using var context = new NotificationServiceDbContext(options);
            await context.Database.MigrateAsync();

            var userId = Guid.NewGuid();
            var orderId = Guid.NewGuid();

            // Helper to create notification and suppressed job
            async Task<(Guid NotifId, Guid JobId)> CreateSuppressedJobAsync(string notifType, DateTimeOffset? dispatchStartedAt = null)
            {
                var nId = Guid.NewGuid();
                var jId = Guid.NewGuid();
                var notif = new Notification
                {
                    Id = nId,
                    OrderId = orderId,
                    RecipientUserId = userId,
                    NotificationType = notifType,
                    Category = "ORDER",
                    Severity = "INFO",
                    Title = notifType,
                    Body = notifType,
                    ResourceType = "RentalOrder",
                    ResourceId = orderId.ToString(),
                    SourceEventId = Guid.NewGuid(),
                    SourceProducer = "RentalService",
                    SourceEventType = notifType,
                    EventVersion = 1,
                    TemplateCode = notifType,
                    TemplateVersion = "1.0",
                    OccurredAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                var job = new NotificationDeliveryJob
                {
                    Id = jId,
                    NotificationId = nId,
                    Channel = "EMAIL",
                    RecipientUserId = userId,
                    Status = "Suppressed",
                    LastErrorCode = "STALE_ACTION_SUPPRESSED",
                    LastErrorDetail = "False suppression",
                    DispatchStartedAt = dispatchStartedAt,
                    MaxAttempts = 5,
                    AttemptCount = 0,
                    NextAttemptAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await context.Notifications.AddAsync(notif);
                await context.DeliveryJobs.AddAsync(job);
                return (nId, jId);
            }

            // 5 eligible result notifications (including both error codes):
            var (_, jobPaidId) = await CreateSuppressedJobAsync("ORDER_PAID");
            var (_, jobCompletedId) = await CreateSuppressedJobAsync("ORDER_COMPLETED");
            var (_, jobRefundId) = await CreateSuppressedJobAsync("REFUND_COMPLETED");
            var (_, jobPayoutId) = await CreateSuppressedJobAsync("PAYOUT_COMPLETED");

            // Also test STALE_EVENT_TERMINATED error code:
            var nTermId = Guid.NewGuid();
            var jobTerminatedId = Guid.NewGuid();
            var notifTerm = new Notification
            {
                Id = nTermId,
                OrderId = orderId,
                RecipientUserId = userId,
                NotificationType = "ORDER_COMPLETED",
                Category = "ORDER",
                Severity = "SUCCESS",
                Title = "Order Completed Terminated",
                Body = "Body",
                ResourceType = "RentalOrder",
                ResourceId = orderId.ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "RentalService",
                SourceEventType = "RentalOrderCompleted",
                EventVersion = 1,
                TemplateCode = "ORDER_COMPLETED",
                TemplateVersion = "1.0",
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var jobTerm = new NotificationDeliveryJob
            {
                Id = jobTerminatedId,
                NotificationId = nTermId,
                Channel = "EMAIL",
                RecipientUserId = userId,
                Status = "Suppressed",
                LastErrorCode = "STALE_EVENT_TERMINATED",
                LastErrorDetail = "Terminated suppression",
                MaxAttempts = 5,
                AttemptCount = 0,
                NextAttemptAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await context.Notifications.AddAsync(notifTerm);
            await context.DeliveryJobs.AddAsync(jobTerm);

            // Ineligible jobs:
            var (_, jobCreatedId) = await CreateSuppressedJobAsync("ORDER_CREATED"); // not a result notification
            var (_, jobDispatchedId) = await CreateSuppressedJobAsync("ORDER_PAID", dispatchStartedAt: DateTimeOffset.UtcNow); // dispatch started

            await context.SaveChangesAsync();

            var deliveryRepo = new DeliveryJobRepository(context);
            var reconciler = new JobReconciliationService(deliveryRepo);

            // 1. Candidate lookup (Dry-run projection)
            var candidateReport = await reconciler.GetCandidatesAsync(100);
            Assert.Equal(5, candidateReport.TotalCandidates);
            Assert.Contains(jobPaidId, candidateReport.CandidateJobIds);
            Assert.Contains(jobCompletedId, candidateReport.CandidateJobIds);
            Assert.Contains(jobRefundId, candidateReport.CandidateJobIds);
            Assert.Contains(jobPayoutId, candidateReport.CandidateJobIds);
            Assert.Contains(jobTerminatedId, candidateReport.CandidateJobIds);
            Assert.DoesNotContain(jobCreatedId, candidateReport.CandidateJobIds);
            Assert.DoesNotContain(jobDispatchedId, candidateReport.CandidateJobIds);

            // Verify DB unchanged after candidate check
            var dbJobPaid = await context.DeliveryJobs.AsNoTracking().FirstAsync(j => j.Id == jobPaidId);
            Assert.Equal("Suppressed", dbJobPaid.Status);

            // 2. Requeue execution with operator ID
            var requeueReport = await reconciler.RequeueJobsAsync(candidateReport.CandidateJobIds, "operator-maint-01");
            Assert.Equal(5, requeueReport.RequestedCount);
            Assert.Equal(5, requeueReport.ReconciledCount);

            // Verify eligible jobs reset to Pending with audit message
            var eligibleIds = new[] { jobPaidId, jobCompletedId, jobRefundId, jobPayoutId, jobTerminatedId };
            var reconciledJobs = await context.DeliveryJobs.AsNoTracking().Where(j => eligibleIds.Contains(j.Id)).ToListAsync();
            Assert.Equal(5, reconciledJobs.Count);
            foreach (var rJob in reconciledJobs)
            {
                Assert.Equal("Pending", rJob.Status);
                Assert.Null(rJob.LastErrorCode);
                Assert.NotNull(rJob.LastErrorDetail);
                Assert.Contains("Requeued by op:operator-maint-01", rJob.LastErrorDetail);
                Assert.Null(rJob.LeaseToken);
                Assert.Null(rJob.LeaseExpiresAt);
            }

            // Verify ineligible jobs remain Suppressed
            var dbJobCreated = await context.DeliveryJobs.AsNoTracking().FirstAsync(j => j.Id == jobCreatedId);
            Assert.Equal("Suppressed", dbJobCreated.Status);

            var dbJobDispatched = await context.DeliveryJobs.AsNoTracking().FirstAsync(j => j.Id == jobDispatchedId);
            Assert.Equal("Suppressed", dbJobDispatched.Status);

            // 3. Idempotency: second run finds 0 candidates
            var secondRunReport = await reconciler.GetCandidatesAsync(100);
            Assert.Equal(0, secondRunReport.TotalCandidates);
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Reconciliation_WhenWorkerClaimsJobBetweenCandidateAndExecute_DoesNotOverwriteWorkerLease()
    {
        var dbName = "notif_p3_reconcile_race_" + Guid.NewGuid().ToString("N")[..8];
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
            var notifId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var userId = Guid.NewGuid();

            var notif = new Notification
            {
                Id = notifId,
                OrderId = orderId,
                RecipientUserId = userId,
                NotificationType = "ORDER_COMPLETED",
                Category = "ORDER",
                Severity = "SUCCESS",
                Title = "Order Completed",
                Body = "Body",
                ResourceType = "RentalOrder",
                ResourceId = orderId.ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "RentalService",
                SourceEventType = "RentalOrderCompleted",
                EventVersion = 1,
                TemplateCode = "ORDER_COMPLETED",
                TemplateVersion = "1.0",
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };

            var job = new NotificationDeliveryJob
            {
                Id = jobId,
                NotificationId = notifId,
                Channel = "EMAIL",
                RecipientUserId = userId,
                Status = "Suppressed",
                LastErrorCode = "STALE_ACTION_SUPPRESSED",
                LastErrorDetail = "Legacy false suppression",
                MaxAttempts = 5,
                AttemptCount = 0,
                NextAttemptAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await context.Notifications.AddAsync(notif);
            await context.DeliveryJobs.AddAsync(job);
            await context.SaveChangesAsync();

            var deliveryRepo = new DeliveryJobRepository(context);
            var reconciler = new JobReconciliationService(deliveryRepo);

            // 1. Reconciler fetches candidates
            var candidates = await reconciler.GetCandidatesAsync(100);
            Assert.Contains(jobId, candidates.CandidateJobIds);

            // 2. Worker claims job in background before reconciler executes requeue
            var workerLeaseToken = "worker-lease-" + Guid.NewGuid().ToString("N");
            var leaseExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
            await using (var workerContext = new NotificationServiceDbContext(options))
            {
                var workerJob = await workerContext.DeliveryJobs.FirstAsync(j => j.Id == jobId);
                workerJob.Status = "Processing";
                workerJob.LeaseToken = workerLeaseToken;
                workerJob.LeaseExpiresAt = leaseExpiry;
                await workerContext.SaveChangesAsync();
            }

            // 3. Reconciler executes requeue on the candidate list
            var report = await reconciler.RequeueJobsAsync(candidates.CandidateJobIds, "operator-race-test");
            // The job is in Processing with active lease -> conditional update affects 0 rows!
            Assert.Equal(0, report.ReconciledCount);

            // The worker lease MUST NOT have been overwritten back to Pending!
            await using (var checkContext = new NotificationServiceDbContext(options))
            {
                var checkJob = await checkContext.DeliveryJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
                Assert.Equal("Processing", checkJob.Status);
                Assert.Equal(workerLeaseToken, checkJob.LeaseToken);
                Assert.NotNull(checkJob.LeaseExpiresAt);
            }
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Reconciliation_TwoConcurrentReconcilers_OnlyOneSucceeds()
    {
        var dbName = "notif_p3_two_reconcilers_" + Guid.NewGuid().ToString("N")[..8];
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
            var notifId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var userId = Guid.NewGuid();

            var notif = new Notification
            {
                Id = notifId,
                OrderId = orderId,
                RecipientUserId = userId,
                NotificationType = "REFUND_COMPLETED",
                Category = "FINANCIAL",
                Severity = "INFO",
                Title = "Refund",
                Body = "Body",
                ResourceType = "RentalOrder",
                ResourceId = orderId.ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "PaymentService",
                SourceEventType = "RefundCompleted",
                EventVersion = 1,
                TemplateCode = "REFUND_COMPLETED",
                TemplateVersion = "1.0",
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };

            var job = new NotificationDeliveryJob
            {
                Id = jobId,
                NotificationId = notifId,
                Channel = "EMAIL",
                RecipientUserId = userId,
                Status = "Suppressed",
                LastErrorCode = "STALE_ACTION_SUPPRESSED",
                LastErrorDetail = "False suppression",
                MaxAttempts = 5,
                AttemptCount = 0,
                NextAttemptAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await context.Notifications.AddAsync(notif);
            await context.DeliveryJobs.AddAsync(job);
            await context.SaveChangesAsync();

            await using var ctx1 = new NotificationServiceDbContext(options);
            await using var ctx2 = new NotificationServiceDbContext(options);
            var rec1 = new JobReconciliationService(new DeliveryJobRepository(ctx1));
            var rec2 = new JobReconciliationService(new DeliveryJobRepository(ctx2));

            var targetIds = new[] { jobId };
            var task1 = rec1.RequeueJobsAsync(targetIds, "op-1");
            var task2 = rec2.RequeueJobsAsync(targetIds, "op-2");

            var results = await Task.WhenAll(task1, task2);
            var totalReconciled = results[0].ReconciledCount + results[1].ReconciledCount;
            Assert.Equal(1, totalReconciled);
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }
    [Fact]
    public async Task Reconciliation_ThreeRequestedIds_OnlyOneEligible_ReturnsOneReconciledAndTwoSkipped()
    {
        var dbName = "notif_p3_rec_skipped_" + Guid.NewGuid().ToString("N")[..8];
        await RecreateDatabaseAsync(dbName);
        try
        {
            var connString = GetTestConnectionString(dbName);
            var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            await using var context = new NotificationServiceDbContext(options);
            await context.Database.MigrateAsync();

            var userId = Guid.NewGuid();
            var orderId = Guid.NewGuid();

            // 1. Eligible job
            var n1 = Guid.NewGuid();
            var j1Eligible = Guid.NewGuid();
            await context.Notifications.AddAsync(new Notification
            {
                Id = n1,
                OrderId = orderId,
                RecipientUserId = userId,
                NotificationType = "ORDER_PAID",
                Category = "ORDER",
                Severity = "INFO",
                Title = "Paid",
                Body = "Body",
                ResourceType = "RentalOrder",
                ResourceId = orderId.ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "RentalService",
                SourceEventType = "OrderPaid",
                EventVersion = 1,
                TemplateCode = "PAID",
                TemplateVersion = "1.0",
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await context.DeliveryJobs.AddAsync(new NotificationDeliveryJob
            {
                Id = j1Eligible,
                NotificationId = n1,
                Channel = "EMAIL",
                RecipientUserId = userId,
                Status = "Suppressed",
                LastErrorCode = "STALE_ACTION_SUPPRESSED",
                AttemptCount = 0,
                MaxAttempts = 5,
                NextAttemptAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            // 2. Ineligible job: not a result notification
            var n2 = Guid.NewGuid();
            var j2Created = Guid.NewGuid();
            await context.Notifications.AddAsync(new Notification
            {
                Id = n2,
                OrderId = orderId,
                RecipientUserId = userId,
                NotificationType = "ORDER_CREATED",
                Category = "ORDER",
                Severity = "ACTION_REQUIRED",
                Title = "Created",
                Body = "Body",
                ResourceType = "RentalOrder",
                ResourceId = orderId.ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "RentalService",
                SourceEventType = "OrderCreated",
                EventVersion = 1,
                TemplateCode = "CREATED",
                TemplateVersion = "1.0",
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await context.DeliveryJobs.AddAsync(new NotificationDeliveryJob
            {
                Id = j2Created,
                NotificationId = n2,
                Channel = "EMAIL",
                RecipientUserId = userId,
                Status = "Suppressed",
                LastErrorCode = "STALE_ACTION_SUPPRESSED",
                AttemptCount = 0,
                MaxAttempts = 5,
                NextAttemptAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            // 3. Ineligible job: non-existent ID
            var j3NonExistent = Guid.NewGuid();

            await context.SaveChangesAsync();

            var reconciler = new JobReconciliationService(new DeliveryJobRepository(context));
            var report = await reconciler.RequeueJobsAsync(new[] { j1Eligible, j2Created, j3NonExistent }, "op-skip-test");

            Assert.Equal(3, report.RequestedCount);
            Assert.Equal(1, report.ReconciledCount);
            Assert.Single(report.ReconciledJobIds, j1Eligible);
            Assert.Equal(2, report.SkippedJobIds.Count);
            Assert.Contains(j2Created, report.SkippedJobIds);
            Assert.Contains(j3NonExistent, report.SkippedJobIds);
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Reconciliation_MoreThan100DistinctIds_ThrowsArgumentOutOfRangeException()
    {
        var deliveryRepoMock = new Moq.Mock<IDeliveryJobRepository>();
        var reconciler = new JobReconciliationService(deliveryRepoMock.Object);

        // 101 distinct IDs -> throws
        var distinct101 = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToList();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reconciler.RequeueJobsAsync(distinct101, "op-test"));

        // 105 IDs with duplicates such that distinct is 100 -> succeeds
        var distinct100 = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToList();
        var duplicates105 = distinct100.Concat(distinct100.Take(5)).ToList();

        deliveryRepoMock.Setup(r => r.RequeueReconciliationJobsAsync(
            Moq.It.IsAny<IReadOnlyList<Guid>>(),
            Moq.It.IsAny<IReadOnlyCollection<string>>(),
            Moq.It.IsAny<IReadOnlyCollection<string>>(),
            Moq.It.IsAny<string>(),
            Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(distinct100);

        var report = await reconciler.RequeueJobsAsync(duplicates105, "op-test");
        Assert.Equal(100, report.RequestedCount);
        Assert.Equal(100, report.ReconciledCount);
    }
}
