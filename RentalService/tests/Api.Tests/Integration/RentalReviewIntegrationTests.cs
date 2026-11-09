using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.DTOs.Operations;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.ReviewIntegration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Integration;
using Dprental.RentalService.Common;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Integration;

public sealed class RentalReviewIntegrationTests
{
    private const string ValidApiKey = "dev-internal-api-key-dprental-2026";
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task GetReviewSnapshot_ValidCompletedItem_Returns200WithAll8Fields()
    {
        var (connStr, dbName) = await CreateMigratedTestDatabaseAsync();
        try
        {
            var orderId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var renterId = Guid.NewGuid();
            var supplierId = Guid.NewGuid();
            var listingId = Guid.NewGuid();
            var completedAt = DateTimeOffset.UtcNow.AddDays(-2);

            await using (var db = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connStr)))
            {
                var order = CreateCompletedOrderWithItem(orderId, itemId, renterId, supplierId, listingId, completedAt);
                db.RentalOrders.Add(order);
                await db.SaveChangesAsync();
            }

            using var factory = CreateFactory(connStr);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

            var response = await client.GetAsync($"/internal/orders/{orderId}/items/{itemId}/review-snapshot");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var snapshot = await response.Content.ReadFromJsonAsync<RentalItemReviewSnapshotDto>(JsonOpts);
            Assert.NotNull(snapshot);
            Assert.Equal(orderId, snapshot.OrderId);
            Assert.Equal(itemId, snapshot.ItemId);
            Assert.Equal(renterId, snapshot.RenterId);
            Assert.Equal(supplierId, snapshot.SupplierId);
            Assert.Equal(listingId, snapshot.EquipmentListingId);
            Assert.Equal("Completed", snapshot.Status);
            Assert.NotNull(snapshot.CompletedAt);
            Assert.True(Math.Abs((snapshot.CompletedAt.Value - completedAt).TotalSeconds) < 0.01);
            Assert.True(snapshot.OrderVersion >= 1);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task GetReviewSnapshot_ItemNotCompleted_Returns200WithCurrentStatus()
    {
        var (connStr, dbName) = await CreateMigratedTestDatabaseAsync();
        try
        {
            var orderId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var renterId = Guid.NewGuid();
            var supplierId = Guid.NewGuid();
            var listingId = Guid.NewGuid();

            await using (var db = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connStr)))
            {
                var order = CreateOrderWithStatus(orderId, itemId, renterId, supplierId, listingId, RentalItemStatus.InRental, null);
                db.RentalOrders.Add(order);
                await db.SaveChangesAsync();
            }

            using var factory = CreateFactory(connStr);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

