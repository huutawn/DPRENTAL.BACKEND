using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Application.Validation;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed partial class InventoryAssetServiceTests
{
    [Fact]
    public async Task CreateAssetAsync_ValidRequest_CreatesAssetAndReturns201Model()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        using var specDoc = JsonDocument.Parse("{\"sensor\":\"Full-Frame\"}");
        var request = new CreateInventoryAssetRequest(
            EquipmentTypeId: eqTypeId,
            BrandId: brandId,
            Model: "Sony FX3",
            SchemaVersion: 1,
            SpecValues: specDoc.RootElement.Clone(),
            RentalRate: new AssetRentalRateDto(500_000m, 3_000_000m, 10_000_000m, "VND"),
            SerialNumber: "SN-001",
            ManufactureYear: 2024);
        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsSuccess);
        var response = result.Value;
        Assert.Equal(ownerId, response.OwnerUserId);
        Assert.Equal(eqTypeId, response.EquipmentTypeId);
        Assert.Equal(brandId, response.BrandId);
        Assert.Equal("Sony FX3", response.Model);
        Assert.Equal("SN-001", response.SerialNumber);
        Assert.Equal(2024, response.ManufactureYear);
        Assert.True(response.AcceptingBookings);
        Assert.Null(response.SuspensionReason);
        Assert.Equal(FixedNow, response.CreatedAt);

        Assert.NotNull(response.RentalRate);
        Assert.Equal(500_000m, response.RentalRate.DailyRate);
        Assert.Equal(3_000_000m, response.RentalRate.WeeklyRate);
        Assert.Equal(10_000_000m, response.RentalRate.MonthlyRate);
        Assert.Equal("VND", response.RentalRate.Currency);
        // Verify stored in repository
        Assert.True(_repo.Store.ContainsKey(response.Id));
        Assert.Equal(brandId, _repo.Store[response.Id].BrandId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task CreateAssetAsync_MissingOrEmptyBrandId_ReturnsValidation(string? brandGuidStr)
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());
        Guid? brandId = brandGuidStr is null ? null : Guid.Parse(brandGuidStr);

        using var doc = JsonDocument.Parse("{}");
        var request = new CreateInventoryAssetRequest(
            Guid.NewGuid(), brandId, "FX3", 1, doc.RootElement, RentalRate: DefaultRateDto);
        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_BRAND_ID", result.Error.Code);
        Assert.Equal(0, _catalogClient.BrandCalls);
    }

    [Fact]
    public async Task CreateAssetAsync_BrandNotFound_ReturnsValidationError()
    {
        var service = CreateService();
        var brandId = Guid.NewGuid();
        _catalogClient.BrandHandler = _ => Task.FromResult(Result.Failure<CatalogBrandResponse>(
            Error.Validation("Brand.NotFound", "Thương hiệu không tồn tại trong Catalog.")));

        var caller = new TestCaller(Guid.NewGuid());
        using var doc = JsonDocument.Parse("{}");
        var request = new CreateInventoryAssetRequest(
            Guid.NewGuid(), brandId, "FX3", 1, doc.RootElement, RentalRate: DefaultRateDto);
        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("Brand.NotFound", result.Error.Code);
        Assert.Empty(_repo.Store);
    }

    [Fact]
    public async Task CreateAssetAsync_BrandInactive_ReturnsValidationError()
    {
        var service = CreateService();
        var brandId = Guid.NewGuid();
        _catalogClient.BrandHandler = id => Task.FromResult(Result.Success(
            new CatalogBrandResponse(id, "BR-OLD", "Inactive Brand", false)));

        var caller = new TestCaller(Guid.NewGuid());
        using var doc = JsonDocument.Parse("{}");
        var request = new CreateInventoryAssetRequest(
            Guid.NewGuid(), brandId, "FX3", 1, doc.RootElement, RentalRate: DefaultRateDto);
        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("Brand.Inactive", result.Error.Code);
        Assert.Empty(_repo.Store);
    }

    [Fact]
    public async Task CreateAssetAsync_Unauthenticated_Returns401()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid(), isAuthenticated: false);

        using var doc = JsonDocument.Parse("{}");
        var request = new CreateInventoryAssetRequest(Guid.NewGuid(), Guid.NewGuid(), "FX3", 1, doc.RootElement, RentalRate: DefaultRateDto);

        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
        Assert.Equal(0, _catalogClient.LookupCalls);
    }

    [Fact]
    public async Task CreateAssetAsync_MissingManagePermission_Returns403()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid(), permissions: new HashSet<string>());

        using var doc = JsonDocument.Parse("{}");
        var request = new CreateInventoryAssetRequest(Guid.NewGuid(), Guid.NewGuid(), "FX3", 1, doc.RootElement, RentalRate: DefaultRateDto);

        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
        Assert.Equal(0, _catalogClient.LookupCalls);
    }

    [Fact]
    public async Task CreateAssetAsync_UnpublishedEquipmentType_ReturnsValidation()
    {
        var service = CreateService();
        _catalogClient.LookupHandler = _ => Task.FromResult(Result.Failure<CatalogEquipmentTypeResponse>(
            Error.Validation("EQUIPMENT_TYPE_NOT_PUBLISHED", "Loại thiết bị chưa được công khai.")));

        var caller = new TestCaller(Guid.NewGuid());
        using var doc = JsonDocument.Parse("{}");
        var request = new CreateInventoryAssetRequest(Guid.NewGuid(), Guid.NewGuid(), "FX3", 1, doc.RootElement, RentalRate: DefaultRateDto);

        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("EQUIPMENT_TYPE_NOT_PUBLISHED", result.Error.Code);
    }

    [Fact]
    public async Task CreateAssetAsync_CatalogSpecValidationFailure_RejectsCreation()
    {
        var service = CreateService();
        _catalogClient.SpecHandler = (_, _, _) => Task.FromResult(Result.Failure(
            Error.Validation("SPEC_VALIDATION_FAILED", "Dữ liệu specification không hợp lệ theo schema version đã công khai.",
                [new ValidationError("sensor", "REQUIRED", "Trường 'sensor' là bắt buộc.")])));

        var caller = new TestCaller(Guid.NewGuid());
        using var doc = JsonDocument.Parse("{}");
        var request = new CreateInventoryAssetRequest(Guid.NewGuid(), Guid.NewGuid(), "FX3", 1, doc.RootElement, RentalRate: DefaultRateDto);

        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("SPEC_VALIDATION_FAILED", result.Error.Code);
        Assert.Equal("Dữ liệu specification không hợp lệ theo schema version đã công khai.", result.Error.Message);
        Assert.NotNull(result.Error.Errors);
        var err = Assert.Single(result.Error.Errors);
        Assert.Equal("sensor", err.Field);
        Assert.Equal("REQUIRED", err.Code);
        Assert.Empty(_repo.Store);
    }

    [Fact]
    public async Task CreateAssetAsync_WithThumbnail_VerifiesAndPreparesPublicMedia()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);
        var thumbId = Guid.NewGuid();

        _mediaClient.VerifyHandler = id => Task.FromResult(Result.Success(new MediaVerificationInfo(id, "Ready", ownerId.ToString("D"))));

        using var doc = JsonDocument.Parse("{}");
        var request = new CreateInventoryAssetRequest(Guid.NewGuid(), Guid.NewGuid(), "FX3", 1, doc.RootElement, RentalRate: DefaultRateDto, ThumbnailMediaId: thumbId);

        var result = await service.CreateAssetAsync(request, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _mediaClient.VerifyCalls);
        Assert.Equal(thumbId, result.Value.ThumbnailMediaId);
    }

    [Fact]
    public async Task PatchAssetAsync_PresenceTracking_UpdatesSpecifiedFieldsOnly()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var originalThumb = Guid.NewGuid();
        var originalBrand = Guid.NewGuid();
        var asset = CreateAssetWithRate(
            ownerUserId: ownerId, brandId: originalBrand,
            model: "Original Model", schemaVersion: 1, specValues: "{\"k\":\"v\"}",
            serialNumber: "SN-ORIG",
            manufactureYear: 2022,
            thumbnailMediaId: originalThumb);
        _repo.Add(asset);
        var patch = new PatchInventoryAssetRequest
        {
            IsModelSpecified = true,
            Model = "Updated Model",
            IsSerialNumberSpecified = true,
            SerialNumber = null
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal("Updated Model", result.Value.Model);
        Assert.Equal(originalBrand, result.Value.BrandId);
        Assert.Null(result.Value.SerialNumber);
        Assert.Equal(2022, result.Value.ManufactureYear);
        Assert.Equal(originalThumb, result.Value.ThumbnailMediaId);
        Assert.Equal(0, _mediaClient.VerifyCalls);
        Assert.Equal(0, _catalogClient.BrandCalls);
        Assert.Equal(0, _catalogClient.SpecCalls);
    }

    [Fact]
    public async Task PatchAssetAsync_BrandUpdate_SucceedsWhenActive()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);
        var oldBrand = Guid.NewGuid();
        var newBrand = Guid.NewGuid();

        var asset = CreateAssetWithRate(ownerUserId: ownerId, brandId: oldBrand, model: "FX3");
        _repo.Add(asset);

        var patch = new PatchInventoryAssetRequest
        {
            IsBrandIdSpecified = true,
            BrandId = newBrand
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(newBrand, result.Value.BrandId);
        Assert.Equal(1, _catalogClient.BrandCalls);
        Assert.Equal(0, _catalogClient.SpecCalls);
    }

    [Fact]
    public async Task PatchAssetAsync_BrandInactive_ReturnsValidationError()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);
        var inactiveBrand = Guid.NewGuid();

        _catalogClient.BrandHandler = id => Task.FromResult(Result.Success(
            new CatalogBrandResponse(id, "BR-INACTIVE", "Inactive Brand", false)));

        var asset = CreateAssetWithRate(ownerUserId: ownerId, model: "FX3");
        _repo.Add(asset);

        var patch = new PatchInventoryAssetRequest
        {
            IsBrandIdSpecified = true,
            BrandId = inactiveBrand
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("Brand.Inactive", result.Error.Code);
    }

    [Fact]
    public async Task PatchAssetAsync_CannotRemoveBrand_ReturnsValidationError()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset = CreateAssetWithRate(ownerUserId: ownerId, model: "FX3");
        _repo.Add(asset);

        var patch = new PatchInventoryAssetRequest
        {
            IsBrandIdSpecified = true,
            BrandId = null
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("CANNOT_REMOVE_BRAND", result.Error.Code);
    }

    [Fact]
    public async Task PatchAssetAsync_EmptyGuidBrand_ReturnsValidationError()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset = CreateAssetWithRate(ownerUserId: ownerId, model: "FX3");
        _repo.Add(asset);

        var patch = new PatchInventoryAssetRequest
        {
            IsBrandIdSpecified = true,
            BrandId = Guid.Empty
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_BRAND_ID", result.Error.Code);
    }

    [Fact]
    public async Task PatchAssetAsync_ModelOnly_DoesNotCallCatalog()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset = CreateAssetWithRate(ownerUserId: ownerId, model: "FX3");
        _repo.Add(asset);

        var patch = new PatchInventoryAssetRequest
        {
            IsModelSpecified = true,
            Model = "FX3 Updated"
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, _catalogClient.LookupCalls);
        Assert.Equal(0, _catalogClient.BrandCalls);
        Assert.Equal(0, _catalogClient.SpecCalls);
    }

    [Fact]
    public async Task PatchAssetAsync_SpecOnly_ValidatesWithCurrentSchemaVersion()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset = CreateAssetWithRate(ownerUserId: ownerId, model: "FX3", schemaVersion: 2, specValues: "{\"color\":\"black\"}");
        _repo.Add(asset);

        int validatedVersion = 0;
        string? validatedSpecs = null;
        _catalogClient.SpecHandler = (typeId, ver, specs) =>
        {
            validatedVersion = ver;
            validatedSpecs = specs;
            return Task.FromResult(Result.Success());
        };

        using var newSpecDoc = JsonDocument.Parse("{\"color\":\"silver\"}");
        var patch = new PatchInventoryAssetRequest
        {
            IsSpecValuesSpecified = true,
            SpecValues = newSpecDoc.RootElement.Clone()
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, validatedVersion);
        Assert.Equal("{\"color\":\"silver\"}", validatedSpecs);
    }

    [Fact]
    public async Task PatchAssetAsync_SchemaOnly_ValidatesWithCurrentSpecValues()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset = CreateAssetWithRate(ownerUserId: ownerId, model: "FX3", schemaVersion: 1, specValues: "{\"resolution\":\"4K\"}");
        _repo.Add(asset);

        int validatedVersion = 0;
        string? validatedSpecs = null;
        _catalogClient.SpecHandler = (typeId, ver, specs) =>
        {
            validatedVersion = ver;
            validatedSpecs = specs;
            return Task.FromResult(Result.Success());
        };

        var patch = new PatchInventoryAssetRequest
        {
            IsSchemaVersionSpecified = true,
            SchemaVersion = 3
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, validatedVersion);
        Assert.Equal("{\"resolution\":\"4K\"}", validatedSpecs);
    }

    [Fact]
    public async Task PatchAssetAsync_OldAssetNullBrand_PreservesNullWhenOmitted()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var ctor = typeof(InventoryAsset).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .First(c => c.GetParameters().Length > 0);
        var oldAssetId = Guid.NewGuid();
        var oldRate = AssetRentalRate.Create(oldAssetId, 100_000m, null, null, "VND", FixedNow).Value;
        var oldAsset = (InventoryAsset)ctor.Invoke([
            oldAssetId, ownerId, Guid.NewGuid(), (Guid?)null, "Old Model", (string?)null, (int?)null, (Guid?)null, 1, "{}", true, (string?)null, oldRate, FixedNow
        ]);
        _repo.Add(oldAsset);

        var patch = new PatchInventoryAssetRequest
        {
            IsModelSpecified = true,
            Model = "Updated Old Model"
        };

        var result = await service.PatchAssetAsync(oldAsset.Id, patch, caller);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.BrandId);
        Assert.Equal("Updated Old Model", result.Value.Model);
    }

    [Fact]
    public async Task PatchAssetAsync_OldAssetNullBrand_CanBeBackfilledWithValidBrand()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);
        var brandId = Guid.NewGuid();

        var ctor = typeof(InventoryAsset).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .First(c => c.GetParameters().Length > 0);
        var oldAssetId = Guid.NewGuid();
        var oldRate = AssetRentalRate.Create(oldAssetId, 100_000m, null, null, "VND", FixedNow).Value;
        var oldAsset = (InventoryAsset)ctor.Invoke([
            oldAssetId, ownerId, Guid.NewGuid(), (Guid?)null, "Old Model", (string?)null, (int?)null, (Guid?)null, 1, "{}", true, (string?)null, oldRate, FixedNow
        ]);
        _repo.Add(oldAsset);
        var patch = new PatchInventoryAssetRequest
        {
            IsBrandIdSpecified = true,
            BrandId = brandId
        };

        var result = await service.PatchAssetAsync(oldAsset.Id, patch, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(brandId, result.Value.BrandId);
    }

    [Fact]
    public async Task PatchAssetAsync_EmptyPatch_Returns400()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid());
        var emptyPatch = new PatchInventoryAssetRequest();

        var result = await service.PatchAssetAsync(Guid.NewGuid(), emptyPatch, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("EMPTY_PATCH", result.Error.Code);
    }

    [Fact]
    public async Task PatchAssetAsync_ConcurrencyTokenConflict_Returns409()
    {
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset = CreateAssetWithRate(ownerUserId: ownerId, model: "M");
        asset.Version = 100;
        _repo.Add(asset);

        var writeTx = new FakeWriteTransaction(_repo);
        writeTx.BeforeExecute = () =>
        {
            asset.Version = 101;
        };

        var mediaOptions = Options.Create(new MediaServiceOptions { InternalBaseUrl = "http://m:80", PublicGatewayUrl = "http://m:50", ApiKey = "k", TimeoutSeconds = 10 });
        var mediaService = new InventoryMediaService(_mediaClient, mediaOptions);
        var queryValidator = new AssetSearchQueryValidator(_clock);
        var filterResolver = new AssetSearchFilterResolver(_catalogClient);
        var service = new InventoryAssetService(_repo, writeTx, _catalogClient, mediaService, queryValidator, filterResolver, _clock);

        var patch = new PatchInventoryAssetRequest { IsModelSpecified = true, Model = "New M" };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Equal("CONCURRENCY_CONFLICT", result.Error.Code);
        Assert.Equal("M", _repo.Store[asset.Id].Model);
    }

    [Fact]
    public async Task PatchAssetAsync_WithoutConcurrencyConflict_Succeeds()
    {
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset = CreateAssetWithRate(ownerUserId: ownerId, model: "M");
        asset.Version = 100;
        _repo.Add(asset);

        var writeTx = new FakeWriteTransaction(_repo);
        var mediaOptions = Options.Create(new MediaServiceOptions { InternalBaseUrl = "http://m:80", PublicGatewayUrl = "http://m:50", ApiKey = "k", TimeoutSeconds = 10 });
        var mediaService = new InventoryMediaService(_mediaClient, mediaOptions);
        var queryValidator = new AssetSearchQueryValidator(_clock);
        var filterResolver = new AssetSearchFilterResolver(_catalogClient);
        var service = new InventoryAssetService(_repo, writeTx, _catalogClient, mediaService, queryValidator, filterResolver, _clock);

        var patch = new PatchInventoryAssetRequest { IsModelSpecified = true, Model = "New M" };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal("New M", result.Value.Model);
        Assert.Equal("New M", _repo.Store[asset.Id].Model);
    }

    [Fact]
    public async Task PatchAssetAsync_SpecValidationInvalid_DoesNotCallMediaService()
    {
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);
        var newThumbId = Guid.NewGuid();

        var asset = CreateAssetWithRate(ownerUserId: ownerId, model: "M");
        _repo.Add(asset);

        _catalogClient.SpecHandler = (_, _, _) => Task.FromResult(Result.Failure(
            Error.Validation("SPEC_VALIDATION_FAILED", "Spec rejected by catalog",
                [new ValidationError("k", "REQUIRED", "Trường 'k' là bắt buộc.")])));

        var service = CreateService();

        using var specDoc = JsonDocument.Parse("{\"k\":\"new-v\"}");
        var patch = new PatchInventoryAssetRequest
        {
            IsSpecValuesSpecified = true,
            SpecValues = specDoc.RootElement.Clone(),
            IsThumbnailMediaIdSpecified = true,
            ThumbnailMediaId = newThumbId
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("SPEC_VALIDATION_FAILED", result.Error.Code);
        Assert.NotNull(result.Error.Errors);
        var patchErr = Assert.Single(result.Error.Errors);
        Assert.Equal("k", patchErr.Field);
        Assert.Equal("REQUIRED", patchErr.Code);
        Assert.Equal(0, _mediaClient.VerifyCalls);
    }

    [Fact]
    public async Task ToggleAcceptingBookings_DisablesAndEmitsAvailabilityEvent_WhenFlagChanged()
    {
        var repo = new FakeAssetRepository();
        var writeTx = new FakeWriteTransaction(repo);
        var mediaOptions = Options.Create(new MediaServiceOptions { InternalBaseUrl = "http://m:80", PublicGatewayUrl = "http://m:50", ApiKey = "k", TimeoutSeconds = 10 });
        var mediaService = new InventoryMediaService(_mediaClient, mediaOptions);
        var queryValidator = new AssetSearchQueryValidator(_clock);
        var filterResolver = new AssetSearchFilterResolver(_catalogClient);
        var service = new InventoryAssetService(repo, writeTx, _catalogClient, mediaService, queryValidator, filterResolver, _clock);

        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);
        var asset = CreateAssetWithRate(ownerUserId: ownerId, model: "M");
        repo.Add(asset);

        Assert.True(asset.AcceptingBookings);

        var toggleReq = new ToggleAcceptingBookingsRequest(AcceptingBookings: false, Reason: "Maintenance period");
        var result = await service.ToggleAcceptingBookingsAsync(asset.Id, toggleReq, caller);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.AcceptingBookings);
        Assert.Equal("Maintenance period", result.Value.SuspensionReason);

        Assert.Single(writeTx.StagedOutbox);
        var outbox = writeTx.StagedOutbox[0];
        Assert.Equal(KafkaTopics.InventoryAssetEventsV1, outbox.Topic);
        Assert.Equal(asset.Id.ToString("D"), outbox.KafkaKey);
        Assert.Contains(asset.Id.ToString("D"), outbox.Payload);

        var toggleAgain = await service.ToggleAcceptingBookingsAsync(asset.Id, toggleReq, caller);
        Assert.True(toggleAgain.IsSuccess);
        Assert.Single(writeTx.StagedOutbox);
    }
}
