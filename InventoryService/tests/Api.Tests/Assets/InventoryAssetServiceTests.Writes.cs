using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed partial class InventoryAssetServiceTests
{
    [Fact]
    public async Task PatchAssetAsync_ValidPhysicalDetails_UpdatesAsset()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset = CreateAssetWithRate(
            ownerUserId: ownerId,
            serialNumber: "SN-ORIG",
            manufactureYear: 2022);
        _repo.Add(asset);

        var patch = new PatchInventoryAssetRequest
        {
            IsSerialNumberSpecified = true,
            SerialNumber = "SN-NEW",
            IsManufactureYearSpecified = true,
            ManufactureYear = 2024
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal("SN-NEW", result.Value.SerialNumber);
        Assert.Equal(2024, result.Value.ManufactureYear);
        Assert.Equal("SN-NEW", _repo.Store[asset.Id].SerialNumber);
        Assert.Equal(2024, _repo.Store[asset.Id].ManufactureYear);
    }

    [Fact]
    public async Task PatchAssetAsync_EmptyPatch_ReturnsValidationError()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset = CreateAssetWithRate(ownerUserId: ownerId);
        _repo.Add(asset);

        var patch = new PatchInventoryAssetRequest();
        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("EMPTY_PATCH_REQUEST", result.Error.Code);
    }

    [Fact]
    public async Task PatchAssetAsync_Unauthenticated_Returns401()
    {
        var service = CreateService();
        var caller = new TestCaller(Guid.NewGuid(), isAuthenticated: false);

        var patch = new PatchInventoryAssetRequest
        {
            IsSerialNumberSpecified = true,
            SerialNumber = "SN-NEW"
        };

        var result = await service.PatchAssetAsync(Guid.NewGuid(), patch, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
    }

    [Fact]
    public async Task PatchAssetAsync_DifferentOwner_Returns403()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(Guid.NewGuid());

        var asset = CreateAssetWithRate(ownerUserId: ownerId);
        _repo.Add(asset);

        var patch = new PatchInventoryAssetRequest
        {
            IsSerialNumberSpecified = true,
            SerialNumber = "SN-NEW"
        };

        var result = await service.PatchAssetAsync(asset.Id, patch, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
    }

    [Fact]
    public async Task ToggleAcceptingBookingsAsync_EnableAndDisable_UpdatesFlagAndOutbox()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller(ownerId);

        var asset = CreateAssetWithRate(ownerUserId: ownerId);
        _repo.Add(asset);

        var disableReq = new ToggleAcceptingBookingsRequest(false, "Maintenance");
        var disableResult = await service.ToggleAcceptingBookingsAsync(asset.Id, disableReq, caller);

        Assert.True(disableResult.IsSuccess);
        Assert.False(disableResult.Value.AcceptingBookings);
        Assert.Equal("Maintenance", disableResult.Value.SuspensionReason);

        var enableReq = new ToggleAcceptingBookingsRequest(true);
        var enableResult = await service.ToggleAcceptingBookingsAsync(asset.Id, enableReq, caller);

        Assert.True(enableResult.IsSuccess);
        Assert.True(enableResult.Value.AcceptingBookings);
        Assert.Null(enableResult.Value.SuspensionReason);
    }
}
