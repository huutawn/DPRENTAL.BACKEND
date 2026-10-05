using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.CatalogService.Application.Options;
using Dprental.CatalogService.Application.Services;
using Dprental.CatalogService.Application.Validators;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;
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
        var request = new GetEquipmentTypesRequest { Page = page };

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
        var request = new GetEquipmentTypesRequest { PageSize = pageSize };

        Result result = validator.Validate(request);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Paging.InvalidPageSize, result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public void Validation_PagingOverflow_Fails()
    {
        var validator = CreateValidator();
        var request = new GetEquipmentTypesRequest { Page = int.MaxValue, PageSize = 100 };

        Result result = validator.Validate(request);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Paging.Overflow, result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public void Validation_KeywordTooLong_Fails()
    {
        var validator = CreateValidator();
        var request = new GetEquipmentTypesRequest { Keyword = new string('a', 101) };

        Result result = validator.Validate(request);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.EquipmentType.InvalidKeyword, result.Error.Code);
    }

    [Theory]
    [InlineData("invalid_sort_code")]
    [InlineData("price_asc")]
    [InlineData("price_desc")]
    public void Validation_InvalidSort_Fails(string sort)
    {
        var validator = CreateValidator();
        var request = new GetEquipmentTypesRequest { Sort = sort };

        Result result = validator.Validate(request);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.EquipmentType.InvalidSort, result.Error.Code);
    }

    [Theory]
    [InlineData("newest")]
    [InlineData("name_asc")]
    [InlineData("name_desc")]
    [InlineData("NEWEST")]
    [InlineData("NAME_ASC")]
    [InlineData("NAME_DESC")]
    [InlineData(null)]
    [InlineData("")]
    public void Validation_ValidSort_Succeeds(string? sort)
    {
        var validator = CreateValidator();
        var request = new GetEquipmentTypesRequest { Sort = sort };

        Result result = validator.Validate(request);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Service_DoesNotCallRepository_WhenValidationFails()
    {
        var repoMock = new TrackingEquipmentTypeRepository();
        var validator = CreateValidator();
        var service = new EquipmentTypeService(
            repoMock,
            new FakeCategoryRepository(),
            new DummyMediaServiceClient(),
            validator,
            new CreateEquipmentTypeRequestValidator(),
            new UpdateEquipmentTypeRequestValidator(),
            new Dprental.CatalogService.Api.Security.CatalogCallerContext((System.Security.Claims.ClaimsPrincipal?)null),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EquipmentTypeService>.Instance);

        var badRequest = new GetEquipmentTypesRequest { Page = 0 };
        Result<PagedResult<EquipmentTypeListItemDto>> result = await service.SearchAsync(badRequest);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Paging.InvalidPage, result.Error.Code);
        Assert.Equal(0, repoMock.SearchCallCount);
    }

    [Fact]
    public async Task Api_ModelBindingFailure_ForNonNumericPage_ReturnsBadRequest()
    {
        using WebApplicationFactory<Program> factory = PublicCatalogEndpointTests.CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/catalog/equipment-types?page=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/catalog/equipment-types?page=0", CatalogErrorCode.Paging.InvalidPage)]
    [InlineData("/api/v1/catalog/equipment-types?pageSize=0", CatalogErrorCode.Paging.InvalidPageSize)]
    [InlineData("/api/v1/catalog/equipment-types?pageSize=150", CatalogErrorCode.Paging.InvalidPageSize)]
    [InlineData("/api/v1/catalog/equipment-types?sort=random", CatalogErrorCode.EquipmentType.InvalidSort)]
    [InlineData("/api/v1/catalog/equipment-types?sort=price_asc", CatalogErrorCode.EquipmentType.InvalidSort)]
    [InlineData("/api/v1/catalog/equipment-types?minPrice=-1", CatalogErrorCode.EquipmentType.InvalidQuery)]
    [InlineData("/api/v1/catalog/equipment-types?maxPrice=-1", CatalogErrorCode.EquipmentType.InvalidQuery)]
    [InlineData("/api/v1/catalog/equipment-types?regionId=c0ffee00-0000-0000-0000-000000000000", CatalogErrorCode.EquipmentType.InvalidQuery)]
    public async Task Api_ValidationFailures_ReturnProblemDetails_WithCorrectErrorCode(string queryUrl, string expectedCode)
    {
        using WebApplicationFactory<Program> factory = PublicCatalogEndpointTests.CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(queryUrl);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedCode, problem.GetProperty("title").GetString());
    }

    private static EquipmentTypeListQueryValidator CreateValidator()
    {
        return new EquipmentTypeListQueryValidator(Options.Create(new CatalogDiscoveryOptions { MaxKeywordLength = 100 }));
    }

    private sealed class TrackingEquipmentTypeRepository : IEquipmentTypeRepository
    {
        public int SearchCallCount { get; private set; }

        public Task<PagedResult<EquipmentTypeListItemDto>> SearchPublishedAsync(GetEquipmentTypesRequest request, CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            return Task.FromResult(new PagedResult<EquipmentTypeListItemDto>([], 1, 20, 0));
        }

        public Task<EquipmentTypeDetailDto?> GetPublishedByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<EquipmentTypeDetailDto?>(null);

        public Task<EquipmentType?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<EquipmentType?>(null);

        public Task<EquipmentTypeDetailDto?> GetDetailByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<EquipmentTypeDetailDto?>(null);

        public Task<bool> ExistsByCodeAsync(string code, Guid? excludeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<Result<EquipmentTypeDetailDto>> AddAsync(EquipmentType equipmentType, IReadOnlyList<EquipmentTypeMediaItemDto?>? mediaItems, EquipmentTypeSpecDefinition? initialSpecSchema = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Result<EquipmentTypeDetailDto>> SaveAsync(EquipmentType entity, IReadOnlyList<EquipmentTypeMediaItemDto?>? desiredMedia, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Result<EquipmentTypeDetailDto>> SaveStateTransitionAsync(EquipmentType entity, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<EquipmentTypeSpecSchemaDto>> GetSpecSchemasAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EquipmentTypeSpecSchemaDto>>([]);

        public Task<EquipmentTypeSpecSchemaDto?> GetSpecSchemaAsync(Guid equipmentTypeId, int version, CancellationToken cancellationToken = default) =>
            Task.FromResult<EquipmentTypeSpecSchemaDto?>(null);

        public Task<EquipmentTypeSpecSchema?> GetDraftSpecSchemaEntityAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<EquipmentTypeSpecSchema?>(null);

        public Task<EquipmentTypeSpecSchema?> GetSpecSchemaEntityAsync(Guid equipmentTypeId, int version, CancellationToken cancellationToken = default) =>
            Task.FromResult<EquipmentTypeSpecSchema?>(null);

        public Task<int> GetMaxSpecSchemaVersionAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<Result<EquipmentTypeSpecSchemaDto>> AddSpecSchemaDraftAsync(EquipmentType parent, EquipmentTypeSpecDefinition definition, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Result<EquipmentTypeSpecSchemaDto>> UpdateSpecSchemaDraftAsync(EquipmentType parent, EquipmentTypeSpecSchema draftSchema, EquipmentTypeSpecDefinition newDefinition, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Result<EquipmentTypeSpecSchemaDto>> PublishSpecSchemaAsync(EquipmentType parent, EquipmentTypeSpecSchema draftSchema, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeCategoryRepository : ICategoryRepository
    {
        public Task<Category?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Category?>(null);

        public Task<bool> IsSlugExistsAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task AddAsync(Category category, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateAsync(Category category, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IEnumerable<Category>> GetAllActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IEnumerable<Category>>([]);
    }

    private sealed class DummyMediaServiceClient : Dprental.CatalogService.Application.Clients.IMediaServiceClient
    {
        public Task<Result<Dprental.CatalogService.Application.DTOs.Media.MediaVerificationDto>> VerifyMediaAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new Dprental.CatalogService.Application.DTOs.Media.MediaVerificationDto(mediaId, "Ready", "test-user", "test.jpg", "image/jpeg", 1024, DateTimeOffset.UtcNow)));

        public Task<Result> SetVisibilityAsync(Guid mediaId, string visibility, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }
}
