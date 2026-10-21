using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dprental.RentalService.Api.Tests.TestHelpers;
using System.Security.Claims;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Security.Jwt;
using Dprental.RentalService.Application.IServices.SupplierDecision;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
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

namespace Dprental.RentalService.Api.Tests.Controllers;

public sealed class RentalItemsControllerIntegrationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 8, 15, 30, TimeSpan.Zero);

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public static Claim[] Claims { get; set; } = [];

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            System.Text.Encodings.Web.UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Claims.Length == 0)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(Claims, "TestAuth");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "TestScheme");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class StubInventoryClient : IInventoryRentalClient
    {
        public Func<string, ConfirmAllocationRequest, Task<Result<ConfirmAllocationResponse>>>? ConfirmHandler { get; set; }

        public Task<Result<ConfirmAllocationResponse>> ConfirmAllocationAsync(string allocationGroupId, ConfirmAllocationRequest request, CancellationToken ct = default) =>
            ConfirmHandler != null
                ? ConfirmHandler(allocationGroupId, request)
                : Task.FromResult(Result.Success(new ConfirmAllocationResponse("REC-CONF", allocationGroupId, FixedNow, false, [])));

        public static Task<Result<ReleaseAllocationResponse>> ReleaseAllocationAsync(string allocationGroupId, ReleaseAllocationRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new ReleaseAllocationResponse("REC-REL", request.OperationKey, allocationGroupId, FixedNow, false)));

        public Task<Result<ListingRentalSnapshotsResponse>> GetListingRentalSnapshotsAsync(ListingRentalSnapshotsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(ListingAvailabilityCheckRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CreateHoldResponse>> CreateHoldAsync(CreateHoldRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public static Task<Result<ExtendHoldResponse>> ExtendHoldAsync(string allocationGroupId, ExtendHoldRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CancelAllocationResponse>> CancelAllocationAsync(string allocationGroupId, CancelAllocationRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<MarkAssetsOverdueResponse>> MarkAssetsOverdueAsync(string allocationGroupId, MarkAssetsOverdueRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<MarkAssetsReturnedResponse>> MarkAssetsReturnedAsync(string allocationGroupId, MarkAssetsReturnedRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<BlockDamagedAssetsResponse>> BlockDamagedAssetsAsync(string allocationGroupId, BlockDamagedAssetsRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubPaymentClient : IPaymentRentalClient
    {
        public Task<Result<RequestRefundResponse>> RequestRefundAsync(RequestRefundRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new RequestRefundResponse("REF-001", request.OperationKey, "Accepted", request.TotalRefundAmount, "VND", false)));

        public Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(CreateInvoiceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CancelInvoiceResponse>> CancelInvoiceAsync(CancelInvoiceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<SettleRentalResponse>> SettleRentalAsync(SettleRentalRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static (RentalOrder Order, RentalItem Item) CreatePaidOrderWithItem(Guid supplierId)
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateOnly startDate = DateOnly.FromDateTime(now.UtcDateTime).AddDays(5);
        DateOnly endDate = startDate.AddDays(3);

        var assignments = new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) };
        var item = new InitialRentalItemDto(
            itemId,
            Guid.NewGuid(),
            supplierId,
            1,
            startDate,
            endDate,
            "Camera Sony",
            null,
            "Pickup",
            200_000m,
            500_000m,
            "grp-01",
            assignments,
            TestListingSnapshots.Default);

        var order = RentalOrder.Create(
            orderId,
            "ORD-CTRL-001",
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
            now,
            [item]).Value;

        order.MarkAsPaid("INV-001", "PAY-001", now, now);

        string holdOpKey = RentalOperationExecutor.ComputeOperationKey("ExtendHold", $"{order.Id}:{order.Items.First().Id}");
        var holdReceipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            holdOpKey,
            "ExtendHold",
            "hash-hold",
            isSuccess: true,
            errorCode: null,
            errorMessage: null,
            createdAt: now,
            status: RentalOperationReceipt.StatusSucceeded,
            targetItemId: order.Items.First().Id);
        order.AddOperationReceipt(holdReceipt);
        return (order, order.Items.First());
    }

    [Fact]
    public async Task Accept_WithoutBearerToken_Returns401()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<RentalServiceDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<RentalServiceDbContext>>();
                    services.AddDbContext<RentalServiceDbContext>(options =>
                        options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
                });
            });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-internal-api-key-dprental-2026");
        var response = await client.PostAsync($"/api/v1/rental/supplier/sub-orders/{Guid.NewGuid()}/accept", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Accept_WithoutIdempotencyKey_Returns400()
    {
        Guid supplierId = Guid.NewGuid();
        string dbName = Guid.NewGuid().ToString("N");

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<RentalServiceDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<RentalServiceDbContext>>();
                    services.AddDbContext<RentalServiceDbContext>(options => options.UseInMemoryDatabase(dbName));
                    services.RemoveAll<Dprental.RentalService.Domain.IRepositories.IOrderLockManager>();
                    services.AddSingleton<Dprental.RentalService.Domain.IRepositories.IOrderLockManager, FakeOrderLockManager>();
                    services.AddAuthentication("TestScheme")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestScheme", _ => { });

                    TestAuthHandler.Claims =
                    [
                        new Claim(RentalAuthClaims.Subject, supplierId.ToString("D")),
                        new Claim(RentalAuthClaims.ProfileCompleted, "true"),
                        new Claim(RentalAuthClaims.Roles, "owner")
                    ];
                });
            });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-internal-api-key-dprental-2026");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("TestScheme");
        // No Idempotency-Key header
        var response = await client.PostAsync($"/api/v1/rental/supplier/sub-orders/{Guid.NewGuid()}/accept", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Accept_WithValidTokenAndKey_Returns202Accepted()
    {
        Guid supplierId = Guid.NewGuid();
        string dbName = Guid.NewGuid().ToString("N");
        var (order, item) = CreatePaidOrderWithItem(supplierId);

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<RentalServiceDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<RentalServiceDbContext>>();
                    services.AddDbContext<RentalServiceDbContext>(options => options.UseInMemoryDatabase(dbName));
                    services.RemoveAll<Dprental.RentalService.Domain.IRepositories.IOrderLockManager>();
                    services.AddSingleton<Dprental.RentalService.Domain.IRepositories.IOrderLockManager, FakeOrderLockManager>();
                    services.AddSingleton<IInventoryRentalClient>(new StubInventoryClient());

                    services.RemoveAll<IPaymentRentalClient>();
                    services.AddSingleton<IPaymentRentalClient>(new StubPaymentClient());

                    services.AddAuthentication("TestScheme")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestScheme", _ => { });

                    TestAuthHandler.Claims =
                    [
                        new Claim(RentalAuthClaims.Subject, supplierId.ToString("D")),
                        new Claim(RentalAuthClaims.ProfileCompleted, "true"),
                        new Claim(RentalAuthClaims.Roles, "owner")
                    ];
                });
            });

        // Seed DB
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            db.RentalOrders.Add(order);
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-internal-api-key-dprental-2026");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("TestScheme");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-ctrl-accept-1");

        var response = await client.PostAsync($"/api/v1/rental/supplier/sub-orders/{item.RentalSubOrderId}/accept", null);

        // Nhận yêu cầu và gửi lệnh ConfirmAllocation cho Inventory; mục thuê chờ Inventory trả lời.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SubOrderDecisionResponse>();
        Assert.NotNull(body);
        Assert.Equal("PendingSupplierConfirmation", body.Status);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            Assert.Equal(1, await db.RentalOutboxMessages.CountAsync(m => m.MessageType == "ConfirmAllocation"));
        }
    }

    [Fact]
    public async Task Reject_WithValidReason_Returns200OK()
    {
        Guid supplierId = Guid.NewGuid();
        string dbName = Guid.NewGuid().ToString("N");
        var (order, item) = CreatePaidOrderWithItem(supplierId);

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<RentalServiceDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<RentalServiceDbContext>>();
                    services.AddDbContext<RentalServiceDbContext>(options => options.UseInMemoryDatabase(dbName));
                    services.RemoveAll<Dprental.RentalService.Domain.IRepositories.IOrderLockManager>();
                    services.AddSingleton<Dprental.RentalService.Domain.IRepositories.IOrderLockManager, FakeOrderLockManager>();
                    services.RemoveAll<IInventoryRentalClient>();
                    services.AddSingleton<IInventoryRentalClient>(new StubInventoryClient());

                    services.RemoveAll<IPaymentRentalClient>();
                    services.AddSingleton<IPaymentRentalClient>(new StubPaymentClient());

                    services.AddAuthentication("TestScheme")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestScheme", _ => { });

                    TestAuthHandler.Claims =
                    [
                        new Claim(RentalAuthClaims.Subject, supplierId.ToString("D")),
                        new Claim(RentalAuthClaims.ProfileCompleted, "true"),
                        new Claim(RentalAuthClaims.Roles, "owner")
                    ];
                });
            });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            db.RentalOrders.Add(order);
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-internal-api-key-dprental-2026");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("TestScheme");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-ctrl-rej-1");

        var req = new SupplierRejectRequest("Equipment is broken", "Will be repaired next week");
        var response = await client.PostAsJsonAsync($"/api/v1/rental/supplier/sub-orders/{item.RentalSubOrderId}/reject", req);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SubOrderDecisionResponse>();
        Assert.NotNull(body);
        Assert.Equal("Rejected", body.Status);
        Assert.Equal("Equipment is broken", Assert.Single(body.Items).DecisionReason);
    }
}