            var response = await client.GetAsync($"/internal/orders/{orderId}/items/{itemId}/review-snapshot");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var snapshot = await response.Content.ReadFromJsonAsync<RentalItemReviewSnapshotDto>(JsonOpts);
            Assert.NotNull(snapshot);
            Assert.Equal("InRental", snapshot.Status);
            Assert.Null(snapshot.CompletedAt);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task GetReviewSnapshot_ItemBelongsToDifferentOrder_Returns404WithRentalItemNotFound()
    {
        var (connStr, dbName) = await CreateMigratedTestDatabaseAsync();
        try
        {
            var orderId1 = Guid.NewGuid();
            var orderId2 = Guid.NewGuid();
            var itemId = Guid.NewGuid();

            await using (var db = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connStr)))
            {
                var order1 = CreateCompletedOrderWithItem(orderId1, itemId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
                db.RentalOrders.Add(order1);
                await db.SaveChangesAsync();
            }

            using var factory = CreateFactory(connStr);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

            // Request with orderId2 instead of orderId1
            var response = await client.GetAsync($"/internal/orders/{orderId2}/items/{itemId}/review-snapshot");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(JsonOpts);
            Assert.NotNull(problem);
            Assert.Equal("RENTAL_ITEM_NOT_FOUND", problem.Title);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task GetReviewSnapshot_ItemNotFound_Returns404WithRentalItemNotFound()
    {
        var (connStr, dbName) = await CreateMigratedTestDatabaseAsync();
        try
        {
            using var factory = CreateFactory(connStr);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

            var response = await client.GetAsync($"/internal/orders/{Guid.NewGuid()}/items/{Guid.NewGuid()}/review-snapshot");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(JsonOpts);
            Assert.NotNull(problem);
            Assert.Equal("RENTAL_ITEM_NOT_FOUND", problem.Title);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Security_MissingOrInvalidApiKey_Returns401Unauthorized()
    {
        var (connStr, dbName) = await CreateMigratedTestDatabaseAsync();
        try
        {
            using var factory = CreateFactory(connStr);
            using var client = factory.CreateClient();

            // 1. Missing key
            var res1 = await client.GetAsync($"/internal/orders/{Guid.NewGuid()}/items/{Guid.NewGuid()}/review-snapshot");
            Assert.Equal(HttpStatusCode.Unauthorized, res1.StatusCode);

            // 2. Wrong key
            using var req2 = new HttpRequestMessage(HttpMethod.Get, $"/internal/orders/{Guid.NewGuid()}/items/{Guid.NewGuid()}/review-snapshot");
            req2.Headers.Add("X-Api-Key", "invalid-fake-key");
            var res2 = await client.SendAsync(req2);
            Assert.Equal(HttpStatusCode.Unauthorized, res2.StatusCode);

            // 3. Multiple keys
            using var req3 = new HttpRequestMessage(HttpMethod.Get, $"/internal/orders/{Guid.NewGuid()}/items/{Guid.NewGuid()}/review-snapshot");
            req3.Headers.Add("X-Api-Key", ValidApiKey);
            req3.Headers.Add("X-Api-Key", "second-key");
            var res3 = await client.SendAsync(req3);
            Assert.Equal(HttpStatusCode.Unauthorized, res3.StatusCode);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Security_BearerCannotBypassApiKey_AndBearerNotRequired()
    {
        var (connStr, dbName) = await CreateMigratedTestDatabaseAsync();
        try
        {
            using var factory = CreateFactory(connStr);
            using var client = factory.CreateClient();

            // 1. Bearer provided without X-Api-Key -> rejected with 401 by InternalApiKeyMiddleware
            using var reqBearerOnly = new HttpRequestMessage(HttpMethod.Get, "/internal/orders/review-reconciliation");
            reqBearerOnly.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "fake-token");
            var res1 = await client.SendAsync(reqBearerOnly);
            Assert.Equal(HttpStatusCode.Unauthorized, res1.StatusCode);

            // 2. X-Api-Key provided without Bearer -> accepted without requiring Bearer
            using var reqKeyOnly = new HttpRequestMessage(HttpMethod.Get, "/internal/orders/review-reconciliation");
            reqKeyOnly.Headers.Add("X-Api-Key", ValidApiKey);
            var res2 = await client.SendAsync(reqKeyOnly);
            Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task ReviewReconciliation_KeysetPagination_TraversesPagesWithFixedWindow()
    {
        var (connStr, dbName) = await CreateMigratedTestDatabaseAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var seededItemIds = new List<Guid>();

            await using (var db = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connStr)))
            {
                // Seed 5 completed items within the 30-day window
                for (int i = 0; i < 5; i++)
                {
                    var orderId = Guid.NewGuid();
                    var itemId = Guid.NewGuid();
                    seededItemIds.Add(itemId);
                    var compAt = now.AddDays(-20 + i); // strictly ascending
                    var order = CreateCompletedOrderWithItem(orderId, itemId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), compAt);
                    db.RentalOrders.Add(order);
                }

                // Seed 1 item older than 30 days (should be excluded)
                var oldOrderId = Guid.NewGuid();
                var oldItemId = Guid.NewGuid();
                var oldOrder = CreateCompletedOrderWithItem(oldOrderId, oldItemId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now.AddDays(-35));
                db.RentalOrders.Add(oldOrder);

                // Seed 1 non-completed item (should be excluded)
                var activeOrderId = Guid.NewGuid();
                var activeItemId = Guid.NewGuid();
                var activeOrder = CreateOrderWithStatus(activeOrderId, activeItemId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), RentalItemStatus.InRental, null);
                db.RentalOrders.Add(activeOrder);

                await db.SaveChangesAsync();
            }

            using var factory = CreateFactory(connStr);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

            // Page 1: limit 2
            var resPage1 = await client.GetAsync("/internal/orders/review-reconciliation?limit=2");
            Assert.Equal(HttpStatusCode.OK, resPage1.StatusCode);
            var batch1 = await resPage1.Content.ReadFromJsonAsync<RentalReviewReconciliationBatchDto>(JsonOpts);
            Assert.NotNull(batch1);
            Assert.Equal(2, batch1.Items.Count);
            Assert.True(batch1.HasMore);
            Assert.False(string.IsNullOrWhiteSpace(batch1.NextCursor));
            Assert.Equal(seededItemIds[0], batch1.Items[0].ItemId);
            Assert.Equal(seededItemIds[1], batch1.Items[1].ItemId);

            // Page 2: with NextCursor from Page 1
            var resPage2 = await client.GetAsync($"/internal/orders/review-reconciliation?cursor={Uri.EscapeDataString(batch1.NextCursor!)}&limit=2");
            Assert.Equal(HttpStatusCode.OK, resPage2.StatusCode);
            var batch2 = await resPage2.Content.ReadFromJsonAsync<RentalReviewReconciliationBatchDto>(JsonOpts);
            Assert.NotNull(batch2);
            Assert.Equal(2, batch2.Items.Count);
            Assert.True(batch2.HasMore);
            Assert.False(string.IsNullOrWhiteSpace(batch2.NextCursor));
            Assert.Equal(seededItemIds[2], batch2.Items[0].ItemId);
            Assert.Equal(seededItemIds[3], batch2.Items[1].ItemId);

            // Page 3: with NextCursor from Page 2 (last remaining item)
            var resPage3 = await client.GetAsync($"/internal/orders/review-reconciliation?cursor={Uri.EscapeDataString(batch2.NextCursor!)}&limit=2");
            Assert.Equal(HttpStatusCode.OK, resPage3.StatusCode);
            var batch3 = await resPage3.Content.ReadFromJsonAsync<RentalReviewReconciliationBatchDto>(JsonOpts);
            Assert.NotNull(batch3);
            Assert.Single(batch3.Items);
            Assert.False(batch3.HasMore);
            Assert.Null(batch3.NextCursor);
            Assert.Equal(seededItemIds[4], batch3.Items[0].ItemId);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task ReviewReconciliation_InvalidCursor_Returns400BadRequest()
    {
        var (connStr, dbName) = await CreateMigratedTestDatabaseAsync();
        try
        {
            using var factory = CreateFactory(connStr);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

            var response = await client.GetAsync("/internal/orders/review-reconciliation?cursor=invalid-corrupt-base64!!");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(JsonOpts);
            Assert.NotNull(problem);
            Assert.Equal("INVALID_CURSOR", problem.Title);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task RealMigration_PostgreSql_BackfillsAuthoritativeEvidence_AndRejectsInvalidOrConflicting()
    {
        var (connStr, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connStr);
            const string priorMigration = "20261028084805_AddRentalRefundConfirmations";

            // 1. Migrate to prior migration (before CompletedAt existed)
            await using (var context = new RentalServiceDbContext(options))
            {
                var migrator = context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
                await migrator.MigrateAsync(priorMigration);
            }

            var t = new DateTimeOffset(2026, 11, 5, 9, 37, 17, 123, TimeSpan.Zero);

            var id1 = Guid.NewGuid(); // Valid: closures + 1 transition + outbox exact
            var id2 = Guid.NewGuid(); // Invalid: missing financial closure
            var id3 = Guid.NewGuid(); // Invalid: missing transition
            var id4 = Guid.NewGuid(); // Invalid: duplicate transitions
            var id5 = Guid.NewGuid(); // Invalid: outbox differs under 1 second (200ms)
            var id6 = Guid.NewGuid(); // Invalid: outbox differs over 1 second (5s)
            var id7 = Guid.NewGuid(); // Invalid: outbox invalid JSON payload

            await using (var seed = new HistoricalReceiptSeedDbContext(options))
            {
                // Item 1: Valid
                var o1 = CreateCompletedOrderWithItem(Guid.NewGuid(), id1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), t);
                var i1 = o1.Items.Single();
                typeof(RentalItem).GetProperty("FinalDeductionAmount")!.SetValue(i1, 0m);
                typeof(RentalItem).GetProperty("InventoryClosureConfirmedAt")!.SetValue(i1, t.AddMinutes(-5));
                typeof(RentalItem).GetProperty("FinancialClosureConfirmedAt")!.SetValue(i1, t);
                typeof(RentalItem).GetProperty("DeductionDeadline")!.SetValue(i1, t.AddDays(2));
                seed.RentalOrders.Add(o1);
                seed.RentalStateTransitions.Add(new RentalStateTransition(Guid.NewGuid(), o1.Id, id1, "PendingSettlement", "Completed", "SettlementCompleted", null, ActorRole.System, "Settled", t, "op-1"));
                seed.RentalOutboxMessages.Add(new RentalOutboxMessage(Guid.NewGuid(), "rental.events", o1.Id.ToString(), "RentalItemCompleted",
                    JsonSerializer.Serialize(new { payload = new { rentalItemId = id1, completedAt = t } }), t));

                // Item 2: Missing financial closure
                var o2 = CreateCompletedOrderWithItem(Guid.NewGuid(), id2, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), t);
                var i2 = o2.Items.Single();
                typeof(RentalItem).GetProperty("FinalDeductionAmount")!.SetValue(i2, 0m);
                typeof(RentalItem).GetProperty("InventoryClosureConfirmedAt")!.SetValue(i2, t.AddMinutes(-5));
                typeof(RentalItem).GetProperty("FinancialClosureConfirmedAt")!.SetValue(i2, null);
                seed.RentalOrders.Add(o2);
                seed.RentalStateTransitions.Add(new RentalStateTransition(Guid.NewGuid(), o2.Id, id2, "PendingSettlement", "Completed", "SettlementCompleted", null, ActorRole.System, "Settled", t, "op-2"));

                // Item 3: Missing transition
                var o3 = CreateCompletedOrderWithItem(Guid.NewGuid(), id3, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), t);
                var i3 = o3.Items.Single();
                typeof(RentalItem).GetProperty("FinalDeductionAmount")!.SetValue(i3, 0m);
                typeof(RentalItem).GetProperty("InventoryClosureConfirmedAt")!.SetValue(i3, t.AddMinutes(-5));
                typeof(RentalItem).GetProperty("FinancialClosureConfirmedAt")!.SetValue(i3, t);
                seed.RentalOrders.Add(o3);

                // Item 4: Duplicate transitions
                var o4 = CreateCompletedOrderWithItem(Guid.NewGuid(), id4, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), t);
                var i4 = o4.Items.Single();
                typeof(RentalItem).GetProperty("FinalDeductionAmount")!.SetValue(i4, 0m);
                typeof(RentalItem).GetProperty("InventoryClosureConfirmedAt")!.SetValue(i4, t.AddMinutes(-5));
                typeof(RentalItem).GetProperty("FinancialClosureConfirmedAt")!.SetValue(i4, t);
                seed.RentalOrders.Add(o4);
                seed.RentalStateTransitions.Add(new RentalStateTransition(Guid.NewGuid(), o4.Id, id4, "PendingSettlement", "Completed", "SettlementCompleted", null, ActorRole.System, "Settled 1", t, "op-4a"));
                seed.RentalStateTransitions.Add(new RentalStateTransition(Guid.NewGuid(), o4.Id, id4, "PendingSettlement", "Completed", "SettlementCompleted", null, ActorRole.System, "Settled 2", t.AddSeconds(1), "op-4b"));

                // Item 5: Outbox differs under 1 second (200ms)
                var o5 = CreateCompletedOrderWithItem(Guid.NewGuid(), id5, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), t);
                var i5 = o5.Items.Single();
                typeof(RentalItem).GetProperty("FinalDeductionAmount")!.SetValue(i5, 0m);
                typeof(RentalItem).GetProperty("InventoryClosureConfirmedAt")!.SetValue(i5, t.AddMinutes(-5));
                typeof(RentalItem).GetProperty("FinancialClosureConfirmedAt")!.SetValue(i5, t);
                seed.RentalOrders.Add(o5);
                seed.RentalStateTransitions.Add(new RentalStateTransition(Guid.NewGuid(), o5.Id, id5, "PendingSettlement", "Completed", "SettlementCompleted", null, ActorRole.System, "Settled", t, "op-5"));
                seed.RentalOutboxMessages.Add(new RentalOutboxMessage(Guid.NewGuid(), "rental.events", o5.Id.ToString(), "RentalItemCompleted",
                    JsonSerializer.Serialize(new { payload = new { rentalItemId = id5, completedAt = t.AddMilliseconds(200) } }), t));

                // Item 6: Outbox differs over 1 second (5s)
                var o6 = CreateCompletedOrderWithItem(Guid.NewGuid(), id6, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), t);
                var i6 = o6.Items.Single();
                typeof(RentalItem).GetProperty("FinalDeductionAmount")!.SetValue(i6, 0m);
                typeof(RentalItem).GetProperty("InventoryClosureConfirmedAt")!.SetValue(i6, t.AddMinutes(-5));
                typeof(RentalItem).GetProperty("FinancialClosureConfirmedAt")!.SetValue(i6, t);
                seed.RentalOrders.Add(o6);
                seed.RentalStateTransitions.Add(new RentalStateTransition(Guid.NewGuid(), o6.Id, id6, "PendingSettlement", "Completed", "SettlementCompleted", null, ActorRole.System, "Settled", t, "op-6"));
                seed.RentalOutboxMessages.Add(new RentalOutboxMessage(Guid.NewGuid(), "rental.events", o6.Id.ToString(), "RentalItemCompleted",
                    JsonSerializer.Serialize(new { payload = new { rentalItemId = id6, completedAt = t.AddSeconds(5) } }), t));

                // Item 7: Outbox invalid JSON payload
                var o7 = CreateCompletedOrderWithItem(Guid.NewGuid(), id7, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), t);
                var i7 = o7.Items.Single();
                typeof(RentalItem).GetProperty("FinalDeductionAmount")!.SetValue(i7, 0m);
                typeof(RentalItem).GetProperty("InventoryClosureConfirmedAt")!.SetValue(i7, t.AddMinutes(-5));
                typeof(RentalItem).GetProperty("FinancialClosureConfirmedAt")!.SetValue(i7, t);
                seed.RentalOrders.Add(o7);
                seed.RentalStateTransitions.Add(new RentalStateTransition(Guid.NewGuid(), o7.Id, id7, "PendingSettlement", "Completed", "SettlementCompleted", null, ActorRole.System, "Settled", t, "op-7"));
                seed.RentalOutboxMessages.Add(new RentalOutboxMessage(Guid.NewGuid(), "rental.events", o7.Id.ToString(), "RentalItemCompleted",
                    JsonSerializer.Serialize(new { payload = new { rentalItemId = id7, completedAt = "invalid-date-format" } }), t));

