using System.Text.Json;
using Dprental.NotificationService.Application.Kafka.Services;
using Dprental.NotificationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Kafka;

public sealed class DisputeResolutionPostgreSqlTests
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RentalDisputeResolved_PersistsTwoRecipientsAndDeduplicatesReplay()
    {
        var source = Environment.GetEnvironmentVariable("NOTIFICATION_TEST_POSTGRES")
            ?? Environment.GetEnvironmentVariable("POSTGRESQL_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Set NOTIFICATION_TEST_POSTGRES to a PostgreSQL connection with CREATE DATABASE permission.");
        var adminConnection = new NpgsqlConnectionStringBuilder(source) { Database = "postgres" }.ConnectionString;
        var databaseName = $"notification_dispute_test_{Guid.NewGuid():N}";
        var testConnection = new NpgsqlConnectionStringBuilder(source) { Database = databaseName }.ConnectionString;

        await using (var admin = new NpgsqlConnection(adminConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
                .UseNpgsql(testConnection).Options;
            await using (var db = new NotificationServiceDbContext(options))
            {
                await db.Database.MigrateAsync();
                var (service, _, _, _) = NotificationIngestionTests.CreateTestIngestionService(db);
                var eventId = Guid.NewGuid();
                var orderId = Guid.NewGuid();
                var renterId = Guid.NewGuid();
                var supplierId = Guid.NewGuid();
                var raw = JsonSerializer.Serialize(new
                {
                    messageId = eventId,
                    messageType = "RentalDeductionDisputeResolved",
                    version = 1,
                    producer = "RentalService",
                    aggregateId = orderId.ToString("D"),
                    occurredAt = DateTimeOffset.UtcNow,
                    payload = new
                    {
                        disputeId = Guid.NewGuid(),
                        deductionProposalId = Guid.NewGuid(),
                        rentalOrderId = orderId,
                        rentalItemId = Guid.NewGuid(),
                        renterId,
                        supplierId,
                        decision = "Partial",
                        finalDeductionAmount = 20_000m,
                        resolvedAt = DateTimeOffset.UtcNow,
                        orderVersion = 5
                    }
                });

                Assert.Equal(IngestionOutcome.Processed,
                    await service.IngestMessageAsync(raw, "rental.events.v1", 0, 1));
                Assert.Equal(IngestionOutcome.Duplicate,
                    await service.IngestMessageAsync(raw, "rental.events.v1", 0, 1));

                await using var check = new NotificationServiceDbContext(options);
                var notifications = await check.Notifications.AsNoTracking()
                    .Where(n => n.SourceEventId == eventId).ToListAsync();
                Assert.Equal(2, notifications.Count);
                Assert.Contains(notifications, n => n.RecipientUserId == renterId &&
                    n.NotificationType == "DEDUCTION_DISPUTE_RESOLVED_RENTER");
                Assert.Contains(notifications, n => n.RecipientUserId == supplierId &&
                    n.NotificationType == "DEDUCTION_DISPUTE_RESOLVED_SUPPLIER");
                Assert.Single(await check.InboxReceipts.AsNoTracking()
                    .Where(r => r.MessageId == eventId).ToListAsync());
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminConnection);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
