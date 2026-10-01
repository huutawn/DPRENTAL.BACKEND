using System.Net;
using System.Net.Http.Json;
using Dprental.CatalogService.Domain.IRepositories;
using Dprental.CatalogService.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class PublicCatalogEndpointTests
{
    [Fact]
    public async Task Listing_IsAnonymous_AndReturnsPagedResponse()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/catalog/equipments?keyword=komatsu&categoryId=11111111-1111-1111-1111-111111111111&minPrice=500000&page=2&pageSize=20");
        PublicCatalogResponse? result = await response.Content.ReadFromJsonAsync<PublicCatalogResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(result);
        Assert.Equal(2, result.Page);
        Assert.Equal(20, result.PageSize);
        Assert.Equal(1, result.TotalItems);
        Assert.Equal(1, result.TotalPages);
    }

    [Fact]
    public async Task Listing_WithInvalidPriceRange_ReturnsBadRequest()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/catalog/equipments?minPrice=3000000&maxPrice=500000");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Detail_ReturnsPublishedEquipment()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/catalog/equipments/22222222-2222-2222-2222-222222222222");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Detail_DoesNotExposeUnknownOrUnpublishedEquipment()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/catalog/equipments/33333333-3333-3333-3333-333333333333");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IEquipmentQueryRepository>();
                    services.AddSingleton<IEquipmentQueryRepository, FakeEquipmentQueryRepository>();
                });
            });

    private sealed class FakeEquipmentQueryRepository : IEquipmentQueryRepository
    {
        private static readonly Guid PublishedId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        public Task<PagedResult<EquipmentListItem>> SearchPublishedAsync(
            EquipmentSearchCriteria criteria,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<EquipmentListItem>(
                [new EquipmentListItem(
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    "Komatsu PC200",
                    "Komatsu",
                    1_500_000,
                    Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    new CategorySummary(
                        Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        "Excavator"))], 1));

        public Task<EquipmentDetail?> GetPublishedByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EquipmentDetail?>(id == PublishedId
                ? new EquipmentDetail(
                    PublishedId,
                    "EQ-001",
                    "Komatsu PC200",
                    "Komatsu",
                    "Published equipment",
                    1_500_000,
                    Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    new CategorySummary(
                        Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        "Excavator"),
                    "{}",
                    [Guid.Parse("55555555-5555-5555-5555-555555555555")])
                : null);
    }

    private sealed record PublicCatalogResponse(
        IReadOnlyList<object> Items,
        int Page,
        int PageSize,
        int TotalItems,
        int TotalPages);
}
