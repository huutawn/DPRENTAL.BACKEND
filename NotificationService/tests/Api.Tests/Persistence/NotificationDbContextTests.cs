using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Persistence;

[Trait("Category", "Unit")]
public sealed class NotificationDbContextTests
{
    private static NotificationServiceDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new NotificationServiceDbContext(options);
    }

    [Fact]
    public void ModelBuilder_ShouldConfigureFiveEntitiesInNotificationSchema()
    {
        using var context = CreateInMemoryDbContext();
        var model = context.Model;

        var notificationEntity = model.FindEntityType(typeof(Notification));
        Assert.NotNull(notificationEntity);
        Assert.Equal("notifications", notificationEntity.GetTableName());
        Assert.Equal("notification", notificationEntity.GetSchema());

        var inboxEntity = model.FindEntityType(typeof(NotificationInboxReceipt));
        Assert.NotNull(inboxEntity);
        Assert.Equal("inbox_receipts", inboxEntity.GetTableName());
        Assert.Equal("notification", inboxEntity.GetSchema());

        var deliveryEntity = model.FindEntityType(typeof(NotificationDeliveryJob));
        Assert.NotNull(deliveryEntity);
        Assert.Equal("delivery_jobs", deliveryEntity.GetTableName());
        Assert.Equal("notification", deliveryEntity.GetSchema());

        var preferenceEntity = model.FindEntityType(typeof(NotificationPreference));
        Assert.NotNull(preferenceEntity);
        Assert.Equal("preferences", preferenceEntity.GetTableName());
        Assert.Equal("notification", preferenceEntity.GetSchema());

        var watermarkEntity = model.FindEntityType(typeof(NotificationAggregateWatermark));
        Assert.NotNull(watermarkEntity);
        Assert.Equal("aggregate_watermarks", watermarkEntity.GetTableName());
        Assert.Equal("notification", watermarkEntity.GetSchema());
        var key = watermarkEntity.FindPrimaryKey();
        Assert.NotNull(key);
        var keyPropertyNames = key.Properties.Select(p => p.Name).ToList();
        Assert.Equal(3, keyPropertyNames.Count);
        Assert.Equal("Scope", keyPropertyNames[0]);
        Assert.Equal("AggregateId", keyPropertyNames[1]);
        Assert.Equal("RecipientUserId", keyPropertyNames[2]);
    }

    [Fact]
    public async Task NotificationRepository_ShouldAddAndQueryWithPagination()
    {
        using var context = CreateInMemoryDbContext();
        var repo = new NotificationRepository(context);
        var userId = Guid.NewGuid();

        for (int i = 1; i <= 5; i++)
        {
            await repo.AddAsync(new Notification
            {
                Id = Guid.NewGuid(),
                RecipientUserId = userId,
                NotificationType = "ORDER_CREATED",
                Category = "ORDER",
                Severity = "INFO",
                Title = $"Title {i}",
                Body = $"Body {i}",
                ResourceType = "RentalOrder",
                ResourceId = Guid.NewGuid().ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "RentalService",
                SourceEventType = "RentalOrderCreated",
                TemplateCode = "ORDER_CREATED",
                TemplateVersion = "1.0",
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i)
            });
        }
        await context.SaveChangesAsync();

        var (items, total) = await repo.GetUserNotificationsPagedAsync(userId, category: null, unreadOnly: false, page: 1, pageSize: 3);
        Assert.Equal(5, total);
        Assert.Equal(3, items.Count);

        var unreadCount = await repo.GetUnreadCountAsync(userId);
        Assert.Equal(5, unreadCount);

        var markReadResult = await repo.MarkAsReadAsync(items[0].Id, userId, DateTimeOffset.UtcNow);
        Assert.True(markReadResult);

        unreadCount = await repo.GetUnreadCountAsync(userId);
        Assert.Equal(4, unreadCount);
    }

    [Fact]
    public async Task PreferenceRepository_ShouldDefaultEmailToTrue_AndAllowUpsert()
    {
        using var context = CreateInMemoryDbContext();
        var repo = new PreferenceRepository(context);
        var userId = Guid.NewGuid();

        var isEnabled = await repo.IsEmailEnabledAsync(userId, "ORDER");
        Assert.True(isEnabled);

        await repo.UpsertPreferencesAsync(userId, new[] { ("ORDER", false), ("PAYMENT", true) });
        await context.SaveChangesAsync();

        isEnabled = await repo.IsEmailEnabledAsync(userId, "ORDER");
        Assert.False(isEnabled);

        var paymentEnabled = await repo.IsEmailEnabledAsync(userId, "PAYMENT");
        Assert.True(paymentEnabled);
    }

    [Fact]
    public async Task AggregateWatermarkRepository_ShouldTrackMonotonicOrderVersion()
    {
        using var context = CreateInMemoryDbContext();
        var repo = new AggregateWatermarkRepository(context);
        var aggregateId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();

        await repo.UpsertWatermarkAsync(new NotificationAggregateWatermark
        {
            AggregateId = aggregateId,
            RecipientUserId = recipientId,
            LastOrderVersion = 2,
            LastMessageId = "msg-2",
            IsTerminal = false
        });
        await context.SaveChangesAsync();

        var watermark = await repo.GetWatermarkAsync(NotificationWatermarkScope.RentalOrder, aggregateId, recipientId);
        Assert.NotNull(watermark);
        Assert.Equal(2, watermark.LastOrderVersion);

        // Lower version should not regress watermark
        await repo.UpsertWatermarkAsync(new NotificationAggregateWatermark
        {
            AggregateId = aggregateId,
            RecipientUserId = recipientId,
            LastOrderVersion = 1,
            LastMessageId = "msg-1",
            IsTerminal = false
        });
        await context.SaveChangesAsync();

        watermark = await repo.GetWatermarkAsync(NotificationWatermarkScope.RentalOrder, aggregateId, recipientId);
        Assert.NotNull(watermark);
        Assert.Equal(2, watermark.LastOrderVersion);

        // Terminal state should set terminal
        await repo.UpsertWatermarkAsync(new NotificationAggregateWatermark
        {
            AggregateId = aggregateId,
            RecipientUserId = recipientId,
            LastOrderVersion = 3,
            LastMessageId = "msg-3",
            IsTerminal = true,
            TerminalState = "COMPLETED"
        });
        await context.SaveChangesAsync();

        watermark = await repo.GetWatermarkAsync(NotificationWatermarkScope.RentalOrder, aggregateId, recipientId);
        Assert.NotNull(watermark);
        Assert.Equal(3, watermark.LastOrderVersion);
        Assert.True(watermark.IsTerminal);
        Assert.Equal("COMPLETED", watermark.TerminalState);
    }

    [Fact]
    public async Task AggregateWatermarkRepository_OlderTerminalEvent_CannotOverwriteNewerTerminalState()
    {
        using var context = CreateInMemoryDbContext();
        var repo = new AggregateWatermarkRepository(context);
        var aggregateId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();

        // Terminal state at version 3: COMPLETED
        await repo.UpsertWatermarkAsync(new NotificationAggregateWatermark
        {
            AggregateId = aggregateId,
            RecipientUserId = recipientId,
            LastOrderVersion = 3,
            LastMessageId = "msg-completed-3",
            IsTerminal = true,
            TerminalState = "COMPLETED"
        });
        await context.SaveChangesAsync();

        // Older event at version 1 with terminal state CANCELLED arrives late
        await repo.UpsertWatermarkAsync(new NotificationAggregateWatermark
        {
            AggregateId = aggregateId,
            RecipientUserId = recipientId,
            LastOrderVersion = 1,
            LastMessageId = "msg-cancelled-1",
            IsTerminal = true,
            TerminalState = "CANCELLED"
        });
        await context.SaveChangesAsync();

        var watermark = await repo.GetWatermarkAsync(NotificationWatermarkScope.RentalOrder, aggregateId, recipientId);
        Assert.NotNull(watermark);
        Assert.Equal(3, watermark.LastOrderVersion);
        Assert.Equal("msg-completed-3", watermark.LastMessageId);
        Assert.True(watermark.IsTerminal);
        Assert.Equal("COMPLETED", watermark.TerminalState);
    }

    [Fact]
    public async Task AggregateWatermarkRepository_ScopedWatermarks_ShouldIsolateDifferentScopes()
    {
        using var context = CreateInMemoryDbContext();
        var repo = new AggregateWatermarkRepository(context);
        var aggregateId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();

        // RentalOrder watermark
        await repo.UpsertWatermarksForRecipientsAsync(
            NotificationWatermarkScope.RentalOrder,
            aggregateId,
            new[] { recipientId },
            orderVersion: 5,
            messageId: "order-msg-5",
            isTerminal: true,
            terminalState: "COMPLETED");

        // RentalItem watermark for the same aggregateId and recipient
        await repo.UpsertWatermarksForRecipientsAsync(
            NotificationWatermarkScope.RentalItem,
            aggregateId,
            new[] { recipientId },
            orderVersion: 2,
            messageId: "item-msg-2",
            isTerminal: false,
            terminalState: null);

        await context.SaveChangesAsync();

        var orderWatermark = await repo.GetWatermarkAsync(NotificationWatermarkScope.RentalOrder, aggregateId, recipientId);
        var itemWatermark = await repo.GetWatermarkAsync(NotificationWatermarkScope.RentalItem, aggregateId, recipientId);

        Assert.NotNull(orderWatermark);
        Assert.Equal(NotificationWatermarkScope.RentalOrder, orderWatermark.Scope);
        Assert.Equal(5, orderWatermark.LastOrderVersion);
        Assert.True(orderWatermark.IsTerminal);

        Assert.NotNull(itemWatermark);
        Assert.Equal(NotificationWatermarkScope.RentalItem, itemWatermark.Scope);
        Assert.Equal(2, itemWatermark.LastOrderVersion);
        Assert.False(itemWatermark.IsTerminal);
    }

    [Fact]
    public async Task AggregateWatermarkRepository_AcquireAdvisoryLock_InMemoryFallback_ShouldCompleteWithoutError()
    {
        using var context = CreateInMemoryDbContext();
        var repo = new AggregateWatermarkRepository(context);
        var aggregateId = Guid.NewGuid();

        var exception = await Record.ExceptionAsync(() => repo.AcquireAdvisoryLockAsync(aggregateId));
        Assert.Null(exception);
    }

    [Fact]
    public async Task AggregateWatermarkRepository_GetAggregateStateAsync_ReturnsCorrectMaxVersionIsPaidIsTerminalAcrossMultipleRecipients()
    {
        using var context = CreateInMemoryDbContext();
        var repo = new AggregateWatermarkRepository(context);
        var aggregateId = Guid.NewGuid();
        var recipient1 = Guid.NewGuid();
        var recipient2 = Guid.NewGuid();
        var recipient3 = Guid.NewGuid();

        // Recipient 1: Version 2, Paid
        await repo.UpsertWatermarksForRecipientsAsync(
            NotificationWatermarkScope.RentalOrder,
            aggregateId,
            new[] { recipient1 },
            orderVersion: 2,
            messageId: "msg-r1",
            isTerminal: false,
            terminalState: null,
            isPaid: true);

        // Recipient 2: Version 5, Completed (Terminal)
        await repo.UpsertWatermarksForRecipientsAsync(
            NotificationWatermarkScope.RentalOrder,
            aggregateId,
            new[] { recipient2 },
            orderVersion: 5,
            messageId: "msg-r2",
            isTerminal: true,
            terminalState: "COMPLETED",
            isPaid: false);

        // Recipient 3: Version 3, Active
        await repo.UpsertWatermarksForRecipientsAsync(
            NotificationWatermarkScope.RentalOrder,
            aggregateId,
            new[] { recipient3 },
            orderVersion: 3,
            messageId: "msg-r3",
            isTerminal: false,
            terminalState: null,
            isPaid: false);

        await context.SaveChangesAsync();

        var state = await repo.GetAggregateStateAsync(NotificationWatermarkScope.RentalOrder, aggregateId);

        Assert.NotNull(state);
        Assert.Equal(NotificationWatermarkScope.RentalOrder, state.Scope);
        Assert.Equal(aggregateId, state.AggregateId);
        Assert.Equal(5, state.MaxVersion);
        Assert.True(state.IsPaid);
        Assert.True(state.IsTerminal);
        Assert.Equal("COMPLETED", state.TerminalState);
        Assert.Equal(5, state.TerminalVersion);
    }

    [Fact]
    public async Task AggregateWatermarkRepository_GetAggregateStateAsync_ConflictingTerminalStatesAtSameVersion_ThrowsInvalidOperationException()
    {
        using var context = CreateInMemoryDbContext();
        var repo = new AggregateWatermarkRepository(context);
        var aggregateId = Guid.NewGuid();
        var recipient1 = Guid.NewGuid();
        var recipient2 = Guid.NewGuid();

        // Recipient 1: Version 4, CANCELLED
        await repo.UpsertWatermarksForRecipientsAsync(
            NotificationWatermarkScope.RentalOrder,
            aggregateId,
            new[] { recipient1 },
            orderVersion: 4,
            messageId: "msg-r1",
            isTerminal: true,
            terminalState: "CANCELLED");

        // Recipient 2: Version 4, COMPLETED (Conflict!)
        await repo.UpsertWatermarksForRecipientsAsync(
            NotificationWatermarkScope.RentalOrder,
            aggregateId,
            new[] { recipient2 },
            orderVersion: 4,
            messageId: "msg-r2",
            isTerminal: true,
            terminalState: "COMPLETED");

        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repo.GetAggregateStateAsync(NotificationWatermarkScope.RentalOrder, aggregateId));

        Assert.Contains("Data integrity error", ex.Message);
        Assert.Contains("Conflicting terminal states", ex.Message);
    }

    [Fact]
    public async Task AggregateWatermarkRepository_GetAggregateStateAsync_ReturnsNullWhenAggregateDoesNotExist()
    {
        using var context = CreateInMemoryDbContext();
        var repo = new AggregateWatermarkRepository(context);

        var state = await repo.GetAggregateStateAsync(NotificationWatermarkScope.RentalOrder, Guid.NewGuid());
        Assert.Null(state);
    }

    [Fact]
    public async Task AggregateWatermarkRepository_GetAggregateStateAsync_PicksTerminalStateFromHighestVersionWhenNoConflict()
    {
        using var context = CreateInMemoryDbContext();
        var repo = new AggregateWatermarkRepository(context);
        var aggregateId = Guid.NewGuid();
        var recipient1 = Guid.NewGuid();
        var recipient2 = Guid.NewGuid();

        // Recipient 1: Version 2, CANCELLED
        await repo.UpsertWatermarksForRecipientsAsync(
            NotificationWatermarkScope.RentalOrder,
            aggregateId,
            new[] { recipient1 },
            orderVersion: 2,
            messageId: "msg-r1",
            isTerminal: true,
            terminalState: "CANCELLED");

        // Recipient 2: Version 4, COMPLETED
        await repo.UpsertWatermarksForRecipientsAsync(
            NotificationWatermarkScope.RentalOrder,
            aggregateId,
            new[] { recipient2 },
            orderVersion: 4,
            messageId: "msg-r2",
            isTerminal: true,
            terminalState: "COMPLETED");

        await context.SaveChangesAsync();

        var state = await repo.GetAggregateStateAsync(NotificationWatermarkScope.RentalOrder, aggregateId);
        Assert.NotNull(state);
        Assert.Equal(4, state.MaxVersion);
        Assert.True(state.IsTerminal);
        Assert.Equal("COMPLETED", state.TerminalState);
        Assert.Equal(4, state.TerminalVersion);
    }

    [Fact]
    public async Task InboxReceiptRepository_ShouldTrackIdempotency()
    {
        using var context = CreateInMemoryDbContext();
        var repo = new InboxReceiptRepository(context);
        var messageId = Guid.NewGuid();

        var exists = await repo.ExistsAsync("notification-service-v1", messageId);
        Assert.False(exists);

        await repo.AddAsync(new NotificationInboxReceipt
        {
            Id = Guid.NewGuid(),
            ConsumerName = "notification-service-v1",
            MessageId = messageId,
            Producer = "RentalService",
            MessageType = "RentalOrderPaid",
            Status = "Processed",
            ProcessedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();

        exists = await repo.ExistsAsync("notification-service-v1", messageId);
        Assert.True(exists);
    }

    [Fact]
    public async Task DeliveryJobRepository_ShouldClaimAndRecoverExpiredLeases()
    {
        using var context = CreateInMemoryDbContext();
        var repo = new DeliveryJobRepository(context);
        var notificationId = Guid.NewGuid();

        // Add notification first to satisfy foreign key
        context.Notifications.Add(new Notification
        {
            Id = notificationId,
            RecipientUserId = Guid.NewGuid(),
            NotificationType = "ORDER_PAID",
            Category = "ORDER",
            Severity = "INFO",
            Title = "Paid",
            Body = "Order Paid",
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
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await repo.AddAsync(job);
        await context.SaveChangesAsync();

        var claimed = await repo.ClaimPendingJobsAsync("worker-1", TimeSpan.FromMinutes(5), batchSize: 10);
        Assert.Single(claimed);
        Assert.Equal("Processing", claimed[0].Status);
        Assert.NotNull(claimed[0].LeaseToken);

        // Simulate expired lease
        claimed[0].LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await context.SaveChangesAsync();

        var recovered = await repo.RecoverExpiredLeasesAsync();
        Assert.Equal(1, recovered);

        var jobAfterRecovery = await context.DeliveryJobs.FindAsync(job.Id);
        Assert.NotNull(jobAfterRecovery);
        Assert.Equal("Pending", jobAfterRecovery.Status);
        Assert.Null(jobAfterRecovery.LeaseToken);
    }

    [Fact]
    public async Task DeliveryJobRepository_RecoverExpiredLeases_WhenDispatchStartedNotNull_ShouldTransitionToUncertain()
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
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await repo.AddAsync(job);
        await context.SaveChangesAsync();

        var claimed = await repo.ClaimPendingJobsAsync("worker-1", TimeSpan.FromMinutes(5), batchSize: 10);
        Assert.Single(claimed);
        Assert.Equal("Processing", claimed[0].Status);

        // Simulate dispatch started, then worker crashed and lease expired
        claimed[0].DispatchStartedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        claimed[0].LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await context.SaveChangesAsync();

        var recovered = await repo.RecoverExpiredLeasesAsync();
        Assert.Equal(1, recovered);

        var jobAfterRecovery = await context.DeliveryJobs.FindAsync(job.Id);
        Assert.NotNull(jobAfterRecovery);
        Assert.Equal("Uncertain", jobAfterRecovery.Status);
        Assert.Null(jobAfterRecovery.LeaseToken);
        Assert.Null(jobAfterRecovery.LeaseExpiresAt);
    }
}
