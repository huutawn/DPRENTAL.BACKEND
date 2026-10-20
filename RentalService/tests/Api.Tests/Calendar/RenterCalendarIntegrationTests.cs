using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Calendar;
using Dprental.RentalService.Application.IServices.Calendar;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Security.Jwt;
using Dprental.RentalService.Application.Services.Calendar;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Dprental.RentalService.Api.Tests.Domain;

namespace Dprental.RentalService.Api.Tests.Calendar;

public sealed class RenterCalendarIntegrationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);

    private static RentalServiceDbContext CreateInMemoryDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new RentalServiceDbContext(options);
    }

    private static RentalCalendarService CreateCalendarService(RentalServiceDbContext db) =>
        new(new RentalOrderRepository(db), new TestTimeProvider(FixedNow));

    private static RentalOrder CreateOrderWithItem(
        Guid renterId,
        string orderCode,
        RentalItemStatus itemStatus,
        DateOnly expectedStartDate,
        DateOnly expectedEndDate,
        int quantity = 2,
        string title = "Sony A7 IV")
    {
        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();

        var assignments = Enumerable.Range(0, quantity)
            .Select(_ => new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))
            .ToList();

        InitialRentalItemDto item = new(
            itemId,
            Guid.NewGuid(),
            supplierId,
            quantity,
            expectedStartDate,
            expectedEndDate,
            title,
            "Description",
            "Pickup",
            200_000m,
            500_000m,
            "grp-01",
            assignments,
            TestListingSnapshots.Default);
        var order = RentalOrder.Create(
            orderId,
            orderCode,
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
            $"op-{Guid.NewGuid():N}",
            FixedNow,
            [item]).Value;

        // Set the item status via reflection for test seeding
        var itemEntity = order.Items.First();
        typeof(RentalItem).GetProperty(nameof(RentalItem.Status))?.SetValue(itemEntity, itemStatus);

        return order;
    }

    [Fact]
    public async Task GetRenterCalendar_Unauthenticated_ReturnsUnauthorized()
    {
        await using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString("N"));
        var service = CreateCalendarService(db);

        var result = await service.GetRenterCalendarAsync("2026-10", 1, 50, RentalCallerContext.Anonymous);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.Unauthorized", result.Error.Code);
    }

    [Fact]
    public async Task GetRenterCalendar_ServiceCaller_ReturnsForbidden()
    {
        await using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString("N"));
        var service = CreateCalendarService(db);

        var serviceCaller = CreateServiceCaller("payment-worker");
        var result = await service.GetRenterCalendarAsync("2026-10", 1, 50, serviceCaller);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.Forbidden", result.Error.Code);
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("2026-00")]
    [InlineData("invalid")]
    [InlineData("2026-1")]
    [InlineData("2026-10-15")]
    public async Task GetRenterCalendar_InvalidMonthFormat_ReturnsValidationError(string invalidMonth)
    {
        await using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString("N"));
        var service = CreateCalendarService(db);

        var caller = CreateUserCaller(Guid.NewGuid());
        var result = await service.GetRenterCalendarAsync(invalidMonth, 1, 50, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_MONTH_FORMAT", result.Error.Code);
    }

    [Theory]
    [InlineData("2026-02", 28)]
    [InlineData("2024-02", 29)]
    [InlineData("2026-04", 30)]
    [InlineData("2026-10", 31)]
    public async Task GetRenterCalendar_DifferentMonthLengths_ReturnsCorrectNumberOfDays(string month, int expectedDays)
    {
        await using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString("N"));
        var service = CreateCalendarService(db);

        var caller = CreateUserCaller(Guid.NewGuid());
        var result = await service.GetRenterCalendarAsync(month, 1, 50, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedDays, result.Value.Days.Count);
        Assert.Equal(1, result.Value.Days[0].Date.Day);
        Assert.Equal(expectedDays, result.Value.Days[^1].Date.Day);
    }

    [Fact]
    public async Task GetRenterCalendar_DefaultMonth_UsesVietnamCurrentMonth()
    {
        await using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString("N"));
        var service = CreateCalendarService(db);

        var caller = CreateUserCaller(Guid.NewGuid());
        var result = await service.GetRenterCalendarAsync(null, 1, 50, caller);

        Assert.True(result.IsSuccess);
        // FixedNow is 2026-10-08 in UTC+7 (Vietnam) -> "2026-10"
        Assert.Equal("2026-10", result.Value.Month);
        Assert.Equal(31, result.Value.Days.Count);
    }

    [Fact]
    public async Task GetRenterCalendar_FiltersByStatus_AndProtectsFromIdor_AndConvertsEndInclusive()
    {
        string dbName = Guid.NewGuid().ToString("N");
        Guid renterId = Guid.NewGuid();
        Guid strangerId = Guid.NewGuid();

        await using (var db = CreateInMemoryDbContext(dbName))
        {
            // 1. Included statuses: AwaitingHandover, InRental, PendingSettlement, Completed
            // Rental stores [ExpectedStartDate, ExpectedEndDate) end-exclusive:
            // [2026-10-10, 2026-10-13) means rental is on days 10, 11, 12! Inclusive end is 2026-10-12.
            var order1 = CreateOrderWithItem(renterId, "ORD-001", RentalItemStatus.AwaitingHandover,
                new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 13), quantity: 2, title: "Lens 24-70mm");

            var order2 = CreateOrderWithItem(renterId, "ORD-002", RentalItemStatus.InRental,
                new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 15), quantity: 1, title: "Sony FX3");

            var order3 = CreateOrderWithItem(renterId, "ORD-003", RentalItemStatus.Completed,
                new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18), quantity: 3, title: "Tripod Manfrotto");

            // 2. Excluded statuses: PendingPayment, PendingSupplierConfirmation, Rejected, Expired, Cancelled
            var orderExcluded1 = CreateOrderWithItem(renterId, "ORD-EXC-1", RentalItemStatus.PendingPayment,
                new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 15), quantity: 5);

            var orderExcluded2 = CreateOrderWithItem(renterId, "ORD-EXC-2", RentalItemStatus.Cancelled,
                new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 15), quantity: 5);

            // 3. Stranger's order (IDOR attempt): must NOT be returned even for Admin!
            var strangerOrder = CreateOrderWithItem(strangerId, "ORD-STRANGER", RentalItemStatus.InRental,
                new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 15), quantity: 10);

            db.RentalOrders.AddRange(order1, order2, order3, orderExcluded1, orderExcluded2, strangerOrder);
            await db.SaveChangesAsync();
        }

        await using (var db = CreateInMemoryDbContext(dbName))
        {
            var service = CreateCalendarService(db);
            var caller = CreateUserCaller(renterId, roles: ["customer"]);

            var result = await service.GetRenterCalendarAsync("2026-10", 1, 50, caller);

            Assert.True(result.IsSuccess);
            var response = result.Value;

            // Entries: only order1, order2, order3 match (total 3 entries)
            Assert.Equal(3, response.TotalCount);
            Assert.Equal(3, response.Entries.Count);

            // Check Order 1: [2026-10-10, 2026-10-13) in Rental -> converted to inclusive [2026-10-10, 2026-10-12]
            var entry1 = response.Entries.Single(e => e.OrderCode == "ORD-001");
            Assert.Equal(new DateOnly(2026, 10, 10), entry1.StartDate);
            Assert.Equal(new DateOnly(2026, 10, 12), entry1.EndDate);
            Assert.Equal("AwaitingHandover", entry1.Status);
            Assert.Equal(2, entry1.Quantity);
            Assert.Equal("Lens 24-70mm", entry1.ListingTitle);
            Assert.Equal("Lens 24-70mm", entry1.ListingTitleSnapshot);

            // Check Order 2: [2026-10-12, 2026-10-15) -> inclusive [2026-10-12, 2026-10-14]
            var entry2 = response.Entries.Single(e => e.OrderCode == "ORD-002");
            Assert.Equal(new DateOnly(2026, 10, 12), entry2.StartDate);
            Assert.Equal(new DateOnly(2026, 10, 14), entry2.EndDate);
            Assert.Equal("InRental", entry2.Status);

            // Daily quantities:
            // Day 2026-10-09: 0 (no items)
            var day9 = response.Days.Single(d => d.Date == new DateOnly(2026, 10, 9));
            Assert.Equal(0, day9.RentedQuantity);

            // Day 2026-10-10: Item 1 active (qty 2) -> 2
            var day10 = response.Days.Single(d => d.Date == new DateOnly(2026, 10, 10));
            Assert.Equal(2, day10.RentedQuantity);

            // Day 2026-10-11: Item 1 active (qty 2) -> 2
            var day11 = response.Days.Single(d => d.Date == new DateOnly(2026, 10, 11));
            Assert.Equal(2, day11.RentedQuantity);

            // Day 2026-10-12: Item 1 active (qty 2) + Item 2 active (qty 1) -> 3
            var day12 = response.Days.Single(d => d.Date == new DateOnly(2026, 10, 12));
            Assert.Equal(3, day12.RentedQuantity);

            // Day 2026-10-13: Item 1 expired (end-exclusive 10-13). Item 2 active (qty 1) -> 1
            var day13 = response.Days.Single(d => d.Date == new DateOnly(2026, 10, 13));
            Assert.Equal(1, day13.RentedQuantity);
        }
    }

    [Fact]
    public async Task GetRenterCalendar_Pagination_DoesNotAffectDailyBreakdown()
    {
        string dbName = Guid.NewGuid().ToString("N");
        Guid renterId = Guid.NewGuid();

        await using (var db = CreateInMemoryDbContext(dbName))
        {
            // Seed 5 items
            for (int i = 1; i <= 5; i++)
            {
                var order = CreateOrderWithItem(renterId, $"ORD-{i:D3}", RentalItemStatus.InRental,
                    new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 15), quantity: 1, title: $"Item {i}");
                db.RentalOrders.Add(order);
            }
            await db.SaveChangesAsync();
        }

        await using (var db = CreateInMemoryDbContext(dbName))
        {
            var service = CreateCalendarService(db);
            var caller = CreateUserCaller(renterId);

            // Page 1 with pageSize = 2
            var p1 = await service.GetRenterCalendarAsync("2026-10", page: 1, pageSize: 2, caller);
            Assert.True(p1.IsSuccess);
            Assert.Equal(5, p1.Value.TotalCount);
            Assert.Equal(3, p1.Value.TotalPages);
            Assert.Equal(2, p1.Value.Entries.Count);

            // Daily breakdown on day 10 must sum ALL 5 items (qty = 5) even though page only has 2 items!
            var day10 = p1.Value.Days.Single(d => d.Date == new DateOnly(2026, 10, 10));
            Assert.Equal(5, day10.RentedQuantity);

            // Page 2 with pageSize = 2
            var p2 = await service.GetRenterCalendarAsync("2026-10", page: 2, pageSize: 2, caller);
            Assert.True(p2.IsSuccess);
            Assert.Equal(2, p2.Value.Entries.Count);
            var day10p2 = p2.Value.Days.Single(d => d.Date == new DateOnly(2026, 10, 10));
            Assert.Equal(5, day10p2.RentedQuantity);
        }
    }

    [Fact]
    public async Task GetRenterCalendar_Admin_OnlyViewsOwnCalendar_CannotSeeOthers()
    {
        string dbName = Guid.NewGuid().ToString("N");
        Guid adminId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();

        await using (var db = CreateInMemoryDbContext(dbName))
        {
            var adminOrder = CreateOrderWithItem(adminId, "ORD-ADMIN", RentalItemStatus.InRental,
                new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 15), quantity: 1, title: "Admin Item");

            var otherOrder = CreateOrderWithItem(otherUserId, "ORD-OTHER", RentalItemStatus.InRental,
                new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 15), quantity: 3, title: "Other Item");

            db.RentalOrders.AddRange(adminOrder, otherOrder);
            await db.SaveChangesAsync();
        }

        await using (var db = CreateInMemoryDbContext(dbName))
        {
            var service = CreateCalendarService(db);
            // Admin caller
            var adminCaller = CreateUserCaller(adminId, roles: ["admin", "adm"]);

            var result = await service.GetRenterCalendarAsync("2026-10", 1, 50, adminCaller);

            Assert.True(result.IsSuccess);
            // Admin only sees their own order, NOT otherUser's order!
            Assert.Equal(1, result.Value.TotalCount);
            Assert.Equal("ORD-ADMIN", result.Value.Entries[0].OrderCode);
            var day10 = result.Value.Days.Single(d => d.Date == new DateOnly(2026, 10, 10));
            Assert.Equal(1, day10.RentedQuantity);
        }
    }

    [Fact]
    public async Task Controller_HttpGet_WithBearerToken_Returns200AndRenterCalendar()
    {
        string dbName = Guid.NewGuid().ToString("N");
        Guid renterId = Guid.NewGuid();

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Authentication:Jwt:Issuer", "dprental-authservice-dev");
                builder.UseSetting("Authentication:Jwt:Audience", "dprental-clients");
                builder.UseSetting("Authentication:Jwt:JwksUri", "http://auth-service:8080/.well-known/jwks.json");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<RentalServiceDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<RentalServiceDbContext>>();
                    services.AddDbContext<RentalServiceDbContext>(options =>
                        options.UseInMemoryDatabase(dbName));
                    services.RemoveAll<IRentalCallerContext>();
                    services.AddScoped<IRentalCallerContext>(_ => CreateUserCaller(renterId));

                    // Add dummy auth handler for testing
                    services.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                });
            });

        // Seed data
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            var order = CreateOrderWithItem(renterId, "ORD-HTTP-001", RentalItemStatus.InRental,
                new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12), quantity: 1, title: "Test Lens");
            db.RentalOrders.Add(order);
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-internal-api-key-dprental-2026");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Test");

        var response = await client.GetAsync("/api/v1/rental/me/calendar?month=2026-10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var calendar = await response.Content.ReadFromJsonAsync<RenterCalendarResponse>();
        Assert.NotNull(calendar);
        Assert.Equal("2026-10", calendar.Month);
        Assert.Equal(31, calendar.Days.Count);
        Assert.Single(calendar.Entries);
        Assert.Equal("ORD-HTTP-001", calendar.Entries[0].OrderCode);
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            System.Text.Encodings.Web.UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim("sub", Guid.NewGuid().ToString("D")),
                new Claim("r", "customer")
            };
            var identity = new ClaimsIdentity(claims, "Test");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;
        public TestTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private static RentalCallerContext CreateUserCaller(Guid userId, IEnumerable<string>? roles = null)
    {
        List<KeyValuePair<string, string>> claims =
        [
            new(RentalAuthClaims.Subject, userId.ToString("D")),
            new(RentalAuthClaims.ProfileCompleted, "true")
        ];
        if (roles is not null)
        {
            claims.Add(new(RentalAuthClaims.Roles, string.Join(",", roles)));
        }
        return RentalCallerContext.FromClaims(claims);
    }

    private static RentalCallerContext CreateServiceCaller(string serviceName)
    {
        List<KeyValuePair<string, string>> claims =
        [
            new(RentalAuthClaims.Subject, Guid.NewGuid().ToString("D")),
            new(RentalAuthClaims.UserTypeOrAlias, RentalAuthClaims.ServiceIndicator),
            new(RentalAuthClaims.Roles, "service")
        ];
        return RentalCallerContext.FromClaims(claims);
    }
}
