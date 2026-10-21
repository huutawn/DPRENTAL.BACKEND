using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Overdue;

public sealed class HandoverOverdueQueryPostgreSqlTests
{
    [Fact]
    public async Task PreviouslyNotifiedOrder_BecomesDueAfterConfiguredGrace()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL test database is required for handover deadline query.");
        }

        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            Guid orderId = Guid.NewGuid();
            Guid itemId = Guid.NewGuid();

            await using (var db = new RentalServiceDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                var order = PostgresTestDatabaseHelper.CreateOrderWithItem(
                    orderId, itemId, Guid.NewGuid(), 1,
                    new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
                Assert.True(order.MarkAsPaid("INV-1", "PAY-1",
                    new DateTimeOffset(2026, 10, 8, 0, 5, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 10, 8, 0, 5, 0, TimeSpan.Zero)).IsSuccess);
                typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!
                    .SetValue(order.Items.Single(), RentalItemStatus.AwaitingHandover);

                order.AddOperationReceipt(new RentalOperationReceipt(
                    Guid.NewGuid(), order.Id,
                    RentalOperationExecutor.ComputeOperationKey("HandoverOverdueNotice", $"{order.Id}:{itemId}"),
                    "HandoverOverdueNotice", "notice-fingerprint", true, null, null,
                    new DateTimeOffset(2026, 10, 10, 17, 0, 0, TimeSpan.Zero),
                    RentalOperationReceipt.StatusSucceeded,
                    targetItemId: itemId));
                db.RentalOrders.Add(order);
                await db.SaveChangesAsync();
            }

            await using (var db = new RentalServiceDbContext(options))
            {
                var repository = new RentalOrderRepository(db);
                var beforeGrace = await repository.GetHandoverOverdueOrderIdsAsync(
                    new DateOnly(2026, 10, 11), new DateOnly(2026, 10, 10), 10);
                var afterGrace = await repository.GetHandoverOverdueOrderIdsAsync(
                    new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 11), 10);

                Assert.DoesNotContain(orderId, beforeGrace);
                Assert.Contains(orderId, afterGrace);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }
}
