using Dprental.NotificationService.Api.Tests.Fixtures;
using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class PostgreSqlMigrationAndPersistenceIntegrationTests
{
    private static string GetTestConnectionString(string dbName) => TestEnvironment.GetPostgreSqlConnectionString(dbName);
    private static Task RecreateDatabaseAsync(string dbName) => TestEnvironment.RecreateDatabaseAsync(dbName);

    [Fact]
    public async Task EmptyDatabase_MigrateAsync_AppliesAllMigrationsSuccessfully()
    {
        var dbName = "notif_migr_test_clean_" + Guid.NewGuid().ToString("N")[..8];
        await RecreateDatabaseAsync(dbName);
        try
        {
            var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
                .UseNpgsql(GetTestConnectionString(dbName))
                .Options;

            await using (var context = new NotificationServiceDbContext(options))
            {
                await context.Database.MigrateAsync();
            }

            // Verify tables exist in schema 'notification' using separate connection
            await using var conn = new NpgsqlConnection(GetTestConnectionString(dbName));
            await conn.OpenAsync();

            await using (var cmd = new NpgsqlCommand(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'notification' ORDER BY table_name;",
                conn))
            {
                var tables = new List<string>();
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }

                Assert.Contains("aggregate_watermarks", tables);
                Assert.Contains("delivery_jobs", tables);
                Assert.Contains("inbox_receipts", tables);
                Assert.Contains("notifications", tables);
                Assert.Contains("preferences", tables);
            }

            // Verify partial index on delivery_jobs exists and works
            await using (var indexCmd = new NpgsqlCommand(
                "SELECT indexname FROM pg_indexes WHERE schemaname = 'notification' AND tablename = 'delivery_jobs';",
                conn))
            {
                var indexes = new List<string>();
                await using var indexReader = await indexCmd.ExecuteReaderAsync();
                while (await indexReader.ReadAsync())
                {
                    indexes.Add(indexReader.GetString(0));
                }

                Assert.Contains("ix_delivery_jobs_pending_claim", indexes);
                Assert.Contains("ix_delivery_jobs_lease_recovery", indexes);
            }

            // Verify new index on notifications.rental_item_id exists
            await using (var notifIdxCmd = new NpgsqlCommand(
                "SELECT indexname FROM pg_indexes WHERE schemaname = 'notification' AND tablename = 'notifications';",
                conn))
            {
                var indexes = new List<string>();
                await using var indexReader = await notifIdxCmd.ExecuteReaderAsync();
                while (await indexReader.ReadAsync())
                {
                    indexes.Add(indexReader.GetString(0));
                }
                Assert.Contains("ix_notifications_rental_item", indexes);
            }

            // Verify new index on aggregate_watermarks.parent_item_id exists
            await using (var wmIdxCmd = new NpgsqlCommand(
                "SELECT indexname FROM pg_indexes WHERE schemaname = 'notification' AND tablename = 'aggregate_watermarks';",
                conn))
            {
                var indexes = new List<string>();
                await using var indexReader = await wmIdxCmd.ExecuteReaderAsync();
                while (await indexReader.ReadAsync())
                {
                    indexes.Add(indexReader.GetString(0));
                }
                Assert.Contains("ix_aggregate_watermarks_parent_item", indexes);
            }
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task BaselineDatabase_UpgradeMigration_PreservesDataAndAppliesBackfills()
    {
        var dbName = "notif_migr_test_upgrade_" + Guid.NewGuid().ToString("N")[..8];
        await RecreateDatabaseAsync(dbName);
        try
        {
            var connString = GetTestConnectionString(dbName);
            var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            // 1. Migrate only to initial migration
            await using (var context = new NotificationServiceDbContext(options))
            {
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync("20261102082253_InitialNotificationSchema");
            }

            // 2. Insert baseline test rows using raw SQL to match initial schema
            var orderId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var notifId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var receiptId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(connString))
            {
                await conn.OpenAsync();

                // Insert watermark without scope or is_paid
                var insertWatermarkSql = @"
                    INSERT INTO notification.aggregate_watermarks
                    (""AggregateId"", ""RecipientUserId"", ""LastOrderVersion"", ""IsTerminal"", ""TerminalState"", ""UpdatedAt"")
                    VALUES (@aggId, @userId, 3, true, 'PAID', NOW());";
                await using (var cmd = new NpgsqlCommand(insertWatermarkSql, conn))
                {
                    cmd.Parameters.AddWithValue("aggId", orderId);
                    cmd.Parameters.AddWithValue("userId", userId);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Insert notification
                var insertNotifSql = @"
                    INSERT INTO notification.notifications
                    (""Id"", ""RecipientUserId"", ""NotificationType"", ""Category"", ""Severity"", ""Title"", ""Body"",
                     ""ResourceType"", ""ResourceId"", ""SourceEventId"", ""SourceProducer"", ""SourceEventType"", ""EventVersion"",
                     ""TemplateCode"", ""TemplateVersion"", ""OccurredAt"", ""CreatedAt"")
                    VALUES (@id, @userId, 'RENTAL_ORDER_PAID', 'Order', 'Info', 'Paid', 'Your order was paid',
                            'RentalOrder', @resourceId, @sourceEventId, 'RentalService', 'RentalOrderPaid', 3,
                            'RENTAL_ORDER_PAID_TEMPLATE', 'v1', NOW(), NOW());";
                await using (var cmd = new NpgsqlCommand(insertNotifSql, conn))
                {
                    cmd.Parameters.AddWithValue("id", notifId);
                    cmd.Parameters.AddWithValue("userId", userId);
                    cmd.Parameters.AddWithValue("resourceId", orderId.ToString());
                    cmd.Parameters.AddWithValue("sourceEventId", Guid.NewGuid());
                    await cmd.ExecuteNonQueryAsync();
                }

                // Insert delivery job
                var insertJobSql = @"
                    INSERT INTO notification.delivery_jobs
                    (""Id"", ""NotificationId"", ""Channel"", ""RecipientUserId"", ""Status"", ""AttemptCount"", ""MaxAttempts"", ""NextAttemptAt"", ""CreatedAt"", ""UpdatedAt"")
                    VALUES (@id, @notifId, 'EMAIL', @userId, 'Pending', 0, 5, NOW(), NOW(), NOW());";
                await using (var cmd = new NpgsqlCommand(insertJobSql, conn))
                {
                    cmd.Parameters.AddWithValue("id", jobId);
                    cmd.Parameters.AddWithValue("notifId", notifId);
                    cmd.Parameters.AddWithValue("userId", userId);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Insert inbox receipt
                var insertReceiptSql = @"
                    INSERT INTO notification.inbox_receipts
                    (""Id"", ""ConsumerName"", ""MessageId"", ""Producer"", ""MessageType"", ""Status"", ""ProcessedAt"")
                    VALUES (@id, 'RentalOrderConsumer', @msgId, 'RentalService', 'RentalOrderPaid', 'Processed', NOW());";
                await using (var cmd = new NpgsqlCommand(insertReceiptSql, conn))
                {
                    cmd.Parameters.AddWithValue("id", receiptId);
                    cmd.Parameters.AddWithValue("msgId", Guid.NewGuid());
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            // 3. Apply the upgrade migration
            await using (var context = new NotificationServiceDbContext(options))
            {
                await context.Database.MigrateAsync();
            }

            // 4. Verify data preservation, column renames, and backfills
            await using (var context = new NotificationServiceDbContext(options))
            {
                var notif = await context.Notifications.FirstOrDefaultAsync(n => n.Id == notifId);
                Assert.NotNull(notif);
                Assert.Equal("RENTAL_ORDER_PAID", notif.NotificationType);
                Assert.Equal(orderId, notif.OrderId); // Backfilled from resourceId!

                var job = await context.DeliveryJobs.FirstOrDefaultAsync(j => j.Id == jobId);
                Assert.NotNull(job);
                Assert.Equal("Pending", job.Status);
                Assert.Equal(notifId, job.NotificationId);

                var receipt = await context.InboxReceipts.FirstOrDefaultAsync(r => r.Id == receiptId);
                Assert.NotNull(receipt);
                Assert.Equal("Processed", receipt.Status);

                var watermark = await context.AggregateWatermarks.FirstOrDefaultAsync(w => w.AggregateId == orderId && w.RecipientUserId == userId);
                Assert.NotNull(watermark);
                Assert.Equal("RentalOrder", watermark.Scope); // Backfilled default!
                Assert.True(watermark.IsPaid); // Backfilled from terminal state PAID / RENTAL_ORDER_PAID!
                Assert.False(watermark.IsTerminal); // Corrected from PAID terminal state!
                Assert.Null(watermark.TerminalState);
                Assert.Equal(3, watermark.LastOrderVersion);
            }
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task DatabaseAtF224363_UpgradeMigration_FixesPaidWatermarks_AndBackfillsLinkage()
    {
        var dbName = "notif_migr_test_f224363_" + Guid.NewGuid().ToString("N")[..8];
        await RecreateDatabaseAsync(dbName);
        try
        {
            var connString = GetTestConnectionString(dbName);
            var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            // 1. Migrate only up to f224363 (AddNotificationSchemaUpgrades)
            await using (var context = new NotificationServiceDbContext(options))
            {
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync("20261104042342_AddNotificationSchemaUpgrades");
            }

            // 2. Insert baseline test rows with raw SQL matching f224363 schema
            var orderPaidId = Guid.NewGuid();
            var orderCancelledId = Guid.NewGuid();
            var orderCompletedId = Guid.NewGuid();
            var returnRequestId = Guid.NewGuid();
            var rentalItemId = Guid.NewGuid();
            var userId = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(connString))
            {
                await conn.OpenAsync();

                // Watermark with terminal_state = 'PAID', is_terminal = true
                var insertPaidWm = @"
                    INSERT INTO notification.aggregate_watermarks
                    (scope, aggregate_id, recipient_user_id, last_order_version, is_terminal, terminal_state, is_paid, updated_at)
                    VALUES ('RentalOrder', @aggId, @userId, 2, true, 'PAID', false, NOW());";
                await using (var cmd = new NpgsqlCommand(insertPaidWm, conn))
                {
                    cmd.Parameters.AddWithValue("aggId", orderPaidId);
                    cmd.Parameters.AddWithValue("userId", userId);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Watermark with terminal_state = 'CANCELLED', is_terminal = true
                var insertCancelledWm = @"
                    INSERT INTO notification.aggregate_watermarks
                    (scope, aggregate_id, recipient_user_id, last_order_version, is_terminal, terminal_state, is_paid, updated_at)
                    VALUES ('RentalOrder', @aggId, @userId, 3, true, 'CANCELLED', false, NOW());";
                await using (var cmd = new NpgsqlCommand(insertCancelledWm, conn))
                {
                    cmd.Parameters.AddWithValue("aggId", orderCancelledId);
                    cmd.Parameters.AddWithValue("userId", userId);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Watermark with terminal_state = 'COMPLETED', is_terminal = true
                var insertCompletedWm = @"
                    INSERT INTO notification.aggregate_watermarks
                    (scope, aggregate_id, recipient_user_id, last_order_version, is_terminal, terminal_state, is_paid, updated_at)
                    VALUES ('RentalOrder', @aggId, @userId, 4, true, 'COMPLETED', true, NOW());";
                await using (var cmd = new NpgsqlCommand(insertCompletedWm, conn))
                {
                    cmd.Parameters.AddWithValue("aggId", orderCompletedId);
                    cmd.Parameters.AddWithValue("userId", userId);
                    await cmd.ExecuteNonQueryAsync();
                }

                // ReturnRequest watermark without parent_item_id
                var insertReturnWm = @"
                    INSERT INTO notification.aggregate_watermarks
                    (scope, aggregate_id, recipient_user_id, parent_order_id, last_order_version, is_terminal, terminal_state, is_paid, updated_at)
                    VALUES ('ReturnRequest', @aggId, @userId, @orderId, 1, false, NULL, false, NOW());";
                await using (var cmd = new NpgsqlCommand(insertReturnWm, conn))
                {
                    cmd.Parameters.AddWithValue("aggId", returnRequestId);
                    cmd.Parameters.AddWithValue("userId", userId);
                    cmd.Parameters.AddWithValue("orderId", orderPaidId);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Notification with ORDER_PAID
                var notifPaidId = Guid.NewGuid();
                var insertNotifPaid = @"
                    INSERT INTO notification.notifications
                    (id, recipient_user_id, order_id, notification_type, category, severity, title, body,
                     resource_type, resource_id, source_event_id, source_producer, source_event_type, event_version,
                     template_code, template_version, occurred_at, created_at)
                    VALUES (@id, @userId, @orderId, 'ORDER_PAID', 'Order', 'Info', 'Paid', 'Order paid',
                            'RentalOrder', @orderIdStr, @sourceId, 'RentalService', 'RentalOrderPaid', 2,
                            'RENTAL_ORDER_PAID', '1.0', NOW(), NOW());";
                await using (var cmd = new NpgsqlCommand(insertNotifPaid, conn))
                {
                    cmd.Parameters.AddWithValue("id", notifPaidId);
                    cmd.Parameters.AddWithValue("userId", userId);
                    cmd.Parameters.AddWithValue("orderId", orderPaidId);
                    cmd.Parameters.AddWithValue("orderIdStr", orderPaidId.ToString());
                    cmd.Parameters.AddWithValue("sourceId", Guid.NewGuid());
                    await cmd.ExecuteNonQueryAsync();
                }

                // Notification with ReturnRequest and render_data_json containing rentalItemId
                var notifReturnId = Guid.NewGuid();
                var insertNotifReturn = @"
                    INSERT INTO notification.notifications
                    (id, recipient_user_id, order_id, notification_type, category, severity, title, body,
                     resource_type, resource_id, source_event_id, source_producer, source_event_type, event_version,
                     template_code, template_version, render_data_json, is_actionable, occurred_at, created_at)
                    VALUES (@id, @userId, @orderId, 'RETURN_CONFIRMATION_OVERDUE_SUPPLIER', 'RETURN', 'ACTION_REQUIRED', 'Overdue', 'Return overdue',
                            'ReturnRequest', @resId, @sourceId, 'RentalService', 'ReturnConfirmationOverdue', 2,
                            'RETURN_CONFIRMATION_OVERDUE_SUPPLIER', '1.0', @renderData, true, NOW(), NOW());";
                await using (var cmd = new NpgsqlCommand(insertNotifReturn, conn))
                {
                    cmd.Parameters.AddWithValue("id", notifReturnId);
                    cmd.Parameters.AddWithValue("userId", userId);
                    cmd.Parameters.AddWithValue("orderId", orderPaidId);
                    cmd.Parameters.AddWithValue("resId", returnRequestId.ToString());
                    cmd.Parameters.AddWithValue("sourceId", Guid.NewGuid());
                    cmd.Parameters.AddWithValue("renderData", $"{{\"rentalOrderId\":\"{orderPaidId}\",\"rentalItemId\":\"{rentalItemId}\",\"returnRequestId\":\"{returnRequestId}\"}}");
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            // 3. Migrate to latest (AddLinkageAndCorrectPaidMilestones)
            await using (var context = new NotificationServiceDbContext(options))
            {
                await context.Database.MigrateAsync();
            }

            // 4. Verify post-upgrade state
            await using (var context = new NotificationServiceDbContext(options))
            {
                // PAID watermark fixed
                var paidWm = await context.AggregateWatermarks.FirstOrDefaultAsync(w => w.Scope == "RentalOrder" && w.AggregateId == orderPaidId && w.RecipientUserId == userId);
                Assert.NotNull(paidWm);
                Assert.True(paidWm.IsPaid, "PAID watermark must have is_paid = true");
                Assert.False(paidWm.IsTerminal, "PAID watermark must NOT be terminal");
                Assert.Null(paidWm.TerminalState);

                // CANCELLED watermark preserved
                var cancelWm = await context.AggregateWatermarks.FirstOrDefaultAsync(w => w.Scope == "RentalOrder" && w.AggregateId == orderCancelledId && w.RecipientUserId == userId);
                Assert.NotNull(cancelWm);
                Assert.True(cancelWm.IsTerminal, "CANCELLED order must remain terminal");
                Assert.Equal("CANCELLED", cancelWm.TerminalState);

                // COMPLETED watermark preserved
                var compWm = await context.AggregateWatermarks.FirstOrDefaultAsync(w => w.Scope == "RentalOrder" && w.AggregateId == orderCompletedId && w.RecipientUserId == userId);
                Assert.NotNull(compWm);
                Assert.True(compWm.IsTerminal, "COMPLETED order must remain terminal");
                Assert.Equal("COMPLETED", compWm.TerminalState);

                // ReturnRequest watermark backfilled with parent_item_id
                var returnWm = await context.AggregateWatermarks.FirstOrDefaultAsync(w => w.Scope == "ReturnRequest" && w.AggregateId == returnRequestId && w.RecipientUserId == userId);
                Assert.NotNull(returnWm);
                Assert.Equal(rentalItemId, returnWm.ParentItemId);

                // Notification backfilled with rental_item_id
                var returnNotif = await context.Notifications.FirstOrDefaultAsync(n => n.ResourceType == "ReturnRequest" && n.ResourceId == returnRequestId.ToString());
                Assert.NotNull(returnNotif);
                Assert.Equal(rentalItemId, returnNotif.RentalItemId);
            }
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }
    [Fact]
    public async Task PostgreSql_DeliveryJobRepository_ClaimPendingJobs_WithSkipLocked_ClaimsExclusively()
    {
        var dbName = "notif_repo_test_claim_" + Guid.NewGuid().ToString("N")[..8];
        await RecreateDatabaseAsync(dbName);
        try
        {
            var connString = GetTestConnectionString(dbName);
            var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            await using var context = new NotificationServiceDbContext(options);
            await context.Database.MigrateAsync();

            var notifId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var notif = new Notification
            {
                Id = notifId,
                RecipientUserId = userId,
                NotificationType = "RENTAL_ORDER_CREATED",
                Category = "Order",
                Severity = "Info",
                Title = "Order Created",
                Body = "Order created",
                ResourceType = "RentalOrder",
                ResourceId = Guid.NewGuid().ToString(),
                SourceEventId = Guid.NewGuid(),
                SourceProducer = "RentalService",
                SourceEventType = "RentalOrderCreated",
                EventVersion = 1,
                TemplateCode = "RENTAL_ORDER_CREATED_TEMPLATE",
                TemplateVersion = "v1",
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await context.Notifications.AddAsync(notif);

            var job = new NotificationDeliveryJob
            {
                Id = Guid.NewGuid(),
                NotificationId = notifId,
                Channel = "EMAIL",
                RecipientUserId = userId,
                Status = "Pending",
                AttemptCount = 0,
                MaxAttempts = 5,
                NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await context.DeliveryJobs.AddAsync(job);
            await context.SaveChangesAsync();

            var repo = new DeliveryJobRepository(context);
            var claimed = await repo.ClaimPendingJobsAsync("worker-1", TimeSpan.FromMinutes(5), 10);

            Assert.Single(claimed);
            Assert.Equal(job.Id, claimed[0].Id);
            Assert.Equal("Processing", claimed[0].Status);
            Assert.StartsWith("worker-1_", claimed[0].LeaseToken);
            Assert.NotNull(claimed[0].LeaseExpiresAt);

            // Second claim should return empty due to status = 'Processing'
            var secondClaim = await repo.ClaimPendingJobsAsync("worker-2", TimeSpan.FromMinutes(5), 10);
            Assert.Empty(secondClaim);
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task PostgreSql_ScopedWatermarks_TwoItemsInSameOrder_CreateIndependentWatermarks()
    {
        var dbName = "notif_repo_test_items_" + Guid.NewGuid().ToString("N")[..8];
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
            var itemA = Guid.NewGuid();
            var itemB = Guid.NewGuid();
            var renterId = Guid.NewGuid();

            var repo = new AggregateWatermarkRepository(context);

            // Upsert Item A: version 3, terminal COMPLETED
            await repo.UpsertWatermarksForRecipientsAsync(
                NotificationWatermarkScope.RentalItem,
                itemA,
                new[] { renterId },
                orderVersion: 3,
                messageId: "msg-itemA-3",
                isTerminal: true,
                terminalState: "COMPLETED",
                parentOrderId: orderId);

            // Upsert Item B: version 2, active (not terminal)
            await repo.UpsertWatermarksForRecipientsAsync(
                NotificationWatermarkScope.RentalItem,
                itemB,
                new[] { renterId },
                orderVersion: 2,
                messageId: "msg-itemB-2",
                isTerminal: false,
                terminalState: null,
                parentOrderId: orderId);

            await context.SaveChangesAsync();

            // Read watermarks
            var wmItemA = await repo.GetWatermarkAsync(NotificationWatermarkScope.RentalItem, itemA, renterId);
            var wmItemB = await repo.GetWatermarkAsync(NotificationWatermarkScope.RentalItem, itemB, renterId);

            Assert.NotNull(wmItemA);
            Assert.True(wmItemA.IsTerminal);
            Assert.Equal("COMPLETED", wmItemA.TerminalState);
            Assert.Equal(orderId, wmItemA.ParentOrderId);

            Assert.NotNull(wmItemB);
            Assert.False(wmItemB.IsTerminal);
            Assert.Null(wmItemB.TerminalState);
            Assert.Equal(orderId, wmItemB.ParentOrderId);

            // Terminal Item A does NOT make Item B stale!
            var evalB = Dprental.NotificationService.Application.Policies.StaleActionSuppressionPolicy.Evaluate("ORDER_CREATED", 2, wmItemB);
            Assert.False(evalB.IsStale);
            Assert.False(evalB.SuppressAction);
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task PostgreSql_AdvisoryLock_SerializesSameOrder_AndReleasesOnRollback()
    {
        var dbName = "notif_lock_test_" + Guid.NewGuid().ToString("N")[..8];
        await RecreateDatabaseAsync(dbName);
        try
        {
            var connString = GetTestConnectionString(dbName);
            var orderId1 = Guid.NewGuid();
            var orderId2 = Guid.NewGuid();

            await using var conn1 = new NpgsqlConnection(connString);
            await conn1.OpenAsync();
            await using var tx1 = await conn1.BeginTransactionAsync();

            // Tx 1 acquires advisory lock on orderId1
            await using (var cmd1 = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@orderId::text));", conn1, tx1))
            {
                cmd1.Parameters.AddWithValue("orderId", orderId1.ToString());
                await cmd1.ExecuteNonQueryAsync();
            }

            // Tx 2 on different orderId2 acquires lock immediately without blocking
            await using var conn2 = new NpgsqlConnection(connString);
            await conn2.OpenAsync();
            await using var tx2 = await conn2.BeginTransactionAsync();
            await using (var cmd2 = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@orderId::text));", conn2, tx2))
            {
                cmd2.Parameters.AddWithValue("orderId", orderId2.ToString());
                await cmd2.ExecuteNonQueryAsync();
            }
            await tx2.CommitAsync();

            // Tx 3 on same orderId1 attempts non-blocking try_lock: must return false (currently locked by Tx 1)
            await using var conn3 = new NpgsqlConnection(connString);
            await conn3.OpenAsync();
            await using var tx3 = await conn3.BeginTransactionAsync();
            bool lockAcquiredWhileTx1Active;
            await using (var cmd3 = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(hashtext(@orderId::text));", conn3, tx3))
            {
                cmd3.Parameters.AddWithValue("orderId", orderId1.ToString());
                lockAcquiredWhileTx1Active = (bool)(await cmd3.ExecuteScalarAsync())!;
            }
            Assert.False(lockAcquiredWhileTx1Active, "Lock on orderId1 must be held by tx1 and reject concurrent try_lock");

            // Rollback Tx 1 -> releases advisory lock
            await tx1.RollbackAsync();

            // Now Tx 3 can acquire lock on orderId1
            bool lockAcquiredAfterRollback;
            await using (var cmd3Retry = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(hashtext(@orderId::text));", conn3, tx3))
            {
                cmd3Retry.Parameters.AddWithValue("orderId", orderId1.ToString());
                lockAcquiredAfterRollback = (bool)(await cmd3Retry.ExecuteScalarAsync())!;
            }
            Assert.True(lockAcquiredAfterRollback, "Rollback of tx1 must release advisory lock immediately");
            await tx3.CommitAsync();
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task PostgreSql_DatabaseMigrationHealthCheck_DetectsPendingMigrations_AndReportsUnhealthy()
    {
        var dbName = "notif_health_test_" + Guid.NewGuid().ToString("N")[..8];
        await RecreateDatabaseAsync(dbName);
        try
        {
            var connString = GetTestConnectionString(dbName);
            var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            await using var context = new NotificationServiceDbContext(options);
            var migrator = context.GetService<IMigrator>();

            // 1. Apply only initial migration -> second migration is pending
            await migrator.MigrateAsync("20261102082253_InitialNotificationSchema");

            var healthCheck = new Dprental.NotificationService.Api.HealthChecks.DatabaseMigrationHealthCheck(context);
            var checkContext = new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext();

            var resultPending = await healthCheck.CheckHealthAsync(checkContext);
            Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, resultPending.Status);
            Assert.Contains("migration chưa được áp dụng", resultPending.Description);

            // 2. Migrate to latest
            await context.Database.MigrateAsync();

            var resultLatest = await healthCheck.CheckHealthAsync(checkContext);
            Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy, resultLatest.Status);
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Migration_WithMalformedRenderJsonAndEmptyGuids_ShouldSucceedWithoutCrashing()
    {
        var dbName = "notif_migr_malformed_" + Guid.NewGuid().ToString("N")[..8];
        await RecreateDatabaseAsync(dbName);
        try
        {
            var connString = GetTestConnectionString(dbName);
            var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            // 1. Migrate to f224363 schema (AddNotificationSchemaUpgrades)
            await using (var context = new NotificationServiceDbContext(options))
            {
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync("20261104042342_AddNotificationSchemaUpgrades");
            }

            var notifMalformed = Guid.NewGuid();
            var notifEmptyGuid = Guid.NewGuid();
            var notifValid = Guid.NewGuid();
            var notifJsonNull = Guid.NewGuid();
            var notifJsonArray = Guid.NewGuid();
            var notifJsonNumeric = Guid.NewGuid();
            var validItemId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var retReqConflict = Guid.NewGuid();
            var retReqSingle = Guid.NewGuid();
            var itemConflictA = Guid.NewGuid();
            var itemConflictB = Guid.NewGuid();
            var itemSingle = Guid.NewGuid();

            await using (var conn = new NpgsqlConnection(connString))
            {
                await conn.OpenAsync();

                var insertSql = @"
                    INSERT INTO notification.notifications
                    (id, recipient_user_id, notification_type, category, severity, title, body,
                     resource_type, resource_id, source_event_id, source_producer, source_event_type, event_version,
                     template_code, template_version, occurred_at, created_at, render_data_json)
                    VALUES
                    (@id1, @userId, 'RENTAL_OVERDUE', 'Order', 'Warning', 'Overdue', 'Body',
                     'RentalItem', 'res-1', @src1, 'RentalService', 'HandoverOverdue', 1,
                     'TMPL', 'v1', NOW(), NOW(), '{malformed json containing rentalItemId: broken'),
                    (@id2, @userId, 'RENTAL_OVERDUE', 'Order', 'Warning', 'Overdue', 'Body',
                     'RentalItem', 'res-2', @src2, 'RentalService', 'HandoverOverdue', 1,
                     'TMPL', 'v1', NOW(), NOW(), '{""rentalItemId"": ""00000000-0000-0000-0000-000000000000""}'),
                    (@id3, @userId, 'RENTAL_OVERDUE', 'Order', 'Warning', 'Overdue', 'Body',
                     'RentalItem', 'res-3', @src3, 'RentalService', 'HandoverOverdue', 1,
                     'TMPL', 'v1', NOW(), NOW(), '{""rentalItemId"": ""' || @validItemId || '""}'),
                    (@idNull, @userId, 'RENTAL_OVERDUE', 'Order', 'Warning', 'Overdue', 'Body',
                     'RentalItem', 'res-4', @srcNull, 'RentalService', 'HandoverOverdue', 1,
                     'TMPL', 'v1', NOW(), NOW(), 'null'),
                    (@idArr, @userId, 'RENTAL_OVERDUE', 'Order', 'Warning', 'Overdue', 'Body',
                     'RentalItem', 'res-5', @srcArr, 'RentalService', 'HandoverOverdue', 1,
                     'TMPL', 'v1', NOW(), NOW(), '[1, 2, 3]'),
                    (@idNum, @userId, 'RENTAL_OVERDUE', 'Order', 'Warning', 'Overdue', 'Body',
                     'RentalItem', 'res-6', @srcNum, 'RentalService', 'HandoverOverdue', 1,
                     'TMPL', 'v1', NOW(), NOW(), '{""rentalItemId"": 99999}');";
                var insertWmSql = @"
                    INSERT INTO notification.aggregate_watermarks
                    (scope, aggregate_id, recipient_user_id, last_order_version, is_terminal, is_paid, updated_at)
                    VALUES
                    ('ReturnRequest', @retReqConflict, @userId, 1, false, false, NOW()),
                    ('ReturnRequest', @retReqSingle, @userId, 1, false, false, NOW());";
                await using var wmCmd = new NpgsqlCommand(insertWmSql, conn);
                wmCmd.Parameters.AddWithValue("retReqConflict", retReqConflict);
                wmCmd.Parameters.AddWithValue("retReqSingle", retReqSingle);
                wmCmd.Parameters.AddWithValue("userId", userId);
                await wmCmd.ExecuteNonQueryAsync();

                var insertRetNotifSql = @"
                    INSERT INTO notification.notifications
                    (id, recipient_user_id, notification_type, category, severity, title, body,
                     resource_type, resource_id, source_event_id, source_producer, source_event_type, event_version,
                     template_code, template_version, occurred_at, created_at, render_data_json)
                    VALUES
                    (@idConflict1, @userId, 'RETURN_CONFIRMATION_OVERDUE_SUPPLIER', 'Order', 'Action', 'Overdue', 'Body',
                     'ReturnRequest', @retReqConflictStr, @src4, 'RentalService', 'ReturnOverdue', 1,
                     'TMPL', 'v1', NOW(), NOW(), '{""rentalItemId"": ""' || @itemConflictA || '""}'),
                    (@idConflict2, @userId, 'RETURN_CONFIRMATION_OVERDUE_SUPPLIER', 'Order', 'Action', 'Overdue', 'Body',
                     'ReturnRequest', @retReqConflictStr, @src5, 'RentalService', 'ReturnOverdue', 1,
                     'TMPL', 'v1', NOW(), NOW(), '{""rentalItemId"": ""' || @itemConflictB || '""}'),
                    (@idSingle, @userId, 'RETURN_CONFIRMATION_OVERDUE_SUPPLIER', 'Order', 'Action', 'Overdue', 'Body',
                     'ReturnRequest', @retReqSingleStr, @src6, 'RentalService', 'ReturnOverdue', 1,
                     'TMPL', 'v1', NOW(), NOW(), '{""rentalItemId"": ""' || @itemSingle || '""}'),
                    (@idRetMalformed, @userId, 'RETURN_CONFIRMATION_OVERDUE_SUPPLIER', 'Order', 'Action', 'Overdue', 'Body',
                     'ReturnRequest', 'res-malformed', @src7, 'RentalService', 'ReturnOverdue', 1,
                     'TMPL', 'v1', NOW(), NOW(), '{malformed returnRequestId json: broken');";
                await using var retCmd = new NpgsqlCommand(insertRetNotifSql, conn);
                retCmd.Parameters.AddWithValue("idConflict1", Guid.NewGuid());
                retCmd.Parameters.AddWithValue("idConflict2", Guid.NewGuid());
                retCmd.Parameters.AddWithValue("idSingle", Guid.NewGuid());
                retCmd.Parameters.AddWithValue("idRetMalformed", Guid.NewGuid());
                retCmd.Parameters.AddWithValue("userId", userId);
                retCmd.Parameters.AddWithValue("retReqConflictStr", retReqConflict.ToString());
                retCmd.Parameters.AddWithValue("retReqSingleStr", retReqSingle.ToString());
                retCmd.Parameters.AddWithValue("itemConflictA", itemConflictA);
                retCmd.Parameters.AddWithValue("itemConflictB", itemConflictB);
                retCmd.Parameters.AddWithValue("itemSingle", itemSingle);
                retCmd.Parameters.AddWithValue("src4", Guid.NewGuid());
                retCmd.Parameters.AddWithValue("src5", Guid.NewGuid());
                retCmd.Parameters.AddWithValue("src6", Guid.NewGuid());
                retCmd.Parameters.AddWithValue("src7", Guid.NewGuid());
                await retCmd.ExecuteNonQueryAsync();
                await using var cmd = new NpgsqlCommand(insertSql, conn);

                cmd.Parameters.AddWithValue("id1", notifMalformed);
                cmd.Parameters.AddWithValue("id2", notifEmptyGuid);
                cmd.Parameters.AddWithValue("id3", notifValid);
                cmd.Parameters.AddWithValue("idNull", notifJsonNull);
                cmd.Parameters.AddWithValue("idArr", notifJsonArray);
                cmd.Parameters.AddWithValue("idNum", notifJsonNumeric);
                cmd.Parameters.AddWithValue("validItemId", validItemId.ToString());
                cmd.Parameters.AddWithValue("userId", userId);
                cmd.Parameters.AddWithValue("src1", Guid.NewGuid());
                cmd.Parameters.AddWithValue("src2", Guid.NewGuid());
                cmd.Parameters.AddWithValue("src3", Guid.NewGuid());
                cmd.Parameters.AddWithValue("srcNull", Guid.NewGuid());
                cmd.Parameters.AddWithValue("srcArr", Guid.NewGuid());
                cmd.Parameters.AddWithValue("srcNum", Guid.NewGuid());
                await cmd.ExecuteNonQueryAsync();
            }

            // 3. Migrate to latest (AddLinkageAndCorrectPaidMilestones)
            await using (var context = new NotificationServiceDbContext(options))
            {
                await context.Database.MigrateAsync();
            }

            // 4. Verify results
            await using (var context = new NotificationServiceDbContext(options))
            {
                var n1 = await context.Notifications.FindAsync(notifMalformed);
                Assert.NotNull(n1);
                Assert.Null(n1.RentalItemId); // Kept original raw json, did not crash or set invalid ID

                var n2 = await context.Notifications.FindAsync(notifEmptyGuid);
                Assert.NotNull(n2);
                Assert.Null(n2.RentalItemId); // Guid.Empty ignored

                var n3 = await context.Notifications.FindAsync(notifValid);
                Assert.NotNull(n3);
                Assert.Equal(validItemId, n3.RentalItemId);

                var nNull = await context.Notifications.FindAsync(notifJsonNull);
                Assert.NotNull(nNull);
                Assert.Null(nNull.RentalItemId); // json null ignored

                var nArr = await context.Notifications.FindAsync(notifJsonArray);
                Assert.NotNull(nArr);
                Assert.Null(nArr.RentalItemId); // json array ignored

                var nNum = await context.Notifications.FindAsync(notifJsonNumeric);
                Assert.NotNull(nNum);
                Assert.Null(nNum.RentalItemId); // json numeric ignored

                // Verify conflicting ReturnRequest has NULL parent_item_id (not assigned randomly)
                var wmConflict = await context.AggregateWatermarks.FirstAsync(w => w.Scope == "ReturnRequest" && w.AggregateId == retReqConflict);
                Assert.Null(wmConflict.ParentItemId);

                // Verify single ReturnRequest has resolved parent_item_id
                var wmSingle = await context.AggregateWatermarks.FirstAsync(w => w.Scope == "ReturnRequest" && w.AggregateId == retReqSingle);
                Assert.Equal(itemSingle, wmSingle.ParentItemId);
            }
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task DatabaseAt20261104075246_UpgradeCorrectionMigration_BackfillsMissingLinkageAndIsIdempotent()
    {
        var dbName = "notif_migr_correction_" + Guid.NewGuid().ToString("N")[..8];
        await RecreateDatabaseAsync(dbName);
        try
        {
            var connString = GetTestConnectionString(dbName);
            var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            // 1. Migrate up to 20261104075246_AddLinkageAndCorrectPaidMilestones
            await using (var context = new NotificationServiceDbContext(options))
            {
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync("20261104075246_AddLinkageAndCorrectPaidMilestones");
            }

            var notifMissingLinkage = Guid.NewGuid();
            var notifMalformed = Guid.NewGuid();
            var validItemId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var retReqSingle = Guid.NewGuid();
            var retReqConflict = Guid.NewGuid();
            var itemSingle = Guid.NewGuid();
            var itemConflictA = Guid.NewGuid();
            var itemConflictB = Guid.NewGuid();

            // 2. Insert records created after migration but missing linkage (e.g. from legacy clients or raw inserts)
            await using (var conn = new NpgsqlConnection(connString))
            {
                await conn.OpenAsync();

                var insertWmSql = @"
                    INSERT INTO notification.aggregate_watermarks
                    (scope, aggregate_id, recipient_user_id, last_order_version, is_terminal, is_paid, updated_at)
                    VALUES
                    ('ReturnRequest', @retReqSingle, @userId, 1, false, false, NOW()),
                    ('ReturnRequest', @retReqConflict, @userId, 1, false, false, NOW());";
                await using var wmCmd = new NpgsqlCommand(insertWmSql, conn);
                wmCmd.Parameters.AddWithValue("retReqSingle", retReqSingle);
                wmCmd.Parameters.AddWithValue("retReqConflict", retReqConflict);
                wmCmd.Parameters.AddWithValue("userId", userId);
                await wmCmd.ExecuteNonQueryAsync();

                var insertNotifSql = @"
                    INSERT INTO notification.notifications
                    (id, recipient_user_id, notification_type, category, severity, title, body,
                     resource_type, resource_id, source_event_id, source_producer, source_event_type, event_version,
                     template_code, template_version, occurred_at, created_at, render_data_json)
                    VALUES
                    (@idValid, @userId, 'RENTAL_OVERDUE', 'Order', 'Warning', 'Overdue', 'Body',
                     'RentalItem', 'res-v', @src1, 'RentalService', 'HandoverOverdue', 1,
                     'TMPL', 'v1', NOW(), NOW(), '{""rentalItemId"": ""' || @validItemId || '""}'),
                    (@idMal, @userId, 'RENTAL_OVERDUE', 'Order', 'Warning', 'Overdue', 'Body',
                     'RentalItem', 'res-m', @src2, 'RentalService', 'HandoverOverdue', 1,
                     'TMPL', 'v1', NOW(), NOW(), '{broken json with rentalItemId'),
                    (@idSingleNotif, @userId, 'RETURN_CONFIRMATION_OVERDUE_SUPPLIER', 'Order', 'Action', 'Overdue', 'Body',
                     'ReturnRequest', @retReqSingleStr, @src3, 'RentalService', 'ReturnOverdue', 1,
                     'TMPL', 'v1', NOW(), NOW(), '{""rentalItemId"": ""' || @itemSingle || '""}'),
                    (@idConflict1, @userId, 'RETURN_CONFIRMATION_OVERDUE_SUPPLIER', 'Order', 'Action', 'Overdue', 'Body',
                     'ReturnRequest', @retReqConflictStr, @src4, 'RentalService', 'ReturnOverdue', 1,
                     'TMPL', 'v1', NOW(), NOW(), '{""rentalItemId"": ""' || @itemConflictA || '""}'),
                    (@idConflict2, @userId, 'RETURN_CONFIRMATION_OVERDUE_SUPPLIER', 'Order', 'Action', 'Overdue', 'Body',
                     'ReturnRequest', @retReqConflictStr, @src5, 'RentalService', 'ReturnOverdue', 1,
                     'TMPL', 'v1', NOW(), NOW(), '{""rentalItemId"": ""' || @itemConflictB || '""}');";
                await using var notifCmd = new NpgsqlCommand(insertNotifSql, conn);
                notifCmd.Parameters.AddWithValue("idValid", notifMissingLinkage);
                notifCmd.Parameters.AddWithValue("idMal", notifMalformed);
                notifCmd.Parameters.AddWithValue("idSingleNotif", Guid.NewGuid());
                notifCmd.Parameters.AddWithValue("idConflict1", Guid.NewGuid());
                notifCmd.Parameters.AddWithValue("idConflict2", Guid.NewGuid());
                notifCmd.Parameters.AddWithValue("validItemId", validItemId.ToString());
                notifCmd.Parameters.AddWithValue("retReqSingleStr", retReqSingle.ToString());
                notifCmd.Parameters.AddWithValue("retReqConflictStr", retReqConflict.ToString());
                notifCmd.Parameters.AddWithValue("itemSingle", itemSingle.ToString());
                notifCmd.Parameters.AddWithValue("itemConflictA", itemConflictA.ToString());
                notifCmd.Parameters.AddWithValue("itemConflictB", itemConflictB.ToString());
                notifCmd.Parameters.AddWithValue("userId", userId);
                notifCmd.Parameters.AddWithValue("src1", Guid.NewGuid());
                notifCmd.Parameters.AddWithValue("src2", Guid.NewGuid());
                notifCmd.Parameters.AddWithValue("src3", Guid.NewGuid());
                notifCmd.Parameters.AddWithValue("src4", Guid.NewGuid());
                notifCmd.Parameters.AddWithValue("src5", Guid.NewGuid());
                await notifCmd.ExecuteNonQueryAsync();
            }

            // 3. Migrate to latest (CorrectNotificationLinkageBackfill)
            await using (var context = new NotificationServiceDbContext(options))
            {
                await context.Database.MigrateAsync();
            }

            // 4. Verify correction backfill
            await using (var context = new NotificationServiceDbContext(options))
            {
                var nValid = await context.Notifications.FindAsync(notifMissingLinkage);
                Assert.NotNull(nValid);
                Assert.Equal(validItemId, nValid.RentalItemId); // Backfilled safely!

                var nMal = await context.Notifications.FindAsync(notifMalformed);
                Assert.NotNull(nMal);
                Assert.Null(nMal.RentalItemId); // Malformed JSON kept safe!

                var wmSingle = await context.AggregateWatermarks.FirstAsync(w => w.Scope == "ReturnRequest" && w.AggregateId == retReqSingle);
                Assert.Equal(itemSingle, wmSingle.ParentItemId); // Single item resolved!

                var wmConflict = await context.AggregateWatermarks.FirstAsync(w => w.Scope == "ReturnRequest" && w.AggregateId == retReqConflict);
                Assert.Null(wmConflict.ParentItemId); // Conflicting item IDs left unresolved!
            }

            // 5. Idempotency check: run MigrateAsync() a second time
            await using (var context = new NotificationServiceDbContext(options))
            {
                await context.Database.MigrateAsync();

                var count = await context.Notifications.CountAsync();
                Assert.Equal(5, count);

                var wmCount = await context.AggregateWatermarks.CountAsync();
                Assert.Equal(2, wmCount);
            }
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task DatabaseAt20261104084104_UpgradeNormalizeUrlsMigration_NormalizesUrlsAndPreservesActionableFlags()
    {
        var dbName = "notif_migr_url_norm_" + Guid.NewGuid().ToString("N")[..8];
        await RecreateDatabaseAsync(dbName);
        try
        {
            var connString = GetTestConnectionString(dbName);
            var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            // 1. Migrate up to previous migration (20261104084104_CorrectNotificationLinkageBackfill)
            await using (var context = new NotificationServiceDbContext(options))
            {
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync("20261104084104_CorrectNotificationLinkageBackfill");
            }

            var userId = Guid.NewGuid();
            var orderId1 = Guid.NewGuid();
            var orderId2 = Guid.NewGuid();
            var orderId3 = Guid.NewGuid();
            var returnId = Guid.NewGuid();
            var settlementId = Guid.NewGuid();

            var idRenterPayment = Guid.NewGuid();
            var idRenterOther = Guid.NewGuid();
            var idSupplierSubOrder = Guid.NewGuid();
            var idSupplierReturn = Guid.NewGuid();
            var idSupplierSettlement = Guid.NewGuid();
            var idSupplierDispute = Guid.NewGuid();
            var idSuppressedCta = Guid.NewGuid();

            // 2. Insert records with legacy URL formats
            await using (var conn = new NpgsqlConnection(connString))
            {
                await conn.OpenAsync();

                var insertSql = @"
                    INSERT INTO notification.notifications
                    (id, recipient_user_id, notification_type, category, severity, title, body,
                     resource_type, resource_id, source_event_id, source_producer, source_event_type, event_version,
                     template_code, template_version, occurred_at, created_at,
                     deep_link_url, action_url, is_actionable)
                    VALUES
                    -- 1. Renter order placed with checkout payment CTA
                    (@idRenterPayment, @userId, 'ORDER_CREATED', 'ORDER', 'ACTION_REQUIRED', 'T1', 'B1',
                     'RentalOrder', @ord1, @src1, 'RentalService', 'RentalOrderCreated', 1,
                     'ORDER_CREATED', '1.0', NOW(), NOW(),
                     '/renter/orders/' || @ord1, '/checkout/payment?orderId=' || @ord1, true),

                    -- 2. Renter other order link (action_url had renter link)
                    (@idRenterOther, @userId, 'ORDER_CONFIRMED', 'ORDER', 'INFO', 'T2', 'B2',
                     'RentalOrder', @ord2, @src2, 'RentalService', 'RentalItemConfirmed', 1,
                     'ORDER_CONFIRMED', '1.0', NOW(), NOW(),
                     '/renter/orders/' || @ord2, '/renter/orders/' || @ord2, false),

                    -- 3. Supplier confirmation sub-order link
                    (@idSupplierSubOrder, @userId, 'SUPPLIER_CONFIRMATION_REQUIRED', 'ORDER', 'ACTION_REQUIRED', 'T3', 'B3',
                     'RentalOrder', @ord3, @src3, 'RentalService', 'RentalSupplierConfirmationRequested', 1,
                     'SUPPLIER_CONFIRMATION_REQUIRED', '1.0', NOW(), NOW(),
                     '/supplier/sub-orders/' || @ord3, '/supplier/sub-orders/' || @ord3 || '/confirm', true),

                    -- 4. Supplier return link
                    (@idSupplierReturn, @userId, 'RETURN_CONFIRMATION_OVERDUE_SUPPLIER', 'RETURN', 'ACTION_REQUIRED', 'T4', 'B4',
                     'ReturnRequest', @retId, @src4, 'RentalService', 'ReturnConfirmationOverdue', 1,
                     'RETURN_CONFIRMATION_OVERDUE_SUPPLIER', '1.0', NOW(), NOW(),
                     '/supplier/returns/' || @retId, '/supplier/returns/' || @retId, true),

                    -- 5. Supplier settlement link
                    (@idSupplierSettlement, @userId, 'PAYOUT_COMPLETED', 'PAYMENT', 'SUCCESS', 'T5', 'B5',
                     'Settlement', @stlId, @src5, 'PaymentService', 'SupplierPayoutCompleted', 1,
                     'PAYOUT_COMPLETED', '1.0', NOW(), NOW(),
                     '/supplier/settlements/' || @stlId, NULL, false),

                    -- 6. Supplier dispute resolution link (/supplier/sub-orders)
                    (@idSupplierDispute, @userId, 'DEDUCTION_DISPUTE_RESOLVED_SUPPLIER', 'SETTLEMENT', 'INFO', 'T6', 'B6',
                     'RentalItem', @ord1, @src6, 'RentalService', 'RentalDeductionDisputeResolved', 1,
                     'DEDUCTION_DISPUTE_RESOLVED_SUPPLIER', '1.0', NOW(), NOW(),
                     '/supplier/sub-orders', NULL, false),

                    -- 7. Suppressed CTA: renter link, action_url is null, is_actionable is false
                    (@idSuppressedCta, @userId, 'ORDER_CREATED', 'ORDER', 'INFO', 'T7', 'B7',
                     'RentalOrder', @ord1, @src7, 'RentalService', 'RentalOrderCreated', 1,
                     'ORDER_CREATED', '1.0', NOW(), NOW(),
                     '/renter/orders/' || @ord1, NULL, false);";

                await using var cmd = new NpgsqlCommand(insertSql, conn);
                cmd.Parameters.AddWithValue("idRenterPayment", idRenterPayment);
                cmd.Parameters.AddWithValue("idRenterOther", idRenterOther);
                cmd.Parameters.AddWithValue("idSupplierSubOrder", idSupplierSubOrder);
                cmd.Parameters.AddWithValue("idSupplierReturn", idSupplierReturn);
                cmd.Parameters.AddWithValue("idSupplierSettlement", idSupplierSettlement);
                cmd.Parameters.AddWithValue("idSupplierDispute", idSupplierDispute);
                cmd.Parameters.AddWithValue("idSuppressedCta", idSuppressedCta);
                cmd.Parameters.AddWithValue("userId", userId);
                cmd.Parameters.AddWithValue("ord1", orderId1.ToString());
                cmd.Parameters.AddWithValue("ord2", orderId2.ToString());
                cmd.Parameters.AddWithValue("ord3", orderId3.ToString());
                cmd.Parameters.AddWithValue("retId", returnId.ToString());
                cmd.Parameters.AddWithValue("stlId", settlementId.ToString());
                cmd.Parameters.AddWithValue("src1", Guid.NewGuid());
                cmd.Parameters.AddWithValue("src2", Guid.NewGuid());
                cmd.Parameters.AddWithValue("src3", Guid.NewGuid());
                cmd.Parameters.AddWithValue("src4", Guid.NewGuid());
                cmd.Parameters.AddWithValue("src5", Guid.NewGuid());
                cmd.Parameters.AddWithValue("src6", Guid.NewGuid());
                cmd.Parameters.AddWithValue("src7", Guid.NewGuid());
                await cmd.ExecuteNonQueryAsync();
            }

            // 3. Migrate to latest (NormalizeNotificationUrls)
            await using (var context = new NotificationServiceDbContext(options))
            {
                await context.Database.MigrateAsync();
            }

            // 4. Verify URL normalization and preservation of actionable flags
            await using (var context = new NotificationServiceDbContext(options))
            {
                // Row 1: Renter payment CTA
                var n1 = await context.Notifications.FindAsync(idRenterPayment);
                Assert.NotNull(n1);
                Assert.Equal($"/orders/{orderId1}", n1.DeepLinkUrl);
                Assert.Equal($"/orders/{orderId1}", n1.ActionUrl);
                Assert.True(n1.IsActionable);

                // Row 2: Renter other order link
                var n2 = await context.Notifications.FindAsync(idRenterOther);
                Assert.NotNull(n2);
                Assert.Equal($"/orders/{orderId2}", n2.DeepLinkUrl);
                Assert.Equal($"/orders/{orderId2}", n2.ActionUrl);
                Assert.False(n2.IsActionable);

                // Row 3: Supplier sub-order confirmation
                var n3 = await context.Notifications.FindAsync(idSupplierSubOrder);
                Assert.NotNull(n3);
                Assert.Equal("/own/orders", n3.DeepLinkUrl);
                Assert.Equal("/own/orders", n3.ActionUrl);
                Assert.True(n3.IsActionable);

                // Row 4: Supplier return
                var n4 = await context.Notifications.FindAsync(idSupplierReturn);
                Assert.NotNull(n4);
                Assert.Equal("/own/orders", n4.DeepLinkUrl);
                Assert.Equal("/own/orders", n4.ActionUrl);
                Assert.True(n4.IsActionable);

                // Row 5: Supplier settlement
                var n5 = await context.Notifications.FindAsync(idSupplierSettlement);
                Assert.NotNull(n5);
                Assert.Equal("/own/orders", n5.DeepLinkUrl);
                Assert.Null(n5.ActionUrl);
                Assert.False(n5.IsActionable);

                // Row 6: Supplier dispute resolution
                var n6 = await context.Notifications.FindAsync(idSupplierDispute);
                Assert.NotNull(n6);
                Assert.Equal("/own/orders", n6.DeepLinkUrl);
                Assert.Null(n6.ActionUrl);
                Assert.False(n6.IsActionable);

                // Row 7: Suppressed CTA - Invariant: must NOT restore suppressed CTAs!
                var n7 = await context.Notifications.FindAsync(idSuppressedCta);
                Assert.NotNull(n7);
                Assert.Equal($"/orders/{orderId1}", n7.DeepLinkUrl);
                Assert.Null(n7.ActionUrl);
                Assert.False(n7.IsActionable);
            }

            // 5. Idempotency check: run MigrateAsync() a second time
            await using (var context = new NotificationServiceDbContext(options))
            {
                await context.Database.MigrateAsync();

                var n1Again = await context.Notifications.FindAsync(idRenterPayment);
                Assert.NotNull(n1Again);
                Assert.Equal($"/orders/{orderId1}", n1Again.DeepLinkUrl);
                Assert.Equal($"/orders/{orderId1}", n1Again.ActionUrl);
            }
        }
        finally
        {
            await TestEnvironment.DropDatabaseAsync(dbName);
        }
    }
}
