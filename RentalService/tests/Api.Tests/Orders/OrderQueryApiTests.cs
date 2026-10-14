using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Domain.IRepositories;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dprental.RentalService.Application.Security.Jwt;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Orders;

/// <summary>Gọi các API xem đơn qua HTTP với toàn bộ DI thật, chỉ thay database và cách xác thực.</summary>
public sealed class OrderQueryApiTests
{
    private const string SubjectHeader = "X-Test-Sub";

    [Fact]
    public async Task GetOrders_WithoutToken_Returns401()
    {
        using OrderApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/rental/orders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Renter_GetsOwnOrderWithSubOrders_AndAnotherRenterGets404()
    {
        using OrderApiFactory factory = new();
        RentalOrder order = factory.SeedPaidMixedOrder();

        using HttpClient renter = factory.CreateClientFor(factory.RenterId);
        using HttpResponseMessage own = await renter.GetAsync($"/api/v1/rental/orders/{order.Id}");
        using HttpClient stranger = factory.CreateClientFor(Guid.NewGuid());
        using HttpResponseMessage other = await stranger.GetAsync($"/api/v1/rental/orders/{order.Id}");

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        JsonElement body = await ReadAsync(own);
        Assert.Equal(order.OrderCode, body.GetProperty("orderCode").GetString());
        Assert.Equal("PAID", body.GetProperty("status").GetString());
        Assert.Equal(2, body.GetProperty("subOrders").GetArrayLength());
        JsonElement firstItem = body.GetProperty("subOrders")[0].GetProperty("items")[0];
        Assert.Equal("2026-10-14", firstItem.GetProperty("endDate").GetString());
        Assert.Equal("PENDING_SUPPLIER_CONFIRMATION", firstItem.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal("Order.NotFound", (await ReadAsync(other)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Supplier_ListsOnlyOwnSubOrder_AndCannotOpenAnotherSuppliersSubOrder()
    {
        using OrderApiFactory factory = new();
        RentalOrder order = factory.SeedPaidMixedOrder();
        RentalSubOrder cuongsSubOrder = order.SubOrders.Single(s => s.SupplierId == factory.SupplierCuong);

        using HttpClient binh = factory.CreateClientFor(factory.SupplierBinh);
        using HttpResponseMessage list = await binh.GetAsync("/api/v1/rental/supplier/sub-orders");
        using HttpResponseMessage foreign = await binh.GetAsync($"/api/v1/rental/supplier/sub-orders/{cuongsSubOrder.Id}");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        JsonElement page = await ReadAsync(list);
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
        JsonElement subOrder = page.GetProperty("items")[0];
        Assert.Equal($"{order.OrderCode}-01", subOrder.GetProperty("subOrderCode").GetString());
        Assert.Equal(1, subOrder.GetProperty("itemCount").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("SubOrder.NotFound", (await ReadAsync(foreign)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Supplier_RejectsWholeSubOrderOverHttp_AndThePerItemRouteNoLongerExists()
    {
        using OrderApiFactory factory = new();
        RentalOrder order = factory.SeedPaidMixedOrder();
        RentalSubOrder binhsSubOrder = order.SubOrders.Single(s => s.SupplierId == factory.SupplierBinh);
        Guid binhsItemId = binhsSubOrder.Items.Single().Id;

        using HttpClient binh = factory.CreateClientFor(factory.SupplierBinh);
        binh.DefaultRequestHeaders.Add("Idempotency-Key", "reject-1");
        using HttpResponseMessage perItem = await binh.PostAsync($"/api/v1/rental/items/{binhsItemId}/reject",
            JsonContent.Create(new { rejectReason = "Không giao kịp" }));
        using HttpResponseMessage reject = await binh.PostAsync($"/api/v1/rental/supplier/sub-orders/{binhsSubOrder.Id}/reject",
            JsonContent.Create(new { rejectReason = "Máy xúc đang bảo trì, không giao kịp" }));

        Assert.Equal(HttpStatusCode.NotFound, perItem.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        JsonElement body = await ReadAsync(reject);
        Assert.Equal("Rejected", body.GetProperty("status").GetString());
        Assert.Equal($"{order.OrderCode}-01", body.GetProperty("subOrderCode").GetString());

        // Người thuê thấy phần của Bình bị từ chối, phần của Cường vẫn chờ xác nhận.
        using HttpClient renter = factory.CreateClientFor(factory.RenterId);
        JsonElement detail = await ReadAsync(await renter.GetAsync($"/api/v1/rental/orders/{order.Id}"));
        Assert.Equal("REJECTED", detail.GetProperty("subOrders")[0].GetProperty("status").GetString());
        Assert.Equal("PENDING_SUPPLIER_CONFIRMATION", detail.GetProperty("subOrders")[1].GetProperty("status").GetString());
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private sealed class OrderApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString("N");

        public Guid RenterId { get; } = Guid.NewGuid();

        public Guid SupplierBinh { get; } = Guid.NewGuid();

        public Guid SupplierCuong { get; } = Guid.NewGuid();

        public HttpClient CreateClientFor(Guid userId)
        {
            HttpClient client = CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("TestScheme");
            client.DefaultRequestHeaders.Add(SubjectHeader, userId.ToString("D"));
            return client;
        }

        /// <summary>Đơn của người thuê gồm máy xúc của Bình và máy trộn của Cường, đã thanh toán.</summary>
        public RentalOrder SeedPaidMixedOrder()
        {
            RentalOrder order = OrderTestData.Paid(
                OrderTestData.Order(
                    RenterId,
                    "DH-2026-000010",
                    OrderTestData.Now,
                    OrderTestData.Line(SupplierBinh, "Máy xúc CAT 320", "Máy xúc", "Caterpillar", 1, 3_000_000m, 20_000_000m),
                    OrderTestData.Line(SupplierCuong, "Máy trộn bê tông 350L", "Máy trộn", "Hòa Phát", 2, 400_000m, 3_000_000m)),
                OrderTestData.Now.AddMinutes(5));

            using IServiceScope scope = Services.CreateScope();
            RentalServiceDbContext context = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            context.RentalOrders.Add(order);
            context.SaveChanges();
            return order;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<DbContextOptions<RentalServiceDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<RentalServiceDbContext>>();
                services.AddDbContext<RentalServiceDbContext>(options => options.UseInMemoryDatabase(_dbName));
                services.RemoveAll<IOrderLockManager>();
                services.AddSingleton<IOrderLockManager, FakeOrderLockManager>();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(OrderTestData.Now.AddMinutes(10)));

                services.AddAuthentication("TestScheme")
                    .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("TestScheme", _ => { });
            });
        }
    }

    /// <summary>Dựng danh tính người gọi từ header của request.</summary>
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
                new(RentalAuthClaims.ProfileCompleted, "true")
            ];

            ClaimsPrincipal principal = new(new ClaimsIdentity(claims, "TestScheme"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "TestScheme")));
        }
    }
}
