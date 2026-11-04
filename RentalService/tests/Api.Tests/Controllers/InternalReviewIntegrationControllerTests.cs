using System.Net;
using System.Net.Http.Json;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Application.DTOs.Integration;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Controllers;

public sealed class InternalReviewIntegrationControllerTests
{
    private const string ValidApiKey = "dev-internal-api-key-dprental-2026";
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

    private static WebApplicationFactory<Program> CreateFactory(string dbName)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
            builder.UseSetting("InternalAuthentication:ApiKey", ValidApiKey);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<DbContextOptions<RentalServiceDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<RentalServiceDbContext>>();
                services.AddDbContext<RentalServiceDbContext>(options => options.UseInMemoryDatabase(dbName));
            });
        });
    }

    private static (RentalOrder Order, RentalItem Item) CreateOrderWithItem(
        Guid? specificOrderId = null,
        Guid? specificItemId = null,
        Guid? specificSupplierId = null,
        Guid? specificRenterId = null)
    {
        Guid orderId = specificOrderId ?? Guid.NewGuid();
        Guid renterId = specificRenterId ?? Guid.NewGuid();
        Guid itemId = specificItemId ?? Guid.NewGuid();
        Guid supplierId = specificSupplierId ?? Guid.NewGuid();
        Guid listingId = Guid.NewGuid();

        var assignments = new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) };
        var itemDto = new InitialRentalItemDto(
            itemId,
            listingId,
            supplierId,
            1,
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 5),
            "Camera Sony",
            null,
            "Pickup",
            100_000m,
            500_000m,
            $"grp-{Guid.NewGuid():N}",
            assignments,
            TestListingSnapshots.Default);

        var order = RentalOrder.Create(
            orderId,
            $"ORD-{Guid.NewGuid():N}"[..12],
            renterId,
            "VND",
            "Pickup",
            "Customer",
            "0909123456",
            "Address",
            null,
            "terms-1",
            "doc-1",
            "hash-1",
            "op-create",
            FixedNow,
            [itemDto]).Value;

        return (order, order.Items.First());
    }

    [Fact]
    public async Task GetSnapshot_MissingApiKey_Returns401()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateFactory(dbName);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/internal/orders/{Guid.NewGuid()}/items/{Guid.NewGuid()}/review-snapshot");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSnapshot_InvalidApiKey_Returns401()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateFactory(dbName);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key");

        using var response = await client.GetAsync($"/internal/orders/{Guid.NewGuid()}/items/{Guid.NewGuid()}/review-snapshot");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSnapshot_BearerToken_Returns403()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateFactory(dbName);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);
        client.DefaultRequestHeaders.Add("Authorization", "Bearer fake-jwt-token");

        using var response = await client.GetAsync($"/internal/orders/{Guid.NewGuid()}/items/{Guid.NewGuid()}/review-snapshot");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetSnapshot_OrderNotFound_Returns404()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateFactory(dbName);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

        using var response = await client.GetAsync($"/internal/orders/{Guid.NewGuid()}/items/{Guid.NewGuid()}/review-snapshot");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetSnapshot_ItemNotBelongingToOrder_Returns404()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateFactory(dbName);
        var (order, _) = CreateOrderWithItem();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            db.RentalOrders.Add(order);
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

        using var response = await client.GetAsync($"/internal/orders/{order.Id}/items/{Guid.NewGuid()}/review-snapshot");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetSnapshot_ItemNotCompleted_Returns400()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateFactory(dbName);
        var (order, item) = CreateOrderWithItem();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            db.RentalOrders.Add(order);
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

        using var response = await client.GetAsync($"/internal/orders/{order.Id}/items/{item.Id}/review-snapshot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains("COMPLETED", problem.Detail);
    }

    [Fact]
    public async Task GetSnapshot_ItemCompleted_ReturnsAuthoritativeSnapshot()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateFactory(dbName);
        var (order, item) = CreateOrderWithItem();

        DateTimeOffset returnsTime = FixedNow.AddDays(-2); // Return inspection time (MUST NOT be used as completedAt)
        DateTimeOffset settlementCompletedTime = FixedNow.AddDays(-1); // Authoritative completion time

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            item.SetReturnsCompleted(returnsTime, null, 0m);
            item.SetStatus(RentalItemStatus.Completed);

            db.RentalOrders.Add(order);
            db.RentalStateTransitions.Add(new RentalStateTransition(
                Guid.NewGuid(), order.Id, item.Id, "PendingSettlement", "Completed",
                "SettlementCompleted", null, ActorRole.System, "Completed", settlementCompletedTime, "op-settle"));

            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

        using var response = await client.GetAsync($"/internal/orders/{order.Id}/items/{item.Id}/review-snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var snapshot = await response.Content.ReadFromJsonAsync<RentalItemReviewSnapshotDto>();
        Assert.NotNull(snapshot);
        Assert.Equal(order.Id, snapshot.OrderId);
        Assert.Equal(item.Id, snapshot.ItemId);
        Assert.Equal(order.RenterId, snapshot.RenterId);
        Assert.Equal(item.SupplierId, snapshot.SupplierId);
        Assert.Equal("Completed", snapshot.Status);
        // Authoritative timestamp check: MUST equal settlementCompletedTime, NEVER returnsTime!
        Assert.Equal(settlementCompletedTime, snapshot.CompletedAt);
        Assert.NotEqual(returnsTime, snapshot.CompletedAt);
    }

    [Fact]
    public async Task GetReconciliationBatch_MissingApiKey_Returns401()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateFactory(dbName);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/internal/reconciliation/rental-items");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetReconciliationBatch_BearerToken_Returns403()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateFactory(dbName);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);
        client.DefaultRequestHeaders.Add("Authorization", "Bearer token");

        using var response = await client.GetAsync("/internal/reconciliation/rental-items");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetReconciliationBatch_KeysetPagination_FiltersAndPaginatesCorrectly()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateFactory(dbName);

        var (order1, item1) = CreateOrderWithItem();
        var (order2, item2) = CreateOrderWithItem();
        var (order3, item3) = CreateOrderWithItem();

        DateTimeOffset completedTime1 = DateTimeOffset.UtcNow.AddDays(-10);
        DateTimeOffset completedTime2 = DateTimeOffset.UtcNow.AddDays(-5);
        DateTimeOffset completedTime3 = DateTimeOffset.UtcNow.AddDays(-2);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();

            item1.SetStatus(RentalItemStatus.Completed);
            item2.SetStatus(RentalItemStatus.Completed);
            item3.SetStatus(RentalItemStatus.Completed);

            db.RentalOrders.AddRange(order1, order2, order3);
            db.RentalStateTransitions.AddRange(
                new RentalStateTransition(Guid.NewGuid(), order1.Id, item1.Id, "PendingSettlement", "Completed", "SettlementCompleted", null, ActorRole.System, "Completed", completedTime1, "op-1"),
                new RentalStateTransition(Guid.NewGuid(), order2.Id, item2.Id, "PendingSettlement", "Completed", "SettlementCompleted", null, ActorRole.System, "Completed", completedTime2, "op-2"),
                new RentalStateTransition(Guid.NewGuid(), order3.Id, item3.Id, "PendingSettlement", "Completed", "SettlementCompleted", null, ActorRole.System, "Completed", completedTime3, "op-3")
            );
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

        // Page 1 with limit = 2
        using var resp1 = await client.GetAsync("/internal/reconciliation/rental-items?limit=2");
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var batch1 = await resp1.Content.ReadFromJsonAsync<ReconciliationBatchDto>();
        Assert.NotNull(batch1);
        Assert.Equal(2, batch1.Items.Count);
        Assert.True(batch1.HasMore);
        Assert.NotNull(batch1.NextCursor);

        // Page 2 using NextCursor
        using var resp2 = await client.GetAsync($"/internal/reconciliation/rental-items?limit=2&cursor={Uri.EscapeDataString(batch1.NextCursor)}");
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var batch2 = await resp2.Content.ReadFromJsonAsync<ReconciliationBatchDto>();
        Assert.NotNull(batch2);
        Assert.Single(batch2.Items);
        Assert.False(batch2.HasMore);
    }

    [Fact]
    public async Task GetReviewSnapshot_MultipleTransitions_TakesFirstTransitionAsAuthoritativeCompletedAt()
    {
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        var (order, item) = CreateOrderWithItem();

        DateTimeOffset t1 = FixedNow.AddDays(-5);
        DateTimeOffset t2 = FixedNow.AddDays(-2); // Later replayed transition

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            item.SetStatus(RentalItemStatus.Completed);
            db.RentalOrders.Add(order);
            db.RentalStateTransitions.AddRange(
                new RentalStateTransition(Guid.NewGuid(), order.Id, item.Id, "PendingSettlement", "Completed", "SettlementCompleted", null, ActorRole.System, "Completed", t1, "op-1"),
                new RentalStateTransition(Guid.NewGuid(), order.Id, item.Id, "PendingSettlement", "Completed", "SettlementCompleted", null, ActorRole.System, "Completed", t2, "op-2")
            );
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

        using var response = await client.GetAsync($"/internal/orders/{order.Id}/items/{item.Id}/review-snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var snapshot = await response.Content.ReadFromJsonAsync<RentalItemReviewSnapshotDto>();
        Assert.NotNull(snapshot);
        Assert.Equal(t1, snapshot.CompletedAt); // Must take earliest (T1), never extending the review window
    }

    [Fact]
    public async Task GetReviewSnapshot_PaymentConfirmedWithoutCompletedTransition_ReturnsCompletedAtNull()
    {
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        var (order, item) = CreateOrderWithItem();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            item.SetStatus(RentalItemStatus.Completed);
            // Set FinancialClosureConfirmedAt but NO RentalStateTransition to Completed
            var prop = typeof(RentalItem).GetProperty(nameof(RentalItem.FinancialClosureConfirmedAt))!;
            prop.SetValue(item, FixedNow.AddDays(-3));

            db.RentalOrders.Add(order);
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

        using var response = await client.GetAsync($"/internal/orders/{order.Id}/items/{item.Id}/review-snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var snapshot = await response.Content.ReadFromJsonAsync<RentalItemReviewSnapshotDto>();
        Assert.NotNull(snapshot);
        Assert.Null(snapshot.CompletedAt); // Must not infer or backfill from payment
    }

    [Fact]
    public async Task GetReviewSnapshot_MultiItemOrder_EachItemHasOwnCompletedAt()
    {
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        var orderId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var itemId1 = Guid.NewGuid();
        var itemId2 = Guid.NewGuid();

        var assignments1 = new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) };
        var itemDto1 = new InitialRentalItemDto(itemId1, Guid.NewGuid(), Guid.NewGuid(), 1, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5), "Item 1", null, "Pickup", 100_000m, 500_000m, "grp1", assignments1, TestListingSnapshots.Default);
        var assignments2 = new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) };
        var itemDto2 = new InitialRentalItemDto(itemId2, Guid.NewGuid(), Guid.NewGuid(), 1, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5), "Item 2", null, "Pickup", 150_000m, 600_000m, "grp2", assignments2, TestListingSnapshots.Default);

        var order = RentalOrder.Create(orderId, "ORD-MULTI-1", renterId, "VND", "Pickup", "Customer", "0909123456", "Address", null, "terms-1", "doc-1", "hash-1", "op-create", FixedNow, [itemDto1, itemDto2]).Value;
        var item1 = order.Items.First(i => i.Id == itemId1);
        var item2 = order.Items.First(i => i.Id == itemId2);

        DateTimeOffset t1 = FixedNow.AddDays(-10);
        DateTimeOffset t2 = FixedNow.AddDays(-3);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            item1.SetStatus(RentalItemStatus.Completed);
            item2.SetStatus(RentalItemStatus.Completed);
            db.RentalOrders.Add(order);
            db.RentalStateTransitions.AddRange(
                new RentalStateTransition(Guid.NewGuid(), orderId, item1.Id, "PendingSettlement", "Completed", "SettlementCompleted", null, ActorRole.System, "Completed", t1, "op-item1"),
                new RentalStateTransition(Guid.NewGuid(), orderId, item2.Id, "PendingSettlement", "Completed", "SettlementCompleted", null, ActorRole.System, "Completed", t2, "op-item2")
            );
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

        using var resp1 = await client.GetAsync($"/internal/orders/{orderId}/items/{item1.Id}/review-snapshot");
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var snap1 = await resp1.Content.ReadFromJsonAsync<RentalItemReviewSnapshotDto>();
        Assert.NotNull(snap1);
        Assert.Equal(t1, snap1.CompletedAt);

        using var resp2 = await client.GetAsync($"/internal/orders/{orderId}/items/{item2.Id}/review-snapshot");
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var snap2 = await resp2.Content.ReadFromJsonAsync<RentalItemReviewSnapshotDto>();
        Assert.NotNull(snap2);
        Assert.Equal(t2, snap2.CompletedAt);
    }
}