                await seed.SaveChangesAsync();
            }

            // 2. Execute REAL EF Core migration to latest (which runs 20261109100000_AddCompletedAtToRentalItemAndBackfill)
            await using (var context = new RentalServiceDbContext(options))
            {
                await context.Database.MigrateAsync();
            }

            // 3. Verify outcomes in migrated PostgreSQL database
            await using (var context = new RentalServiceDbContext(options))
            {
                var item1 = await context.RentalItems.SingleAsync(i => i.Id == id1);
                Assert.NotNull(item1.CompletedAt);
                Assert.True(Math.Abs((item1.CompletedAt.Value - t).TotalMilliseconds) < 1);
                // Verify deadline was not altered or extended
                Assert.NotNull(item1.DeductionDeadline);
                Assert.Equal(t.AddDays(2).ToUniversalTime(), item1.DeductionDeadline.Value.ToUniversalTime());

                var item2 = await context.RentalItems.SingleAsync(i => i.Id == id2);
                Assert.Null(item2.CompletedAt);

                var item3 = await context.RentalItems.SingleAsync(i => i.Id == id3);
                Assert.Null(item3.CompletedAt);

                var item4 = await context.RentalItems.SingleAsync(i => i.Id == id4);
                Assert.Null(item4.CompletedAt);

                var item5 = await context.RentalItems.SingleAsync(i => i.Id == id5);
                Assert.Null(item5.CompletedAt); // Outbox mismatch < 1s rejected

                var item6 = await context.RentalItems.SingleAsync(i => i.Id == id6);
                Assert.Null(item6.CompletedAt); // Outbox mismatch > 1s rejected

                var item7 = await context.RentalItems.SingleAsync(i => i.Id == id7);
                Assert.Null(item7.CompletedAt); // Invalid outbox payload rejected
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Repository_DirectQueries_SnapshotAndKeysetPaginationWithIdenticalTimestamps()
    {
        var (connStr, dbName) = await CreateMigratedTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connStr);
            var sameTime = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
            var orderId = Guid.NewGuid();
            var itemIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            itemIds.Sort(); // Sorted ascending for deterministic keyset tie-break assertions

            var seededOrders = new List<RentalOrder>();
            await using (var db = new RentalServiceDbContext(options))
            {
                foreach (var id in itemIds)
                {
                    var order = CreateCompletedOrderWithItem(Guid.NewGuid(), id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), sameTime);
                    seededOrders.Add(order);
                    db.RentalOrders.Add(order);
                }
                await db.SaveChangesAsync();
            }

            await using (var db = new RentalServiceDbContext(options))
            {
                var repo = new RentalOrderRepository(db);

                // Test Snapshot Record
                var snapRecord = await repo.GetReviewSnapshotRecordAsync(itemIds[0]);
                Assert.NotNull(snapRecord);
                Assert.Equal(seededOrders[0].Id, snapRecord.OrderId);
                Assert.Equal(itemIds[0], snapRecord.ItemId);
                Assert.Equal(RentalItemStatus.Completed, snapRecord.Status);
                Assert.NotNull(snapRecord.CompletedAt);

                // Test Keyset Pagination with IDENTICAL timestamps (breaks ties by Id)
                var page1 = await repo.GetReviewReconciliationPageAsync(sameTime.AddDays(-1), null, null, 2);
                Assert.Equal(2, page1.Items.Count);
                Assert.True(page1.HasMore);
                Assert.Equal(itemIds[0], page1.Items[0].ItemId);
                Assert.Equal(itemIds[1], page1.Items[1].ItemId);

                // Page 2 using lastCompletedAt and lastItemId from Page 1
                var page2 = await repo.GetReviewReconciliationPageAsync(sameTime.AddDays(-1), page1.Items[1].CompletedAt, page1.Items[1].ItemId, 2);
                Assert.Single(page2.Items);
                Assert.False(page2.HasMore);
                Assert.Equal(itemIds[2], page2.Items[0].ItemId);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task CompletionRetry_PreservesTimestamp_AndEnqueueFailureRollsBack()
    {
        var now = DateTimeOffset.UtcNow;
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var order = CreateCompletedOrderWithItem(orderId, itemId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now);
        var item = order.Items.Single();

        var initialCompletedAt = item.CompletedAt;
        Assert.NotNull(initialCompletedAt);

        // 1. Completion retry: TryCompleteSettlement called again does NOT overwrite CompletedAt
        typeof(RentalItem).GetProperty("FinalDeductionAmount")!.SetValue(item, 0m);
        typeof(RentalItem).GetProperty("InventoryClosureConfirmedAt")!.SetValue(item, now);
        typeof(RentalItem).GetProperty("FinancialClosureConfirmedAt")!.SetValue(item, now);
        typeof(RentalItem).GetProperty("Status")!.SetValue(item, RentalItemStatus.PendingSettlement);

        bool completedAgain = item.TryCompleteSettlement(now.AddHours(2), "op-retry-settle");
        Assert.True(completedAgain);
        Assert.Equal(initialCompletedAt, item.CompletedAt); // Preserved!

        // 2. RentalSettledConsumer rolls back when enqueue fails
        var (connStr, dbName) = await CreateMigratedTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connStr);
            await using (var db = new RentalServiceDbContext(options))
            {
                var testOrder = CreateCompletedOrderWithItem(orderId, itemId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now);
                var testItem = testOrder.Items.Single();
                testItem.SetStatus(RentalItemStatus.PendingSettlement);
                typeof(RentalItem).GetProperty("FinalDeductionAmount")!.SetValue(testItem, 0m);
                typeof(RentalItem).GetProperty("InventoryClosureConfirmedAt")!.SetValue(testItem, now);
                typeof(RentalItem).GetProperty("CompletedAt")!.SetValue(testItem, null); // will be completed by consumer

                string key = RentalOperationExecutor.ComputeOperationKey(RentalItem.SettleRentalAction, itemId.ToString("N"));
                var request = new SettleRentalRequest(testOrder.Id, testItem.Id, "INV-1", "PAY-1",
                    testItem.SupplierId, testItem.DepositAmount, 0m, testItem.DepositAmount,
                    "VND", Guid.NewGuid().ToString("D"), key);
                string receiptPayload = JsonSerializer.Serialize(new { providerRequestJson = JsonSerializer.Serialize(request) });
                testOrder.AddOperationReceipt(RentalOperationReceipt.CreatePending(Guid.NewGuid(), testOrder.Id, key,
                    RentalItem.SettleRentalAction, "fingerprint", now, targetItemId: testItem.Id, payloadJson: receiptPayload));

                db.RentalOrders.Add(testOrder);
                await db.SaveChangesAsync();
            }

            // Consumer with failing producer
            var failingProducer = new FakeRentalEventProducer
            {
                CompletedHandler = _ => Result.Failure<ProducerEnqueueResult>(
                    Error.Failure("ENQUEUE_FAILED", "Simulated outbox failure"))
            };
            await using (var db = new RentalServiceDbContext(options))
            {
                var orderRepo = new RentalOrderRepository(db);
                var consumer = new RentalSettledConsumer(failingProducer, TimeProvider.System);

                var loadedOrder = await orderRepo.GetByIdWithDetailsAsync(orderId);
                Assert.NotNull(loadedOrder);

                string key = RentalOperationExecutor.ComputeOperationKey(RentalItem.SettleRentalAction, itemId.ToString("N"));
                var payload = new RentalSettledPayload(
                    orderId, itemId, "INV-1", "PAY-1", "SETTLE-1", key, 0m, 500_000m, "VND", null, now);

                var envelope = new KafkaMessageEnvelope<RentalSettledPayload>(
                    Guid.NewGuid(), "RentalSettled", 1, "payment-service", orderId.ToString(), now, "corr", Guid.NewGuid(), payload);
                var inbox = new RentalInboxMessage(Guid.NewGuid(), envelope.MessageId, "RentalSettled", "payment-service",
                    orderId.ToString(), null, null, JsonSerializer.Serialize(envelope), now);

                var result = await consumer.ApplyRentalSettledStagedAsync(loadedOrder, envelope, inbox);
                Assert.True(result.IsFailure);
                Assert.Equal("ENQUEUE_FAILED", result.Error.Code);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    #region Helpers

    private static async Task<(string ConnectionString, string DatabaseName)> CreateMigratedTestDatabaseAsync()
    {
        var (connStr, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        await using (var migrate = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connStr)))
        {
            await migrate.Database.MigrateAsync();
        }
        return (connStr, dbName);
    }

    private static WebApplicationFactory<Program> CreateFactory(string connStr)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", connStr);
            builder.UseSetting("InternalAuthentication:ApiKey", ValidApiKey);
        });
    }

    private static RentalOrder CreateCompletedOrderWithItem(
        Guid orderId,
        Guid itemId,
        Guid renterId,
        Guid supplierId,
        Guid listingId,
        DateTimeOffset completedAt)
    {
        return CreateOrderWithStatus(orderId, itemId, renterId, supplierId, listingId, RentalItemStatus.Completed, completedAt);
    }

    private static RentalOrder CreateOrderWithStatus(
        Guid orderId,
        Guid itemId,
        Guid renterId,
        Guid supplierId,
        Guid listingId,
        RentalItemStatus status,
        DateTimeOffset? completedAt)
    {
        var now = DateTimeOffset.UtcNow;
        var assignments = new List<InitialRentalUnitAssignmentDto>
        {
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        };

        var itemDto = new InitialRentalItemDto(
            itemId,
            listingId,
            supplierId,
            1,
            DateOnly.FromDateTime(DateTime.UtcNow),
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)),
            "Camera Equipment",
            "Professional Camera Kit",
            "District 1, HCMC",
            200_000m,
            500_000m,
            $"group-{itemId:N}",
            assignments,
            TestListingSnapshots.Default);

        var order = RentalOrder.Create(
            orderId,
            $"ORD-{orderId.ToString("N")[..8]}",
            renterId,
            "VND",
            "Pickup",
            "Customer A",
            "0909123456",
            "123 Nguyen Hue",
            "Call before delivery",
            "bank-ref-001",
            "v1.0",
            "terms-doc-001",
            "sha256-hash-001",
            $"op-create-{orderId:N}",
            now,
            [itemDto]).Value;

        var item = order.Items.Single();
        item.SetStatus(status);
        item.SetCompletedAt(completedAt);

        return order;
    }

    #endregion
}
