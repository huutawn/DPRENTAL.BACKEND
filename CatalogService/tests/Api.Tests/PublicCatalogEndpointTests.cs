using System.Net;
using System.Net.Http.Json;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class PublicCatalogEndpointTests
{
    private static readonly Guid PublishedEquipmentTypeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UnpublishedEquipmentTypeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Listing_IsAnonymous_AndReturnsPagedResult()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/catalog/equipment-types");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<EquipmentTypeListItemDto>>();
        Assert.NotNull(page);
        Assert.Equal(1, page.Page);
        Assert.Equal(20, page.PageSize);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1, page.TotalPages);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Listing_WithUnexpectedQueryKey_ReturnsBadRequest()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/catalog/equipment-types?minPrice=500");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Detail_ReturnsPublishedEquipmentType()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync($"/api/v1/catalog/equipment-types/{PublishedEquipmentTypeId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<EquipmentTypeDetailDto>();
        Assert.NotNull(detail);
        Assert.Equal(PublishedEquipmentTypeId, detail.Id);
        Assert.Equal(EquipmentTypeStatus.Published, detail.Status);
    }

    [Fact]
    public async Task Detail_DoesNotExposeUnknownOrUnpublishedEquipmentType()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync($"/api/v1/catalog/equipment-types/{UnpublishedEquipmentTypeId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseTestJwks();

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IEquipmentTypeRepository>();
                    services.AddScoped<IEquipmentTypeRepository, FakeEquipmentTypeRepository>();
                });
            });

    private sealed class FakeEquipmentTypeRepository : IEquipmentTypeRepository
    {
        public Task<PagedResult<EquipmentTypeListItemDto>> SearchPublishedAsync(
            GetEquipmentTypesRequest request,
            CancellationToken cancellationToken = default)
        {
            var item = new EquipmentTypeListItemDto(
                PublishedEquipmentTypeId,
                "CAM-001",
                "Máy quay Sony FX3",
                "Mô tả máy quay",
                [new CategoryDto(Guid.NewGuid(), "Camera", null, null)],
                Guid.NewGuid(),
                null);

            var result = new PagedResult<EquipmentTypeListItemDto>([item], request.Page, request.PageSize, 1);
            return Task.FromResult(result);
        }

        public Task<EquipmentTypeDetailDto?> GetPublishedByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            if (id != PublishedEquipmentTypeId)
            {
                return Task.FromResult<EquipmentTypeDetailDto?>(null);
            }

            var mediaId = Guid.NewGuid();
            var detail = new EquipmentTypeDetailDto(
                PublishedEquipmentTypeId,
                "CAM-001",
                "Máy quay Sony FX3",
                "Mô tả máy quay",
                EquipmentTypeStatus.Published,
                DateTime.UtcNow,
                null,
                DateTime.UtcNow,
                [new CategoryDto(Guid.NewGuid(), "Camera", null, null)],
                [new EquipmentTypeMediaResponseDto(mediaId, null, 0, true)]);

            return Task.FromResult<EquipmentTypeDetailDto?>(detail);
        }

        public Task<EquipmentType?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<EquipmentType?>(null);

        public Task<EquipmentTypeDetailDto?> GetDetailByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<EquipmentTypeDetailDto?>(null);

        public Task<bool> ExistsByCodeAsync(string code, Guid? excludeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<Result<EquipmentTypeDetailDto>> AddAsync(EquipmentType equipmentType, IReadOnlyList<EquipmentTypeMediaItemDto?>? mediaItems, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Result<EquipmentTypeDetailDto>> SaveAsync(EquipmentType entity, IReadOnlyList<EquipmentTypeMediaItemDto?>? desiredMedia, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Result<EquipmentTypeDetailDto>> SaveStateTransitionAsync(EquipmentType entity, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<EquipmentType?> GetWithSchemaAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<EquipmentType?>(null);
    }
}
