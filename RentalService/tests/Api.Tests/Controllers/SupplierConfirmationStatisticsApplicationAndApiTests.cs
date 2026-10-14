using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Security.Jwt;
using Dprental.RentalService.Application.DTOs.SupplierStatistics;
using Dprental.RentalService.Application.IServices.Expiry;
using Dprental.RentalService.Application.IServices.SupplierStatistics;
using Dprental.RentalService.Application.Services.SupplierStatistics;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Repositories;
using Dprental.RentalService.Domain.IRepositories;
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

namespace Dprental.RentalService.Api.Tests.Controllers;

public sealed class SupplierConfirmationStatisticsApplicationAndApiTests
{
    private sealed class FakeRentalOrderRepository : IRentalOrderRepository
    {
        public SupplierConfirmationStatisticsRecord RecordedStats { get; set; } = new(10, 2, 1, 3);
        public Guid? LastSupplierId { get; private set; }
        public DateTimeOffset? LastFromUtc { get; private set; }
        public DateTimeOffset? LastToUtc { get; private set; }

        public Task<SupplierConfirmationStatisticsRecord> GetSupplierConfirmationStatisticsAsync(
            Guid supplierId,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken = default)
        {
            LastSupplierId = supplierId;
            LastFromUtc = fromUtc;
            LastToUtc = toUtc;
            return Task.FromResult(RecordedStats);
        }

        public Task<RentalOrder?> GetPendingPaymentByRenterIdAsync(Guid renterId, CancellationToken ct = default) =>
            Task.FromResult<RentalOrder?>(null);

