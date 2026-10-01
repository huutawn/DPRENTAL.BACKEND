using System.Net;
using System.Net.Http.Json;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.Equipments;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class PublicCatalogEndpointTests
{
    private static readonly Guid PublishedEquipmentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UnpublishedEquipmentId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Listing_IsAnonymous_AndReturnsPagedResult()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/catalog/equipments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<EquipmentListItemDto>>();
        Assert.NotNull(page);
        Assert.Equal(1, page.Page);
        Assert.Equal(20, page.PageSize);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1, page.TotalPages);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Listing_WithInvalidPriceRange_ReturnsBadRequest()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/catalog/equipments?minPrice=500&maxPrice=100");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Detail_ReturnsPublishedEquipment()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync($"/api/v1/catalog/equipments/{PublishedEquipmentId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<EquipmentDetailDto>();
        Assert.NotNull(detail);
        Assert.Equal(PublishedEquipmentId, detail.Id);
    }

    [Fact]
    public async Task Detail_DoesNotExposeUnknownOrUnpublishedEquipment()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync($"/api/v1/catalog/equipments/{UnpublishedEquipmentId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseSetting("Auth:Jwt:Keys:0:KeyId", TestJwtHelper.DefaultKeyId);
                builder.UseSetting("Auth:Jwt:Keys:0:PublicKeyPem", TestJwtHelper.PublicKeyPem);

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IEquipmentQueryRepository>();
                    services.AddScoped<IEquipmentQueryRepository, FakeEquipmentQueryRepository>();
                });
            });
    private sealed class FakeEquipmentQueryRepository : IEquipmentQueryRepository
    {
        public Task<PagedResult<EquipmentListItemDto>> SearchPublishedAsync(
            GetEquipmentsRequest request,
            CancellationToken cancellationToken = default)
        {
            var item = new EquipmentListItemDto(
                PublishedEquipmentId,
                "Máy quay Sony FX3",
                "Sony",
                1200000m,
                Guid.NewGuid(),
                Guid.NewGuid(),
                new CategoryDto(Guid.NewGuid(), "Camera"));

            var result = new PagedResult<EquipmentListItemDto>([item], request.Page, request.PageSize, 1);
            return Task.FromResult(result);
        }

        public Task<EquipmentDetailDto?> GetPublishedByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            if (id != PublishedEquipmentId)
            {
                return Task.FromResult<EquipmentDetailDto?>(null);
            }

            var detail = new EquipmentDetailDto(
                PublishedEquipmentId,
                "CAM-001",
                "Máy quay Sony FX3",
                "Sony",
                "Mô tả máy quay",
                1200000m,
                Guid.NewGuid(),
                new CategoryDto(Guid.NewGuid(), "Camera"),
                null,
                [Guid.NewGuid()]);

            return Task.FromResult<EquipmentDetailDto?>(detail);
        }
    }
}
