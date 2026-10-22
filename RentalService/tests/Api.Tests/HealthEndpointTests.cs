using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Common;
using System.Net;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.RentalService.Api.Tests;

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
                    services.RemoveAll<DbContextOptions<RentalServiceDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<RentalServiceDbContext>>();
                    services.AddDbContext<RentalServiceDbContext>(options =>
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
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("InternalAuthentication:ApiKey", "dev-internal-api-key-dprental-2026");
                builder.UseSetting("ConnectionStrings:Postgres", "");
            });

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public void MissingProductionTerms_FailsAtStartup()
    {
        using WebApplicationFactory<Program> factory = CreateFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("Kafka:Enabled", "false");
            });

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains(exception.Failures, failure => failure.Contains("Rental:Terms:CurrentVersion", StringComparison.Ordinal));
        Assert.Contains(exception.Failures, failure => failure.Contains("Rental:Terms:DocumentRef", StringComparison.Ordinal));
        Assert.Contains(exception.Failures, failure => failure.Contains("Rental:Terms:ContentHash", StringComparison.Ordinal));
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("InternalAuthentication:ApiKey", "dev-internal-api-key-dprental-2026");
                builder.UseSetting(
                    "ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.ConfigureTestServices(services =>
                {
                    var listingDependencies = new TestListingDependencies();
                    services.AddSingleton<IRentalListingReader>(listingDependencies);
                    services.AddSingleton<IRentalAvailabilityReader>(listingDependencies);
                });
            });

    private sealed class TestListingDependencies :
        IRentalListingReader,
        IRentalAvailabilityReader
    {

        public Task<Result<RentalListingSnapshot>> GetListingAsync(Guid listingId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<RentalListingSnapshot>>(Error.NotFound("NOT_FOUND", "Not found"));

        public Task<Result<RentalAvailabilityResult>> GetAvailableQuantityAsync(
            Guid listingId,
            DateOnly startDate,
            DateOnly endDate,
            int requestedQuantity,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<RentalAvailabilityResult>>(Error.NotFound("NOT_FOUND", "Not found"));
    }
}
