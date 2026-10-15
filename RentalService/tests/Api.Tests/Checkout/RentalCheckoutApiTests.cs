using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Security.Jwt;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Repositories;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Checkout;

/// <summary>Gọi POST /rental/checkout qua HTTP với toàn bộ DI thật, chỉ thay database và các service bên ngoài.</summary>
public sealed class RentalCheckoutApiTests
{
    // 10:00 ngày 09/10/2026 giờ Việt Nam.
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 3, 0, 0, TimeSpan.Zero);
    private const string Route = "/api/v1/rental/checkout";
    private const string SubjectHeader = "X-Test-Sub";
    private const string ProfileCompletedHeader = "X-Test-Pc";

    // 2 máy x 500.000 x 3 ngày (12, 13, 14/10) + cọc 2 x 2.000.000.
    // 2 máy x 500.000 x 3 ngày; Inventory chưa có tiền cọc nên cọc = 0.
    private const decimal Total = 3_000_000m;

    [Fact]
    public async Task Submit_WithoutToken_Returns401()
    {
        using CheckoutApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", "key-1");

        using HttpResponseMessage response = await client.PostAsJsonAsync(Route, factory.Request());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Submit_WithoutIdempotencyKey_Returns400()
    {
        using CheckoutApiFactory factory = new();
        using HttpClient client = factory.CreateRenterClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(Route, factory.Request());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MISSING_IDEMPOTENCY_KEY", (await ReadAsync(response)).GetProperty("title").GetString());
        Assert.Empty(factory.Inventory.HoldRequests);
    }

    [Fact]
    public async Task Submit_WithIdempotencyKeyLongerThan64Characters_Returns400()
    {
        using CheckoutApiFactory factory = new();
        using HttpClient client = factory.CreateRenterClient(idempotencyKey: new string('k', 65));

        using HttpResponseMessage response = await client.PostAsJsonAsync(Route, factory.Request());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_IDEMPOTENCY_KEY", (await ReadAsync(response)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Submit_WithIncompleteProfile_Returns403()
    {
        using CheckoutApiFactory factory = new();
        using HttpClient client = factory.CreateRenterClient("key-1", profileCompleted: false);

        using HttpResponseMessage response = await client.PostAsJsonAsync(Route, factory.Request());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Checkout.ProfileIncomplete", (await ReadAsync(response)).GetProperty("title").GetString());
    }

    [Fact]
<<<<<<< HEAD
    public async Task Submit_Returns202WithOperation_AndReplayReturnsTheSameOperation()
=======
    public async Task Submit_Returns202WithOperation_ThenTheSameOperationOnReplay()
>>>>>>> origin/rental/fix/sub-order-and-order-queries
    {
        using CheckoutApiFactory factory = new();
        using HttpClient client = factory.CreateRenterClient("key-1");

        using HttpResponseMessage accepted = await client.PostAsJsonAsync(Route, factory.Request());

<<<<<<< HEAD
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        string createdJson = await created.Content.ReadAsStringAsync();
        JsonElement operation = JsonDocument.Parse(createdJson).RootElement;
        string operationId = operation.GetProperty("operationId").GetString()!;
        string statusUrl = operation.GetProperty("statusUrl").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(operationId));
        Assert.EndsWith(operationId, statusUrl);

        using HttpResponseMessage statusResponse = await client.GetAsync(statusUrl);
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        Assert.Equal("Processing", (await ReadAsync(statusResponse)).GetProperty("status").GetString());
=======
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        string acceptedJson = await accepted.Content.ReadAsStringAsync();
        JsonElement operation = JsonDocument.Parse(acceptedJson).RootElement;
        string operationId = operation.GetProperty("operationId").GetString()!;
        string statusUrl = $"/api/v1/rental/checkout/operations/{operationId}";
        Assert.Equal(statusUrl, operation.GetProperty("statusUrl").GetString());
        Assert.Equal(statusUrl, accepted.Headers.Location?.OriginalString);
>>>>>>> origin/rental/fix/sub-order-and-order-queries

        // Mã máy và mã giữ lịch là dữ liệu nội bộ, không trả cho renter.
        Assert.DoesNotContain("asset", acceptedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("allocation", acceptedJson, StringComparison.OrdinalIgnoreCase);

        // Replay with exact same key returns 202 Accepted with same operationId
        using HttpResponseMessage replayed = await client.PostAsJsonAsync(Route, factory.Request());

        Assert.Equal(HttpStatusCode.Accepted, replayed.StatusCode);
<<<<<<< HEAD
        JsonElement replay = await ReadAsync(replayed);
        Assert.Equal(operationId, replay.GetProperty("operationId").GetString());
=======
        Assert.Equal(operationId, (await ReadAsync(replayed)).GetProperty("operationId").GetString());

        // Lệnh giữ lịch chỉ đi qua Outbox một lần; đơn và hóa đơn chỉ được tạo khi Inventory trả lời.
        Assert.Single(factory.LoadCreateHoldCommands());
>>>>>>> origin/rental/fix/sub-order-and-order-queries
        Assert.Empty(factory.Inventory.HoldRequests);
        Assert.Empty(factory.Payment.InvoiceRequests);
    }

    [Fact]
<<<<<<< HEAD
    public async Task Submit_Returns202BeforePaymentIsCalled()
=======
    public async Task OperationStatus_IsProcessingUntilInventoryReplies_AndHiddenFromOtherRenters()
>>>>>>> origin/rental/fix/sub-order-and-order-queries
    {
        using CheckoutApiFactory factory = new();
        using HttpClient client = factory.CreateRenterClient("key-1");
        using HttpResponseMessage accepted = await client.PostAsJsonAsync(Route, factory.Request());
        string statusUrl = (await ReadAsync(accepted)).GetProperty("statusUrl").GetString()!;

        using HttpResponseMessage status = await client.GetAsync(statusUrl);

<<<<<<< HEAD
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await ReadAsync(response)).GetProperty("operationId").GetString()));
        Assert.Empty(factory.Payment.InvoiceRequests);
