using System.Net;
using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Domain.Repositories;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.PaymentService.Api.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task HealthLive_ReturnsOk()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", content);
    }

    [Fact]
    public async Task HealthReady_ReturnsOk()
    {
        using WebApplicationFactory<Program> factory = CreateFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<PaymentServiceDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<PaymentServiceDbContext>>();
                    services.AddDbContext<PaymentServiceDbContext>(options =>
                        options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
                });
            });

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", content);
    }

    [Fact]
    public void MissingPostgresConnection_FailsAtStartup()
    {
        using WebApplicationFactory<Program> factory = new PaymentWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "");
                builder.UseSetting("InternalAuthentication:RentalApiKey", "dev-internal-api-key-dprental-2026");
            });
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new PaymentWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("InternalAuthentication:RentalApiKey", "dev-internal-api-key-dprental-2026");
            });
}
