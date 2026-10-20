using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Common;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class AssetSearchFilterResolverTests
{
    private sealed class StubCatalogClient : ICatalogServiceClient
    {
        public Func<Guid, IReadOnlyList<int>, Task<Result<Dprental.InventoryService.Application.Caching.CachedRead<CatalogBatchSchemasResponse>>>>? BatchHandler { get; set; }
        public int BatchCalls { get; private set; }

        public Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Result<CatalogBrandResponse>> GetBrandAsync(Guid brandId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public async Task<Result<Dprental.InventoryService.Application.Caching.CachedRead<CatalogBatchSchemasResponse>>> GetEquipmentTypeSchemasBatchAsync(Guid equipmentTypeId, IReadOnlyList<int> versions, CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            if (BatchHandler != null)
            {
                return await BatchHandler(equipmentTypeId, versions);
            }
            return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<CatalogBatchSchemasResponse>(
                new CatalogBatchSchemasResponse(equipmentTypeId, 1, [new CatalogBatchSchemaItemResponse(1, [])]),
                DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public Task<Result<Dprental.InventoryService.Application.Caching.CachedRead<IReadOnlyList<Guid>>>> GetEquipmentTypeIdsByCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<IReadOnlyList<Guid>>(Array.Empty<Guid>(), DateTimeOffset.UtcNow.AddMinutes(5))));
    }

    private readonly StubCatalogClient _catalogClient = new();
    private readonly AssetSearchFilterResolver _resolver;

    public AssetSearchFilterResolverTests()
    {
        _resolver = new AssetSearchFilterResolver(_catalogClient);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    public async Task ResolveAsync_EmptyFilters_ReturnsNullCriteriaWithoutCallingRemoteOrVersions(string? rawFilters)
    {
        int versionCallCount = 0;
        var result = await _resolver.ResolveAsync(
            rawFilters,
            Guid.NewGuid(),
            _ => { versionCallCount++; return Task.FromResult<IReadOnlyList<int>>([1]); });

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Criteria);
        Assert.Null(result.Value.CompatibleSchemaVersions);
        Assert.Equal(0, versionCallCount);
        Assert.Equal(0, _catalogClient.BatchCalls);
    }

    [Fact]
    public async Task ResolveAsync_NullStringLiteral_ReturnsError()
    {
        var result = await _resolver.ResolveAsync(
            "null",
            Guid.NewGuid(),
            _ => Task.FromResult<IReadOnlyList<int>>([1]));

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_QUERY_PARAMETER", result.Error.Code);
    }

    [Fact]
    public async Task ResolveAsync_NotJsonArray_ReturnsError()
    {
        var result = await _resolver.ResolveAsync(
            "{\"field\":\"capacity\",\"operator\":\"eq\",\"value\":1}",
            Guid.NewGuid(),
            _ => Task.FromResult<IReadOnlyList<int>>([1]));

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_QUERY_PARAMETER", result.Error.Code);
    }

    [Fact]
    public async Task ResolveAsync_Exceeds20Conditions_ReturnsError()
    {
        var items = Enumerable.Range(1, 21).Select(i => $"{{\"field\":\"f{i}\",\"operator\":\"eq\",\"value\":\"val\"}}");
        var json = $"[{string.Join(",", items)}]";

        var result = await _resolver.ResolveAsync(
            json,
            Guid.NewGuid(),
            _ => Task.FromResult<IReadOnlyList<int>>([1]));

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_QUERY_PARAMETER", result.Error.Code);
    }

    [Fact]
    public async Task ResolveAsync_NonemptyFiltersWithoutEquipmentType_ReturnsError()
    {
        var json = "[{\"field\":\"capacity\",\"operator\":\"eq\",\"value\":1000}]";

        var result = await _resolver.ResolveAsync(
            json,
            null,
            _ => Task.FromResult<IReadOnlyList<int>>([1]));

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_QUERY_PARAMETER", result.Error.Code);
    }

    [Fact]
    public async Task ResolveAsync_ForbiddenFieldProvided_RejectsBeforeCallingRemote()
    {
        var json = "[{\"field\":\"ownerUserId\",\"operator\":\"eq\",\"value\":\"secret\"}]";
        int versionCalls = 0;

        var result = await _resolver.ResolveAsync(
            json,
            Guid.NewGuid(),
            _ => { versionCalls++; return Task.FromResult<IReadOnlyList<int>>([1]); },
            isFieldForbidden: f => string.Equals(f, "ownerUserId", StringComparison.OrdinalIgnoreCase));

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_QUERY_PARAMETER", result.Error.Code);
        Assert.Equal(0, versionCalls);
        Assert.Equal(0, _catalogClient.BatchCalls);
    }

    [Fact]
    public async Task ResolveAsync_ValidNumberFilter_ParsesAndDeterminesCompatibleVersions()
    {
        var eqId = Guid.NewGuid();
        _catalogClient.BatchHandler = (id, versions) => Task.FromResult(Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<CatalogBatchSchemasResponse>(new CatalogBatchSchemasResponse(
            id,
            2,
            [
                new CatalogBatchSchemaItemResponse(1, [
                    new CatalogBatchFieldResponse("weightKg", "Weight", "number", true, true, false, 0, 0, 10000, null, "kg", null)
                ]),
                new CatalogBatchSchemaItemResponse(2, [
                    new CatalogBatchFieldResponse("weightKg", "Weight", "number", true, true, false, 0, 0, 10000, null, "kg", null)
                ])
            ]), DateTimeOffset.UtcNow.AddMinutes(5))));

        var json = "[{\"field\":\"weightKg\",\"operator\":\"gte\",\"value\":2500}]";
        var result = await _resolver.ResolveAsync(
            json,
            eqId,
            _ => Task.FromResult<IReadOnlyList<int>>([1, 2]));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value.Criteria);
        Assert.Single(result.Value.Criteria);
        var crit = result.Value.Criteria[0];
        Assert.Equal("weightKg", crit.Field);
        Assert.Equal("gte", crit.Operator);
        Assert.Equal(2500m, crit.Value);
        Assert.Equal("number", crit.DataType);

        Assert.NotNull(result.Value.CompatibleSchemaVersions);
        Assert.Equal(2, result.Value.CompatibleSchemaVersions.Count);
    }

    [Fact]
    public async Task ResolveAsync_IncompatibleHistoricalSchemaUnit_ExcludesIncompatibleVersion()
    {
        var eqId = Guid.NewGuid();
        _catalogClient.BatchHandler = (id, versions) => Task.FromResult(Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<CatalogBatchSchemasResponse>(new CatalogBatchSchemasResponse(
            id,
            2,
            [
                new CatalogBatchSchemaItemResponse(1, [
                    new CatalogBatchFieldResponse("weight", "Weight", "number", true, true, false, 0, 0, 10000, null, "lbs", null) // lbs != kg
                ]),
                new CatalogBatchSchemaItemResponse(2, [
                    new CatalogBatchFieldResponse("weight", "Weight", "number", true, true, false, 0, 0, 10000, null, "kg", null)
                ])
            ]), DateTimeOffset.UtcNow.AddMinutes(5))));

        var json = "[{\"field\":\"weight\",\"operator\":\"eq\",\"value\":1000}]";
        var result = await _resolver.ResolveAsync(
            json,
            eqId,
            _ => Task.FromResult<IReadOnlyList<int>>([1, 2]));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value.CompatibleSchemaVersions);
        // Only version 2 is compatible!
        var comp = Assert.Single(result.Value.CompatibleSchemaVersions);
        Assert.Equal(2, comp);
    }
}