=======
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        JsonElement body = await ReadAsync(status);
        Assert.Equal("Processing", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("orderId").ValueKind);

        using HttpClient otherRenter = factory.CreateClient();
        otherRenter.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("TestScheme");
        otherRenter.DefaultRequestHeaders.Add(SubjectHeader, Guid.NewGuid().ToString("D"));
        otherRenter.DefaultRequestHeaders.Add(ProfileCompletedHeader, "true");

        using HttpResponseMessage hidden = await otherRenter.GetAsync(statusUrl);

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
>>>>>>> origin/rental/fix/sub-order-and-order-queries
    }

    [Fact]
    public async Task Submit_Returns409_WhenPriceChanged()
    {
        using CheckoutApiFactory factory = new();
        using HttpClient client = factory.CreateRenterClient("key-1");

        using HttpResponseMessage response = await client.PostAsJsonAsync(Route, factory.Request(expectedTotal: Total - 1));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        JsonElement problem = await ReadAsync(response);
        Assert.Equal("Checkout.PriceChanged", problem.GetProperty("title").GetString());
        Assert.Empty(factory.Inventory.HoldRequests);
    }

    [Fact]
    public async Task Submit_Returns400_WhenRequestIsInvalid()
    {
        using CheckoutApiFactory factory = new();
        using HttpClient client = factory.CreateRenterClient("key-1");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            Route, factory.Request() with { TermsAccepted = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement problem = await ReadAsync(response);
        Assert.Equal("Checkout.InvalidRequest", problem.GetProperty("title").GetString());
        Assert.Empty(factory.Inventory.HoldRequests);
    }

    [Fact]
    public async Task Submit_Returns503_WhenInventoryOmitsTheListingName()
    {
        using CheckoutApiFactory factory = new();
        using HttpClient client = factory.CreateRenterClient("key-1");
        CheckoutRequest request = factory.Request();
        factory.ChangeListing(listing => listing with { Name = "" });

        using HttpResponseMessage response = await client.PostAsJsonAsync(Route, request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        JsonElement problem = await ReadAsync(response);
        Assert.Equal("Checkout.DependencyUnavailable", problem.GetProperty("title").GetString());
        Assert.Empty(factory.Inventory.HoldRequests);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private sealed class CheckoutApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString("N");
        private readonly Guid _listingId = Guid.NewGuid();
        private readonly FakeInventoryListingClient _listings = new();
        private bool _seeded;

        public Guid RenterId { get; } = Guid.NewGuid();

        public Guid CartItemId { get; private set; }

        public FakeInventoryRentalClient Inventory { get; } = new();

        public FakePaymentRentalClient Payment { get; } = new();

        /// <summary>Sửa thông tin máy mà Inventory giả trả về (gọi sau <see cref="Request"/>, khi dữ liệu đã được tạo).</summary>
        public void ChangeListing(Func<ListingRentalSnapshotDto, ListingRentalSnapshotDto> change) =>
            _listings.Listings[_listingId] = change(_listings.Listings[_listingId]);

        public CheckoutRequest Request(decimal expectedTotal = Total)
        {
            EnsureSeeded();
            return new(
                [CartItemId],
                new("Hồ Chí Minh", "123 Nguyễn Huệ, Quận 1", "Nguyễn Văn A", "0901234567", null),
                new("VCB", "0123456789", "NGUYEN VAN A"),
                "v1.0",
                true,
                expectedTotal);
        }

        public List<RentalOutboxMessage> LoadCreateHoldCommands()
        {
            using IServiceScope scope = Services.CreateScope();
            RentalServiceDbContext context = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            return context.RentalOutboxMessages
                .Where(m => m.MessageType == KafkaContractSpec.CommandTypes.CreateHold)
                .ToList();
        }

        public HttpClient CreateRenterClient(string? idempotencyKey = null, bool profileCompleted = true)
        {
            HttpClient client = CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("TestScheme");
            client.DefaultRequestHeaders.Add(SubjectHeader, RenterId.ToString("D"));
            client.DefaultRequestHeaders.Add(ProfileCompletedHeader, profileCompleted ? "true" : "false");
            if (idempotencyKey is not null)
            {
                client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
            }

            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
            builder.UseSetting("Rental:Terms:CurrentVersion", "v1.0");
            builder.UseSetting("Rental:Terms:DocumentRef", "terms/v1.0.pdf");
            builder.UseSetting("Rental:Terms:ContentHash", "hash123456");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<RentalServiceDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<RentalServiceDbContext>>();
                services.AddDbContext<RentalServiceDbContext>(options => options.UseInMemoryDatabase(_dbName));

                services.RemoveAll<IOrderLockManager>();
                services.AddSingleton<IOrderLockManager, FakeOrderLockManager>();
                services.RemoveAll<IOrderCodeGenerator>();
                services.AddSingleton<IOrderCodeGenerator, FakeOrderCodeGenerator>();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));

                Inventory.Listings = _listings;
                services.RemoveAll<IInventoryRentalClient>();
                services.AddSingleton<IInventoryRentalClient>(Inventory);
                services.RemoveAll<IPaymentRentalClient>();
                services.AddSingleton<IPaymentRentalClient>(Payment);

                services.AddAuthentication("TestScheme")
                    .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("TestScheme", _ => { });
            });
        }

        private void EnsureSeeded()
        {
            if (_seeded)
            {
                return;
            }

            _seeded = true;

            Guid supplierId = Guid.NewGuid();

            RentalCart cart = RentalCart.Create(Guid.NewGuid(), RenterId, Now).Value;
            CartItemId = cart.AddItem(
                Guid.NewGuid(), _listingId, supplierId, 2, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 14), Now).Value.Id;

            using IServiceScope scope = Services.CreateScope();
            RentalServiceDbContext context = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            context.RentalCarts.Add(cart);
            context.SaveChanges();

            _listings.Listings[_listingId] = TestListings.Create(
                _listingId, supplierId, "Sony A7 IV", 500_000m, thumbnailMediaId: Guid.NewGuid());
            _listings.AvailableQuantities[_listingId] = 10;
        }
    }

    /// <summary>Dựng danh tính renter từ header của request để mỗi test dùng claim riêng.</summary>
    private sealed class HeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? subject = Request.Headers[SubjectHeader];
            if (string.IsNullOrWhiteSpace(subject))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            Claim[] claims =
            [
                new(RentalAuthClaims.Subject, subject),
                new(RentalAuthClaims.ProfileCompleted, Request.Headers[ProfileCompletedHeader].ToString())
            ];

            ClaimsPrincipal principal = new(new ClaimsIdentity(claims, "TestScheme"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "TestScheme")));
        }
    }
}
