using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Security.Jwt;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Orders;

/// <summary>Dựng API thật với database in-memory và đăng nhập giả bằng header, dùng chung cho các test gọi HTTP về đơn hàng.</summary>
internal static class OrderApiHeaders
{
    public const string Subject = "X-Test-Sub";
    public const string Roles = "X-Test-Roles";
    public const string Service = "X-Test-Service";
}

internal sealed class OrderApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = Guid.NewGuid().ToString("N");

    public Guid RenterId { get; } = Guid.NewGuid();

    public Guid SupplierBinh { get; } = Guid.NewGuid();

    public Guid SupplierCuong { get; } = Guid.NewGuid();

    public HttpClient CreateClientFor(Guid userId, string? roles = null, bool service = false)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("TestScheme");
        client.DefaultRequestHeaders.Add(OrderApiHeaders.Subject, userId.ToString("D"));
        if (roles is not null)
        {
            client.DefaultRequestHeaders.Add(OrderApiHeaders.Roles, roles);
        }

        if (service)
        {
            client.DefaultRequestHeaders.Add(OrderApiHeaders.Service, "true");
        }

        return client;
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", "dev-internal-api-key-dprental-2026");
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
        builder.UseSetting("InternalAuthentication:ApiKey", "dev-internal-api-key-dprental-2026");

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
internal sealed class HeaderAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? subject = Request.Headers[OrderApiHeaders.Subject];
        if (string.IsNullOrWhiteSpace(subject))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        List<Claim> claims =
        [
            new(RentalAuthClaims.Subject, subject),
            new(RentalAuthClaims.ProfileCompleted, "true")
        ];
        string? roles = Request.Headers[OrderApiHeaders.Roles];
        if (!string.IsNullOrWhiteSpace(roles))
        {
            claims.Add(new Claim(RentalAuthClaims.Roles, roles));
        }
        if (Request.Headers.ContainsKey(OrderApiHeaders.Service))
        {
            claims.Add(new Claim(RentalAuthClaims.UserTypeOrAlias, RentalAuthClaims.ServiceIndicator));
        }

        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, "TestScheme"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "TestScheme")));
    }
}