        public Task<RentalOrder?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RentalOrder?> GetByOrderCodeAsync(string code, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RentalOrder?> GetByCheckoutOperationIdAsync(string opId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<RentalOrder>> GetByRenterIdAsync(Guid renterId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<RentalOrder>> GetBySupplierIdAsync(Guid supplierId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RentalOrder> CreateAsync(RentalOrder order, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> UpdateAsync(RentalOrder order, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RentalOrder?> GetByIdWithDetailsAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RentalOrder?> GetByItemIdWithDetailsAsync(Guid itemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Guid?> GetOrderIdByItemIdAsync(Guid itemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RentalSubOrder?> GetSubOrderByIdAsync(Guid subOrderId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RecordPage<RenterOrderSummaryRecord>> GetRenterOrderPageAsync(Guid renterId, int skip, int take, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RentalOrder?> GetRenterOrderWithSubOrdersAsync(Guid orderId, Guid renterId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RecordPage<SupplierSubOrderSummaryRecord>> GetPaidSubOrderPageForSupplierAsync(Guid supplierId, int skip, int take, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RentalSubOrder?> GetPaidSubOrderForSupplierAsync(Guid subOrderId, Guid supplierId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> GetPendingPaymentExpiredOrderIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> GetPendingSupplierTimeoutItemIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<RenterCalendarItemRecord>> GetRenterCalendarItemsAsync(Guid renterId, DateOnly monthStart, DateOnly monthEnd, IReadOnlyCollection<RentalItemStatus> targetStatuses, CancellationToken ct = default) => throw new NotImplementedException();
    }

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

    #region Application Service Unit Tests

    [Fact]
    public async Task Service_AnonymousCaller_ReturnsUnauthorized()
    {
        var repo = new FakeRentalOrderRepository();
        var service = new SupplierStatisticsService(repo);
        var caller = RentalCallerContext.Anonymous;

        var request = new SupplierConfirmationStatisticsRequest(
            Guid.NewGuid(),
            "2026-10-01T00:00:00Z",
            "2026-11-01T00:00:00Z");

        var result = await service.GetConfirmationStatisticsAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
    }

    [Fact]
    public async Task Service_ServiceTokenCaller_ReturnsForbidden()
    {
        var repo = new FakeRentalOrderRepository();
        var service = new SupplierStatisticsService(repo);
        var caller = RentalCallerContext.ForService("TestService", ["admin"]);

        var request = new SupplierConfirmationStatisticsRequest(
            Guid.NewGuid(),
            "2026-10-01T00:00:00Z",
            "2026-11-01T00:00:00Z");

        var result = await service.GetConfirmationStatisticsAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
        Assert.Equal(RentalAuthorizationPolicy.ErrorCodes.ServiceTokenNotAllowed, result.Error.Code);
    }

    [Fact]
    public async Task Service_NonAdminUser_ReturnsForbidden()
    {
        var repo = new FakeRentalOrderRepository();
        var service = new SupplierStatisticsService(repo);
        var caller = RentalCallerContext.ForUser(Guid.NewGuid(), true, ["owner", "customer"]);

        var request = new SupplierConfirmationStatisticsRequest(
            Guid.NewGuid(),
            "2026-10-01T00:00:00Z",
            "2026-11-01T00:00:00Z");

        var result = await service.GetConfirmationStatisticsAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
    }

    [Fact]
    public async Task Service_AdminCaller_ReturnsSuccess()
    {
        var repo = new FakeRentalOrderRepository { RecordedStats = new(12, 3, 2, 1) };
        var service = new SupplierStatisticsService(repo);
        var caller = RentalCallerContext.ForUser(Guid.NewGuid(), true, ["adm"]);

        Guid supplierId = Guid.NewGuid();
        var request = new SupplierConfirmationStatisticsRequest(
            supplierId,
            "2026-10-01T00:00:00Z",
            "2026-11-01T00:00:00Z");

        var result = await service.GetConfirmationStatisticsAsync(request, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(supplierId, result.Value.SupplierId);
        Assert.Equal(12, result.Value.AcceptedCount);
        Assert.Equal(3, result.Value.RejectedCount);
        Assert.Equal(2, result.Value.ConfirmationTimeoutCount);
        Assert.Equal(1, result.Value.CancelAfterAcceptCount);
    }

    [Fact]
    public async Task Service_EmptySupplierId_ReturnsValidation()
    {
        var repo = new FakeRentalOrderRepository();
        var service = new SupplierStatisticsService(repo);
        var caller = RentalCallerContext.ForUser(Guid.NewGuid(), true, ["admin"]);

        var request = new SupplierConfirmationStatisticsRequest(
            Guid.Empty,
            "2026-10-01T00:00:00Z",
            "2026-11-01T00:00:00Z");

        var result = await service.GetConfirmationStatisticsAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_SUPPLIER_ID", result.Error.Code);
    }

    [Theory]
    [InlineData(null, "2026-11-01T00:00:00Z")]
    [InlineData("2026-10-01T00:00:00Z", null)]
    [InlineData("", "")]
    public async Task Service_MissingDate_ReturnsValidation(string? from, string? to)
    {
        var repo = new FakeRentalOrderRepository();
        var service = new SupplierStatisticsService(repo);
        var caller = RentalCallerContext.ForUser(Guid.NewGuid(), true, ["admin"]);

        var request = new SupplierConfirmationStatisticsRequest(Guid.NewGuid(), from, to);
        var result = await service.GetConfirmationStatisticsAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Theory]
    [InlineData("2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z")]
    [InlineData("2026-10-01t00:00:00z", "2026-11-01t00:00:00z")]
    [InlineData("2026-10-01T07:00:00+07:00", "2026-11-01T07:00:00+07:00")]
    [InlineData("2026-10-01T00:00:00-05:00", "2026-11-01T00:00:00-05:00")]
    [InlineData("2026-10-01T00:00:00.1Z", "2026-11-01T00:00:00.1Z")]
    [InlineData("2026-10-01T00:00:00.1234567Z", "2026-11-01T00:00:00.1234567Z")]
    [InlineData("2026-10-01T00:00:00.1234567+07:00", "2026-11-01T00:00:00.1234567+07:00")]
    public async Task Service_ValidStrictIsoFormats_Succeeds(string from, string to)
    {
        var repo = new FakeRentalOrderRepository();
        var service = new SupplierStatisticsService(repo);
        var caller = RentalCallerContext.ForUser(Guid.NewGuid(), true, ["admin"]);

        var request = new SupplierConfirmationStatisticsRequest(Guid.NewGuid(), from, to);
        var result = await service.GetConfirmationStatisticsAsync(request, caller);

        Assert.True(result.IsSuccess);
        Assert.NotNull(repo.LastSupplierId);
    }

    [Theory]
    [InlineData("2026-10-01T00:00:00+07", "2026-11-01T00:00:00Z")]     // +07 without minutes
    [InlineData("2026-10-01T00:00:00+0700", "2026-11-01T00:00:00Z")]   // +0700 without colon
    [InlineData("2026-10-01", "2026-11-01T00:00:00Z")]                 // date only
    [InlineData("2026-10-01T00:00", "2026-11-01T00:00:00Z")]           // missing seconds
    [InlineData("2026-10-01T00:00:00", "2026-11-01T00:00:00Z")]        // missing timezone
    [InlineData("2026-10-01T00:00:00Z trailing", "2026-11-01T00:00:00Z")] // trailing text
    [InlineData("2026-10-01T00:00:00.12345678Z", "2026-11-01T00:00:00Z")] // 8 fractional digits
    [InlineData("2026-02-31T00:00:00Z", "2026-11-01T00:00:00Z")]       // invalid date
    [InlineData("2026-10-01T25:00:00Z", "2026-11-01T00:00:00Z")]       // invalid hour
    [InlineData("2026-10-01T00:65:00Z", "2026-11-01T00:00:00Z")]       // invalid minute
    [InlineData("2026-10-01T00:00:00+25:00", "2026-11-01T00:00:00Z")]  // invalid offset
    public async Task Service_InvalidStrictFormats_ReturnsValidation(string from, string to)
    {
        var repo = new FakeRentalOrderRepository();
        var service = new SupplierStatisticsService(repo);
        var caller = RentalCallerContext.ForUser(Guid.NewGuid(), true, ["admin"]);

        var request = new SupplierConfirmationStatisticsRequest(Guid.NewGuid(), from, to);
        var result = await service.GetConfirmationStatisticsAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_DATETIME_FORMAT", result.Error.Code);
        Assert.Null(repo.LastSupplierId); // Repository MUST not be called on validation failure
    }
    [Theory]
    [InlineData("2026-11-01T00:00:00Z", "2026-10-01T00:00:00Z")] // reversed
    [InlineData("2026-10-01T00:00:00Z", "2026-10-01T00:00:00Z")] // identical
    public async Task Service_ReversedOrEqualDateRange_ReturnsValidation(string from, string to)
    {
        var repo = new FakeRentalOrderRepository();
        var service = new SupplierStatisticsService(repo);
        var caller = RentalCallerContext.ForUser(Guid.NewGuid(), true, ["admin"]);

        var request = new SupplierConfirmationStatisticsRequest(Guid.NewGuid(), from, to);
        var result = await service.GetConfirmationStatisticsAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_TIME_RANGE", result.Error.Code);
    }

    [Fact]
    public async Task Service_EquivalentOffsets_NormalizeToExactUtcRange()
    {
        var repo = new FakeRentalOrderRepository();
        var service = new SupplierStatisticsService(repo);
        var caller = RentalCallerContext.ForUser(Guid.NewGuid(), true, ["admin"]);

        Guid supplierId = Guid.NewGuid();
        // Vietnam timezone UTC+7
        var requestVn = new SupplierConfirmationStatisticsRequest(
            supplierId,
            "2026-10-01T07:00:00+07:00",
            "2026-11-01T07:00:00+07:00");

        var resultVn = await service.GetConfirmationStatisticsAsync(requestVn, caller);

        Assert.True(resultVn.IsSuccess);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), repo.LastFromUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), repo.LastToUtc);
        Assert.Equal(TimeSpan.Zero, resultVn.Value.FromUtc.Offset);
        Assert.Equal(TimeSpan.Zero, resultVn.Value.ToUtc.Offset);
    }

    #endregion

    #region WebApplicationFactory HTTP Tests

    private static WebApplicationFactory<Program> CreateTestFactory(Action<IServiceCollection>? configureServices = null)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<RentalServiceDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<RentalServiceDbContext>>();
                    services.AddDbContext<RentalServiceDbContext>(options =>
                        options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));

                    services.RemoveAll<IOrderLockManager>();
                    services.AddSingleton<IOrderLockManager, FakeOrderLockManager>();

                    services.AddAuthentication("TestScheme")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestScheme", _ => { });

                    configureServices?.Invoke(services);
                });
            });
    }

    [Fact]
    public async Task Http_Unauthenticated_Returns401()
    {
        TestAuthHandler.Claims = [];
        using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/rental/admin/suppliers/{Guid.NewGuid()}/confirmation-statistics?fromUtc=2026-10-01T00:00:00Z&toUtc=2026-11-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Http_ServiceToken_Returns403()
    {
        TestAuthHandler.Claims =
        [
            new Claim(RentalAuthClaims.Subject, Guid.NewGuid().ToString("D")),
            new Claim(RentalAuthClaims.UserTypeOrAlias, RentalAuthClaims.ServiceIndicator),
            new Claim(RentalAuthClaims.Roles, "adm")
        ];

        using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/rental/admin/suppliers/{Guid.NewGuid()}/confirmation-statistics?fromUtc=2026-10-01T00:00:00Z&toUtc=2026-11-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Http_NonAdminUser_Returns403()
    {
        TestAuthHandler.Claims =
        [
            new Claim(RentalAuthClaims.Subject, Guid.NewGuid().ToString("D")),
            new Claim(RentalAuthClaims.ProfileCompleted, "true"),
            new Claim(RentalAuthClaims.Roles, "owner")
        ];

        using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/rental/admin/suppliers/{Guid.NewGuid()}/confirmation-statistics?fromUtc=2026-10-01T00:00:00Z&toUtc=2026-11-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Http_AdminUser_Returns200WithExpectedResponse()
    {
        Guid supplierId = Guid.NewGuid();
        var fakeRepo = new FakeRentalOrderRepository { RecordedStats = new(15, 4, 3, 2) };

        TestAuthHandler.Claims =
        [
            new Claim(RentalAuthClaims.Subject, Guid.NewGuid().ToString("D")),
            new Claim(RentalAuthClaims.ProfileCompleted, "true"),
            new Claim(RentalAuthClaims.Roles, "adm")
        ];

        using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IRentalOrderRepository>();
            services.AddSingleton<IRentalOrderRepository>(fakeRepo);
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/rental/admin/suppliers/{supplierId}/confirmation-statistics?fromUtc=2026-10-01T00:00:00Z&toUtc=2026-11-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(supplierId, root.GetProperty("supplierId").GetGuid());
        Assert.Equal(15, root.GetProperty("acceptedCount").GetInt64());
        Assert.Equal(4, root.GetProperty("rejectedCount").GetInt64());
        Assert.Equal(3, root.GetProperty("confirmationTimeoutCount").GetInt64());
        Assert.Equal(2, root.GetProperty("cancelAfterAcceptCount").GetInt64());

        // Verify NO internal/sensitive data leaked in response
        Assert.False(root.TryGetProperty("assetId", out _));
        Assert.False(root.TryGetProperty("serialNumber", out _));
        Assert.False(root.TryGetProperty("allocationGroupId", out _));
        Assert.False(root.TryGetProperty("internalNotes", out _));
        Assert.False(root.TryGetProperty("concurrency", out _));
    }

    [Theory]
    [InlineData("fromUtc=2026-10-01T00:00:00&toUtc=2026-11-01T00:00:00Z")] // missing timezone in from
    [InlineData("fromUtc=2026-10-01T00:00:00Z&toUtc=2026-11-01T00:00:00")] // missing timezone in to
    [InlineData("fromUtc=2026-11-01T00:00:00Z&toUtc=2026-10-01T00:00:00Z")] // reversed range
    [InlineData("fromUtc=&toUtc=2026-11-01T00:00:00Z")]                     // empty from
    [InlineData("")]                                                         // missing both
    public async Task Http_InvalidQueryParameters_Returns400(string queryString)
    {
        TestAuthHandler.Claims =
        [
            new Claim(RentalAuthClaims.Subject, Guid.NewGuid().ToString("D")),
            new Claim(RentalAuthClaims.ProfileCompleted, "true"),
            new Claim(RentalAuthClaims.Roles, "adm")
        ];

        using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/rental/admin/suppliers/{Guid.NewGuid()}/confirmation-statistics?{queryString}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Http_InvalidSupplierId_Returns400(string invalidSupplierId)
    {
        TestAuthHandler.Claims =
        [
            new Claim(RentalAuthClaims.Subject, Guid.NewGuid().ToString("D")),
            new Claim(RentalAuthClaims.ProfileCompleted, "true"),
            new Claim(RentalAuthClaims.Roles, "adm")
        ];

        using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/rental/admin/suppliers/{invalidSupplierId}/confirmation-statistics?fromUtc=2026-10-01T00:00:00Z&toUtc=2026-11-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Http_UrlEncodedPlusOffset_Returns200()
    {
        Guid supplierId = Guid.NewGuid();
        var fakeRepo = new FakeRentalOrderRepository { RecordedStats = new(5, 1, 0, 0) };

        TestAuthHandler.Claims =
        [
            new Claim(RentalAuthClaims.Subject, Guid.NewGuid().ToString("D")),
            new Claim(RentalAuthClaims.ProfileCompleted, "true"),
            new Claim(RentalAuthClaims.Roles, "adm")
        ];

        using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IRentalOrderRepository>();
            services.AddSingleton<IRentalOrderRepository>(fakeRepo);
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/rental/admin/suppliers/{supplierId}/confirmation-statistics?fromUtc=2026-10-01T07:00:00%2B07:00&toUtc=2026-11-01T07:00:00%2B07:00");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), fakeRepo.LastFromUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), fakeRepo.LastToUtc);
    }

    [Fact]
    public async Task Http_UnencodedPlusOffset_DecodedAsSpace_Returns400()
    {
        TestAuthHandler.Claims =
        [
            new Claim(RentalAuthClaims.Subject, Guid.NewGuid().ToString("D")),
            new Claim(RentalAuthClaims.ProfileCompleted, "true"),
            new Claim(RentalAuthClaims.Roles, "adm")
        ];

        using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/rental/admin/suppliers/{Guid.NewGuid()}/confirmation-statistics?fromUtc=2026-10-01T07:00:00+07:00&toUtc=2026-11-01T07:00:00+07:00");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Http_AdminEndpointAndExpiryDi_CoexistInHost()
    {
        TestAuthHandler.Claims =
        [
            new Claim(RentalAuthClaims.Subject, Guid.NewGuid().ToString("D")),
            new Claim(RentalAuthClaims.ProfileCompleted, "true"),
            new Claim(RentalAuthClaims.Roles, "adm")
        ];

        using var factory = CreateTestFactory(services =>
        {
            services.RemoveAll<IRentalOrderRepository>();
            services.AddSingleton<IRentalOrderRepository>(new FakeRentalOrderRepository());
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/rental/admin/suppliers/{Guid.NewGuid()}/confirmation-statistics?fromUtc=2026-10-01T00:00:00Z&toUtc=2026-11-01T00:00:00Z");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest);

        using var scope = factory.Services.CreateScope();
        var expiry = scope.ServiceProvider.GetRequiredService<IExpiryWorkflowService>();
        Assert.NotNull(expiry);
    }

    #endregion
}
