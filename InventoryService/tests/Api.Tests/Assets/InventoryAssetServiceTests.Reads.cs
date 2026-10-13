using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Common;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed partial class InventoryAssetServiceTests
{
    [Fact]
    public async Task GetAssetByIdAsync_OwnershipIsolation_RejectsNonOwnerWith403()
    {
        var service = CreateService();
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();

        var asset = CreateAssetWithRate(ownerUserId: ownerA);
        _repo.Add(asset);

        // Caller B tries to access asset of A
        var callerB = new TestCaller(ownerB);
        var result = await service.GetAssetByIdAsync(asset.Id, callerB);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
    }

    [Fact]
    public async Task GetAssetByIdAsync_AdminBypass_CanReadAsset()
    {
        var service = CreateService();
        var ownerA = Guid.NewGuid();
        var admin = new TestCaller(Guid.NewGuid(), isAdmin: true);

        var asset = CreateAssetWithRate(ownerUserId: ownerA);
        _repo.Add(asset);

        var result = await service.GetAssetByIdAsync(asset.Id, admin);

        Assert.True(result.IsSuccess);
        Assert.Equal(asset.Id, result.Value.Id);
    }

    [Fact]
    public async Task ListAssetsAsync_AlwaysFiltersByCallerUser_EvenForAdmin()
    {
        var service = CreateService();
        var ownerA = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        var assetA = CreateAssetWithRate(ownerUserId: ownerA, model: "FX3 A");
        var assetAdmin = CreateAssetWithRate(ownerUserId: adminId, model: "FX3 Admin");
        _repo.Add(assetA);
        _repo.Add(assetAdmin);

        var adminCaller = new TestCaller(adminId, isAdmin: true);
        var result = await service.ListAssetsAsync(new ListAssetsRequest(Page: 1, PageSize: 20), adminCaller);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Value.Items);
        Assert.Equal(assetAdmin.Id, result.Value.Value.Items[0].Id);
    }

    [Fact]
    public async Task ListAssetsAsync_PagingOffsetOverflow_Returns400()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var result = await service.ListAssetsAsync(new ListAssetsRequest(Page: int.MaxValue, PageSize: 100), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_PAGE", result.Error.Code);
    }

    [Fact]
    public async Task ListAssetsAsync_WithoutDynamicFilters_DoesNotCallCatalog()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var result = await service.ListAssetsAsync(new ListAssetsRequest(), caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, _catalogClient.BatchSchemasCalls);
    }

    [Fact]
    public async Task ListAssetsAsync_EmptyFiltersStringOrArray_DoesNotCallCatalog()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var r1 = await service.ListAssetsAsync(new ListAssetsRequest(Filters: ""), caller);
        var r2 = await service.ListAssetsAsync(new ListAssetsRequest(Filters: "[]"), caller);

        Assert.True(r1.IsSuccess);
        Assert.True(r2.IsSuccess);
        Assert.Equal(0, _catalogClient.BatchSchemasCalls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("not-a-json")]
    [InlineData("{\"field\":\"x\"}")]
    public async Task ListAssetsAsync_MalformedFiltersJson_Returns400(string malformed)
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var result = await service.ListAssetsAsync(new ListAssetsRequest(Filters: malformed), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task ListAssetsAsync_DynamicFiltersWithoutEquipmentTypeId_Returns400()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var filters = "[{\"field\":\"capacityKg\",\"operator\":\"gte\",\"value\":2500}]";
        var result = await service.ListAssetsAsync(new ListAssetsRequest(Filters: filters, EquipmentTypeId: null), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task ListAssetsAsync_DynamicFiltersExceeding20Conditions_Returns400()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var filterItems = Enumerable.Range(1, 21)
            .Select(i => $"{{\"field\":\"f{i}\",\"operator\":\"eq\",\"value\":\"val\"}}");
        var filters = $"[{string.Join(",", filterItems)}]";

        var result = await service.ListAssetsAsync(new ListAssetsRequest(Filters: filters, EquipmentTypeId: Guid.NewGuid()), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Theory]
    [InlineData("[{\"field\":\"c\",\"operator\":\"eq\",\"value\":1,\"extra\":true}]")]
    [InlineData("[{\"field\":\"c\",\"operator\":\"eq\"}]")]
    [InlineData("[{\"operator\":\"eq\",\"value\":1}]")]
    [InlineData("[{\"field\":\"\",\"operator\":\"eq\",\"value\":1}]")]
    [InlineData("[{\"field\":\"c\",\"operator\":\"\",\"value\":1}]")]
    [InlineData("[{\"field\":\"c\",\"operator\":\"invalid_op\",\"value\":1}]")]
    public async Task ListAssetsAsync_InvalidFilterObjectStructure_Returns400(string badFilterJson)
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var result = await service.ListAssetsAsync(new ListAssetsRequest(Filters: badFilterJson, EquipmentTypeId: Guid.NewGuid()), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task ListAssetsAsync_SearchKeywordExceeds200Chars_Returns400()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var longKeyword = new string('a', 201);
        var result = await service.ListAssetsAsync(new ListAssetsRequest(Q: longKeyword), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task ListAssetsAsync_EmptyUuid_Returns400()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var r1 = await service.ListAssetsAsync(new ListAssetsRequest(EquipmentTypeId: Guid.Empty), caller);
        var r2 = await service.ListAssetsAsync(new ListAssetsRequest(BrandId: Guid.Empty), caller);

        Assert.True(r1.IsFailure);
        Assert.True(r2.IsFailure);
    }

    [Theory]
    [InlineData(1899, 2024)]
    [InlineData(2024, 2050)]
    [InlineData(2025, 2020)]
    public async Task ListAssetsAsync_InvalidManufactureYears_Returns400(int from, int to)
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var result = await service.ListAssetsAsync(new ListAssetsRequest(ManufactureYearFrom: from, ManufactureYearTo: to), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Theory]
    [InlineData("invalid_sort")]
    [InlineData("date_desc")]
    public async Task ListAssetsAsync_InvalidSortOption_Returns400(string sort)
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var result = await service.ListAssetsAsync(new ListAssetsRequest(Sort: sort), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task ListAssetsAsync_FieldMissingOrNotFilterableInCurrentSchema_Returns400()
    {
        var eqId = Guid.NewGuid();
        _catalogClient.BatchSchemasHandler = (id, versions) => Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(
            id,
            1,
            [
                new CatalogBatchSchemaItemResponse(1, [
                    new CatalogBatchFieldResponse("allowed", "Allowed", "text", true, false, false, 0, null, null, null, null, null)
                ])
            ])));

        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var r1 = await service.ListAssetsAsync(new ListAssetsRequest(EquipmentTypeId: eqId, Filters: "[{\"field\":\"missing\",\"operator\":\"eq\",\"value\":\"x\"}]"), caller);
        Assert.True(r1.IsFailure);
        Assert.Equal(ErrorType.Validation, r1.Error.Type);

        var r2 = await service.ListAssetsAsync(new ListAssetsRequest(EquipmentTypeId: eqId, Filters: "[{\"field\":\"allowed\",\"operator\":\"eq\",\"value\":\"x\"}]"), caller);
        Assert.True(r2.IsFailure);
        Assert.Equal(ErrorType.Validation, r2.Error.Type);
    }

    [Fact]
    public async Task ListAssetsAsync_EnumFilterWithInvalidOption_Returns400()
    {
        var eqId = Guid.NewGuid();
        _catalogClient.BatchSchemasHandler = (id, versions) => Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(
            id,
            1,
            [
                new CatalogBatchSchemaItemResponse(1, [
                    new CatalogBatchFieldResponse("fuelType", "Fuel", "enum", true, true, false, 0, null, null,
                        [new CatalogBatchOptionResponse("diesel", "Diesel"), new CatalogBatchOptionResponse("electric", "Electric")],
                        null, null)
                ])
            ])));

        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var result = await service.ListAssetsAsync(new ListAssetsRequest(EquipmentTypeId: eqId, Filters: "[{\"field\":\"fuelType\",\"operator\":\"eq\",\"value\":\"gasoline\"}]"), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task ListAssetsAsync_HistoricalVersionIncompatible_ExcludedFromSearch()
    {
        var ownerId = Guid.NewGuid();
        var eqId = Guid.NewGuid();

        var asset1 = CreateAssetWithRate(ownerUserId: ownerId, eqTypeId: eqId, model: "Model V1", schemaVersion: 1, specValues: "{\"capacity\": 3000}");
        var asset2 = CreateAssetWithRate(ownerUserId: ownerId, eqTypeId: eqId, model: "Model V2", schemaVersion: 2, specValues: "{\"capacity\": 3000}");
        _repo.Add(asset1);
        _repo.Add(asset2);

        _catalogClient.BatchSchemasHandler = (id, versions) => Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(
            id,
            2,
            [
                new CatalogBatchSchemaItemResponse(1, [
                    new CatalogBatchFieldResponse("capacity", "Cap", "number", true, true, false, 0, 0, 10000, null, "lbs", null)
                ]),
                new CatalogBatchSchemaItemResponse(2, [
                    new CatalogBatchFieldResponse("capacity", "Cap", "number", true, true, false, 0, 0, 10000, null, "kg", null)
                ])
            ])));

        var service = CreateService();
        var caller = new TestCaller(ownerId);

        var result = await service.ListAssetsAsync(new ListAssetsRequest(
            EquipmentTypeId: eqId,
            Filters: "[{\"field\":\"capacity\",\"operator\":\"gte\",\"value\":2500}]"), caller);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Value.Items);
        Assert.Equal(asset2.Id, result.Value.Value.Items[0].Id);
    }

    [Theory]
    [InlineData("1e100")]
    [InlineData("-1e100")]
    [InlineData("1e309")]
    public async Task ListAssetsAsync_NumberFilter_DecimalOverflow_Returns400(string overflowNumber)
    {
        var eqId = Guid.NewGuid();
        _catalogClient.BatchSchemasHandler = (id, versions) => Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(
            id,
            1,
            [
                new CatalogBatchSchemaItemResponse(1, [
                    new CatalogBatchFieldResponse("capacity", "Cap", "number", true, true, false, 0, null, null, null, null, null)
                ])
            ])));

        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var filterJson = $"[{{\"field\":\"capacity\",\"operator\":\"gte\",\"value\":{overflowNumber}}}]";
        var result = await service.ListAssetsAsync(new ListAssetsRequest(EquipmentTypeId: eqId, Filters: filterJson), caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_QUERY_PARAMETER", result.Error.Code);
    }

    [Fact]
    public async Task ListAssetsAsync_NumberFilter_ValidDecimalBoundaries_Succeeds()
    {
        var eqId = Guid.NewGuid();
        _catalogClient.BatchSchemasHandler = (id, versions) => Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(
            id,
            1,
            [
                new CatalogBatchSchemaItemResponse(1, [
                    new CatalogBatchFieldResponse("capacity", "Cap", "number", true, true, false, 0, null, null, null, null, null)
                ])
            ])));

        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var filterJson = "[{\"field\":\"capacity\",\"operator\":\"lte\",\"value\":79228162514264337593543950335}]";
        var result = await service.ListAssetsAsync(new ListAssetsRequest(EquipmentTypeId: eqId, Filters: filterJson), caller);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ListAssetsAsync_OwnerKeywordSearch_SearchesBothModelAndSerialNumber()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset1 = CreateAssetWithRate(ownerUserId: ownerId, model: "Model Alpha", serialNumber: "SN-999");
        var asset2 = CreateAssetWithRate(ownerUserId: ownerId, model: "Model Beta", serialNumber: "SN-SECRET-MATCH");
        _repo.Add(asset1);
        _repo.Add(asset2);

        // Searching by SerialNumber works for owner!
        var result = await service.ListAssetsAsync(new ListAssetsRequest(Q: "SECRET-MATCH"), caller);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Value.Items);
        Assert.Equal(asset2.Id, result.Value.Value.Items[0].Id);
    }

    [Fact]
    public async Task ListAssetsAsync_OwnerIsolation_NeverReturnsOtherOwnersAssets()
    {
        var service = CreateService();
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var callerA = new TestCaller(ownerA);

        var assetA = CreateAssetWithRate(ownerUserId: ownerA, model: "Asset Owner A");
        var assetB = CreateAssetWithRate(ownerUserId: ownerB, model: "Asset Owner B");
        _repo.Add(assetA);
        _repo.Add(assetB);

        var result = await service.ListAssetsAsync(new ListAssetsRequest(), callerA);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Value.Items);
        Assert.Equal(assetA.Id, result.Value.Value.Items[0].Id);
    }

    [Fact]
    public async Task ListAssetsAsync_OwnerSchemaVersionsLookup_RespectsAcceptingBookingsFilter()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var eqId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        // Asset 1: acceptingBookings = false, schemaVersion = 1
        var a1 = CreateAssetWithRate(ownerUserId: ownerId, eqTypeId: eqId, model: "Disabled V1", schemaVersion: 1);
        a1.DisableBookings("Maintenance", FixedNow);

        // Asset 2: acceptingBookings = true, schemaVersion = 2
        var a2 = CreateAssetWithRate(ownerUserId: ownerId, eqTypeId: eqId, model: "Active V2", schemaVersion: 2);

        _repo.Add(a1);
        _repo.Add(a2);

        IReadOnlyList<int>? versionsRequestedToCatalog = null;
        _catalogClient.BatchSchemasHandler = (typeId, versions) =>
        {
            versionsRequestedToCatalog = versions;
            return Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(
                typeId, 2, [new CatalogBatchSchemaItemResponse(2, [
                    new CatalogBatchFieldResponse("cap", "Cap", "number", true, true, false, 0, 0, 100, null, "t", null)
                ])])));
        };

        // Query with acceptingBookings = true
        var result = await service.ListAssetsAsync(new ListAssetsRequest(
            EquipmentTypeId: eqId,
            AcceptingBookings: true,
            Filters: "[{\"field\":\"cap\",\"operator\":\"eq\",\"value\":10}]"), caller);

        Assert.True(result.IsSuccess);
        // Version 1 (from disabled asset) must NOT be requested to Catalog! Only version 2!
        Assert.NotNull(versionsRequestedToCatalog);
        Assert.Single(versionsRequestedToCatalog);
        Assert.Equal(2, versionsRequestedToCatalog[0]);
    }
}
