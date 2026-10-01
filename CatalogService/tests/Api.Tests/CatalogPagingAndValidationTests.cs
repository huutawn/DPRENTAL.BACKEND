using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.CatalogService.Application.Options;
using Dprental.CatalogService.Application.Services;
using Dprental.CatalogService.Application.Validators;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.Equipments;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class CatalogPagingAndValidationTests
{
    private static readonly JsonSerializerOptions CamelCaseOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void PagedResult_Contract_HasTotalCount_AndNoTotalItems()
    {
        var items = new List<string> { "item1", "item2" };
        var paged = new PagedResult<string>(items, page: 1, pageSize: 20, totalCount: 2);

        string json = JsonSerializer.Serialize(paged, CamelCaseOptions);
        using JsonDocument doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("totalCount", out JsonElement totalCountProp));
        Assert.Equal(2, totalCountProp.GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("totalItems", out _));
        Assert.False(doc.RootElement.TryGetProperty("TotalItems", out _));
        Assert.Equal(1, paged.TotalPages);
    }

    [Fact]
    public void PagedResult_WithEmptyData_ReturnsZeroTotalPages()
    {
        var paged = new PagedResult<string>([], page: 1, pageSize: 20, totalCount: 0);

        Assert.Equal(0, paged.TotalCount);
        Assert.Equal(0, paged.TotalPages);
        Assert.Empty(paged.Items);
    }

    [Fact]
    public void PagedResult_PageExceedsTotalPages_ReturnsExpectedTotalPages()
    {
        var paged = new PagedResult<string>([], page: 5, pageSize: 20, totalCount: 2);

        Assert.Equal(2, paged.TotalCount);
        Assert.Equal(1, paged.TotalPages);
        Assert.Equal(5, paged.Page);
        Assert.Empty(paged.Items);
    }

    [Theory]
    [InlineData(41, 20, 3)]
    [InlineData(40, 20, 2)]
    [InlineData(1, 20, 1)]
    [InlineData(100, 10, 10)]
    public void PagedResult_CalculatesTotalPagesCorrectly(int totalCount, int pageSize, int expectedTotalPages)
    {
        var paged = new PagedResult<string>([], page: 1, pageSize: pageSize, totalCount: totalCount);

        Assert.Equal(expectedTotalPages, paged.TotalPages);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validation_InvalidPage_Fails(int page)
    {
        var validator = CreateValidator();
        var request = new GetEquipmentsRequest { Page = page };

        Result result = validator.Validate(request);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Paging.InvalidPage, result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    [InlineData(200)]
    public void Validation_InvalidPageSize_Fails(int pageSize)
    {
        var validator = CreateValidator();
        var request = new GetEquipmentsRequest { PageSize = pageSize };

        Result result = validator.Validate(request);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Paging.InvalidPageSize, result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public void Validation_PagingOverflow_Fails()
    {
        var validator = CreateValidator();
        var request = new GetEquipmentsRequest { Page = int.MaxValue, PageSize = 100 };

        Result result = validator.Validate(request);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Paging.Overflow, result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public void Validation_KeywordTooLong_Fails()
    {
        var validator = CreateValidator();
        var request = new GetEquipmentsRequest { Keyword = new string('a', 101) };

        Result result = validator.Validate(request);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Equipment.InvalidKeyword, result.Error.Code);
    }

    [Fact]
    public void Validation_InvalidSort_Fails()
    {
        var validator = CreateValidator();
        var request = new GetEquipmentsRequest { Sort = "invalid_sort_code" };

        Result result = validator.Validate(request);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Equipment.InvalidSort, result.Error.Code);
    }

    [Theory]
    [InlineData("newest")]
    [InlineData("price_asc")]
    [InlineData("price_desc")]
    [InlineData("NEWEST")]
    [InlineData("PRICE_ASC")]
    [InlineData(null)]
    [InlineData("")]
    public void Validation_ValidSort_Succeeds(string? sort)
    {
        var validator = CreateValidator();
        var request = new GetEquipmentsRequest { Sort = sort };

        Result result = validator.Validate(request);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Validation_NegativeMinPrice_Fails()
    {
        var validator = CreateValidator();
        var request = new GetEquipmentsRequest { MinPrice = -10m };

        Result result = validator.Validate(request);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Equipment.InvalidMinPrice, result.Error.Code);
    }

    [Fact]
    public void Validation_NegativeMaxPrice_Fails()
    {
        var validator = CreateValidator();
        var request = new GetEquipmentsRequest { MaxPrice = -5m };

        Result result = validator.Validate(request);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Equipment.InvalidMaxPrice, result.Error.Code);
    }

    [Fact]
    public void Validation_MinPriceGreaterThanMaxPrice_Fails()
    {
        var validator = CreateValidator();
        var request = new GetEquipmentsRequest { MinPrice = 500m, MaxPrice = 100m };

        Result result = validator.Validate(request);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Equipment.InvalidPriceRange, result.Error.Code);
    }

    [Fact]
    public async Task Service_DoesNotCallRepository_WhenValidationFails()
    {
        var repoMock = new TrackingEquipmentRepository();
        var validator = CreateValidator();
        var service = new CatalogDiscoveryService(repoMock, validator);

        var badRequest = new GetEquipmentsRequest { Page = 0 };
        Result<PagedResult<EquipmentListItemDto>> result = await service.SearchAsync(badRequest);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Paging.InvalidPage, result.Error.Code);
        Assert.Equal(0, repoMock.SearchCallCount);
    }

    [Fact]
    public async Task Api_ModelBindingFailure_ForNonNumericPage_ReturnsBadRequest()
    {
        using WebApplicationFactory<Program> factory = PublicCatalogEndpointTests.CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/catalog/equipments?page=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/catalog/equipments?page=0", CatalogErrorCode.Paging.InvalidPage)]
    [InlineData("/api/v1/catalog/equipments?pageSize=0", CatalogErrorCode.Paging.InvalidPageSize)]
    [InlineData("/api/v1/catalog/equipments?pageSize=150", CatalogErrorCode.Paging.InvalidPageSize)]
    [InlineData("/api/v1/catalog/equipments?minPrice=-1", CatalogErrorCode.Equipment.InvalidMinPrice)]
    [InlineData("/api/v1/catalog/equipments?maxPrice=-1", CatalogErrorCode.Equipment.InvalidMaxPrice)]
    [InlineData("/api/v1/catalog/equipments?minPrice=200&maxPrice=100", CatalogErrorCode.Equipment.InvalidPriceRange)]
    [InlineData("/api/v1/catalog/equipments?sort=random", CatalogErrorCode.Equipment.InvalidSort)]
    public async Task Api_ValidationFailures_ReturnProblemDetails_WithCorrectErrorCode(string queryUrl, string expectedCode)
    {
        using WebApplicationFactory<Program> factory = PublicCatalogEndpointTests.CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(queryUrl);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedCode, problem.GetProperty("title").GetString());
    }

    private static EquipmentListQueryValidator CreateValidator()
    {
        return new EquipmentListQueryValidator(Options.Create(new CatalogDiscoveryOptions { MaxKeywordLength = 100 }));
    }

    private sealed class TrackingEquipmentRepository : IEquipmentQueryRepository
    {
        public int SearchCallCount { get; private set; }

        public Task<PagedResult<EquipmentListItemDto>> SearchPublishedAsync(GetEquipmentsRequest request, CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            return Task.FromResult(new PagedResult<EquipmentListItemDto>([], 1, 20, 0));
        }

        public Task<EquipmentDetailDto?> GetPublishedByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<EquipmentDetailDto?>(null);
        }
    }
}
