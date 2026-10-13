using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.DTOs.Operations;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Domain.Repositories;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Security.Jwt;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
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

namespace Dprental.RentalService.Api.Tests.Operations;

public sealed class AdminReplayIntegrationTests
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

    private static (RentalOrder Order, RentalOperationReceipt Receipt) SeedOrderWithReceipt(
        RentalServiceDbContext db,
        string operationId,
        string status,
        string? errorClassification = null,
        bool isOutcomeUncertain = false,
        string? payloadJson = "{\"version\":1,\"action\":\"ExtendHold\",\"providerRequestJson\":\"{}\"}")
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        var assignments = new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) };
        var item = new InitialRentalItemDto(
            itemId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            new DateOnly(2026, 10, 15),
            new DateOnly(2026, 10, 18),
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
            $"ORD-REP-{Guid.NewGuid():N}"[..12],
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
            [item]).Value;

        var receipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            operationId,
            "ExtendHold",
            "fingerprint-1",
            isSuccess: status == RentalOperationReceipt.StatusSucceeded,
            errorCode: status == RentalOperationReceipt.StatusFailed ? "TIMEOUT" : null,
            errorMessage: status == RentalOperationReceipt.StatusFailed ? "Timeout calling provider" : null,
            createdAt: FixedNow,
            status: status,
            retryCount: status == RentalOperationReceipt.StatusFailed ? 10 : 1,
            targetItemId: itemId,
            payloadJson: payloadJson,
            errorClassification: errorClassification,
            isOutcomeUncertain: isOutcomeUncertain);

        order.AddOperationReceipt(receipt);
        db.RentalOrders.Add(order);
        db.SaveChanges();

        return (order, receipt);
    }

    private static WebApplicationFactory<Program> CreateTestFactory(string dbName)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<RentalServiceDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<RentalServiceDbContext>>();
                    services.AddDbContext<RentalServiceDbContext>(options => options.UseInMemoryDatabase(dbName));
                    services.RemoveAll<IOrderLockManager>();
                    services.AddSingleton<IOrderLockManager, FakeOrderLockManager>();
                    services.AddAuthentication("TestScheme")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestScheme", _ => { });
                });
            });
    }

    [Fact]
    public async Task AdminReplay_AnonymousCaller_Returns401Unauthorized()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateTestFactory(dbName);
        TestAuthHandler.Claims = [];

        using var client = factory.CreateClient();
        Guid orderId = Guid.NewGuid();
        string opId = "op-1";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/rental/internal/orders/{orderId}/operations/{opId}/replay")
        {
            Content = JsonContent.Create(new AdminReplayRequest("Operational intervention needed"))
        };
        request.Headers.Add("Idempotency-Key", "idemp-anon");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminReplay_ServiceCaller_Returns403Forbidden()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateTestFactory(dbName);
        Guid serviceId = Guid.NewGuid();
        TestAuthHandler.Claims =
        [
            new Claim(RentalAuthClaims.Subject, serviceId.ToString("D")),
            new Claim(RentalAuthClaims.UserTypeOrAlias, RentalAuthClaims.ServiceIndicator),
            new Claim(RentalAuthClaims.Roles, "service")
        ];

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("TestScheme");

        Guid orderId = Guid.NewGuid();
        string opId = "op-1";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/rental/internal/orders/{orderId}/operations/{opId}/replay")
        {
            Content = JsonContent.Create(new AdminReplayRequest("Service attempting replay"))
        };
        request.Headers.Add("Idempotency-Key", "idemp-svc");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminReplay_CustomerCaller_Returns403Forbidden()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateTestFactory(dbName);
        Guid customerId = Guid.NewGuid();
        TestAuthHandler.Claims =
        [
            new Claim(RentalAuthClaims.Subject, customerId.ToString("D")),
            new Claim(RentalAuthClaims.ProfileCompleted, "true"),
            new Claim(RentalAuthClaims.Roles, "customer,owner")
        ];

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("TestScheme");

        Guid orderId = Guid.NewGuid();
        string opId = "op-1";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/rental/internal/orders/{orderId}/operations/{opId}/replay")
        {
            Content = JsonContent.Create(new AdminReplayRequest("Customer attempting replay"))
        };
        request.Headers.Add("Idempotency-Key", "idemp-cust");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminReplay_MissingIdempotencyKey_Returns400BadRequest()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateTestFactory(dbName);
        Guid adminId = Guid.NewGuid();
        TestAuthHandler.Claims =
        [
            new Claim(RentalAuthClaims.Subject, adminId.ToString("D")),
            new Claim(RentalAuthClaims.ProfileCompleted, "true"),
            new Claim(RentalAuthClaims.Roles, "admin")
        ];

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("TestScheme");

        Guid orderId = Guid.NewGuid();
        string opId = "op-1";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/rental/internal/orders/{orderId}/operations/{opId}/replay")
        {
            Content = JsonContent.Create(new AdminReplayRequest("Replay without idempotency key"))
        };

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminReplay_MissingReason_Returns400BadRequest()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateTestFactory(dbName);
        Guid adminId = Guid.NewGuid();
        TestAuthHandler.Claims =
        [
            new Claim(RentalAuthClaims.Subject, adminId.ToString("D")),
            new Claim(RentalAuthClaims.ProfileCompleted, "true"),
            new Claim(RentalAuthClaims.Roles, "admin")
        ];

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("TestScheme");

        Guid orderId = Guid.NewGuid();
        string opId = "op-1";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/rental/internal/orders/{orderId}/operations/{opId}/replay")
        {
            Content = JsonContent.Create(new AdminReplayRequest("   "))
        };
        request.Headers.Add("Idempotency-Key", "idemp-missing-reason");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminReplay_AlreadySucceeded_Returns409Conflict()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateTestFactory(dbName);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            SeedOrderWithReceipt(db, "op-succeeded-1", RentalOperationReceipt.StatusSucceeded);
        }

        using var scopeForOrder = factory.Services.CreateScope();
        var orderDb = scopeForOrder.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
        var order = await orderDb.RentalOrders.FirstAsync();

        Guid adminId = Guid.NewGuid();
        TestAuthHandler.Claims =
        [
            new Claim(RentalAuthClaims.Subject, adminId.ToString("D")),
            new Claim(RentalAuthClaims.ProfileCompleted, "true"),
            new Claim(RentalAuthClaims.Roles, "admin")
        ];

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("TestScheme");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/rental/internal/orders/{order.Id}/operations/op-succeeded-1/replay")
        {
            Content = JsonContent.Create(new AdminReplayRequest("Replay already succeeded"))
        };
        request.Headers.Add("Idempotency-Key", "idemp-succ");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AdminReplay_CorruptedJournalPayload_Returns422Unprocessable()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateTestFactory(dbName);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            SeedOrderWithReceipt(db, "op-corrupt-1", RentalOperationReceipt.StatusFailed, payloadJson: "{ invalid json");
        }

        using var scopeForOrder = factory.Services.CreateScope();
        var orderDb = scopeForOrder.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
        var order = await orderDb.RentalOrders.FirstAsync();

        Guid adminId = Guid.NewGuid();
        TestAuthHandler.Claims =
        [
            new Claim(RentalAuthClaims.Subject, adminId.ToString("D")),
            new Claim(RentalAuthClaims.ProfileCompleted, "true"),
            new Claim(RentalAuthClaims.Roles, "admin")
        ];

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("TestScheme");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/rental/internal/orders/{order.Id}/operations/op-corrupt-1/replay")
        {
            Content = JsonContent.Create(new AdminReplayRequest("Replay corrupt journal"))
        };
        request.Headers.Add("Idempotency-Key", "idemp-corrupt");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AdminReplay_ValidRequest_ResetsBudget_PreservesUncertainty_Returns202Accepted()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using var factory = CreateTestFactory(dbName);
        string opId = "op-retry-exhausted";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            SeedOrderWithReceipt(
                db,
                opId,
                RentalOperationReceipt.StatusFailed,
                errorClassification: RentalOperationReceipt.ClassificationUncertainExhausted,
                isOutcomeUncertain: true);
        }

        using var scopeForOrder = factory.Services.CreateScope();
        var orderDb = scopeForOrder.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
        var order = await orderDb.RentalOrders.FirstAsync();

        Guid adminId = Guid.NewGuid();
        TestAuthHandler.Claims =
        [
            new Claim(RentalAuthClaims.Subject, adminId.ToString("D")),
            new Claim(RentalAuthClaims.ProfileCompleted, "true"),
            new Claim(RentalAuthClaims.Roles, "admin")
        ];

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("TestScheme");

        string reason = "Confirmed upstream network restored; manual replay authorized";
        string idempKey = "idemp-replay-valid-1";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/rental/internal/orders/{order.Id}/operations/{opId}/replay")
        {
            Content = JsonContent.Create(new AdminReplayRequest(reason))
        };
        request.Headers.Add("Idempotency-Key", idempKey);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var responseBody = await response.Content.ReadFromJsonAsync<AdminReplayResponse>();
        Assert.NotNull(responseBody);
        Assert.Equal(order.Id, responseBody.OrderId);
        Assert.Equal(opId, responseBody.OperationId);
        Assert.Equal("Pending", responseBody.Status);
        Assert.Equal(0, responseBody.RetryCount);

        // Verify in database:
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
        var reloadedOrder = await verifyDb.RentalOrders
            .Include(o => o.OperationReceipts)
            .Include(o => o.StateTransitions)
            .FirstAsync(o => o.Id == order.Id);

        var reloadedReceipt = reloadedOrder.OperationReceipts.First(r => r.OperationId == opId);
        Assert.Equal("Pending", reloadedReceipt.Status);
        Assert.Equal(0, reloadedReceipt.RetryCount);
        Assert.True(reloadedReceipt.IsOutcomeUncertain, "Sticky IsOutcomeUncertain must be preserved across manual replay!");

        // Verify audit transition appended with Admin actor and reason
        var audit = reloadedOrder.StateTransitions.FirstOrDefault(t => t.OperationId == idempKey);
        Assert.NotNull(audit);
        Assert.Equal(ActorRole.Admin, audit.ActorRole);
        Assert.Equal("AdminReplayRequested", audit.Action);
        Assert.Contains(reason, audit.Reason);
        Assert.Contains(adminId.ToString(), audit.Reason);

        // Idempotent retry with EXACT SAME key and reason returns 202 Accepted without adding duplicate audit
        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/rental/internal/orders/{order.Id}/operations/{opId}/replay")
        {
            Content = JsonContent.Create(new AdminReplayRequest(reason))
        };
        replayRequest.Headers.Add("Idempotency-Key", idempKey);
        using var replayResponse = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Accepted, replayResponse.StatusCode);

        // Retry with SAME key but DIFFERENT reason returns 409 Conflict
        using var conflictRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/rental/internal/orders/{order.Id}/operations/{opId}/replay")
        {
            Content = JsonContent.Create(new AdminReplayRequest("Different reason for same key"))
        };
        conflictRequest.Headers.Add("Idempotency-Key", idempKey);
        using var conflictResponse = await client.SendAsync(conflictRequest);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
    }
}
