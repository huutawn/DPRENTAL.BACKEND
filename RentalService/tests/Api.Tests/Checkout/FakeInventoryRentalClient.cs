using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Api.Tests.Checkout;

/// <summary>
/// Inventory giả cho test giữ lịch: ghi lại mọi lời gọi hold/release và replay theo ExternalReference.
/// </summary>
internal sealed class FakeInventoryRentalClient : IInventoryRentalClient
{
    private readonly Dictionary<string, CreateHoldResponse> _groupsByReference = [];
    private readonly Dictionary<Guid, string> _firstGroupByListing = [];

    public List<CreateHoldRequest> HoldRequests { get; } = [];

    public List<CancellationToken> HoldTokens { get; } = [];

    public List<ReleaseCall> Releases { get; } = [];

    public Dictionary<Guid, Error> HoldErrors { get; } = [];

    public Dictionary<Guid, int> AssignedCountOverrides { get; } = [];

    public bool FailRelease { get; set; }

    public int CreatedGroupCount => _groupsByReference.Count;

    public string GroupIdFor(Guid listingId) => _firstGroupByListing[listingId];

    public Task<Result<CreateHoldResponse>> CreateHoldAsync(CreateHoldRequest request, CancellationToken ct = default)
    {
        HoldRequests.Add(request);
        HoldTokens.Add(ct);

        if (HoldErrors.TryGetValue(request.ListingId, out Error? error))
        {
            return Task.FromResult(Result.Failure<CreateHoldResponse>(error));
        }

        if (_groupsByReference.TryGetValue(request.ExternalReference, out CreateHoldResponse? existing))
        {
            return Task.FromResult(Result.Success(existing with { IsReplay = true }));
        }

        int assignedCount = AssignedCountOverrides.GetValueOrDefault(request.ListingId, request.Quantity);
        CreateHoldResponse created = new(
            $"grp-{_groupsByReference.Count + 1}",
            Enumerable.Range(0, assignedCount)
                .Select(_ => new AssignedAssetDto(Guid.NewGuid(), Guid.NewGuid(), "Held"))
                .ToList(),
            request.RequestedHoldExpiresAt,
            request.ListingId,
            request.Quantity,
            request.From,
            request.To,
            false,
            "Held");

        _groupsByReference[request.ExternalReference] = created;
        _firstGroupByListing.TryAdd(request.ListingId, created.AllocationGroupId);
        return Task.FromResult(Result.Success(created));
    }

    public Task<Result<ReleaseAllocationResponse>> ReleaseAllocationAsync(
        string allocationGroupId,
        ReleaseAllocationRequest request,
        CancellationToken ct = default)
    {
        Releases.Add(new(allocationGroupId, request, ct));

        return Task.FromResult(FailRelease
            ? Result.Failure<ReleaseAllocationResponse>(Error.ServiceUnavailable("Inventory.Unavailable", "Inventory không phản hồi."))
            : Result.Success(new ReleaseAllocationResponse(
                $"receipt-{Releases.Count}", request.OperationKey, allocationGroupId, DateTimeOffset.UnixEpoch, false)));
    }

    public Task<Result<ListingRentalSnapshotDto>> GetListingRentalSnapshotAsync(Guid listingId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(ListingAvailabilityCheckRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<Result<ExtendHoldResponse>> ExtendHoldAsync(string allocationGroupId, ExtendHoldRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<Result<ConfirmAllocationResponse>> ConfirmAllocationAsync(string allocationGroupId, ConfirmAllocationRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<Result<CancelAllocationResponse>> CancelAllocationAsync(string allocationGroupId, CancelAllocationRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<Result<MarkAssetsOverdueResponse>> MarkAssetsOverdueAsync(string allocationGroupId, MarkAssetsOverdueRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<Result<MarkAssetsReturnedResponse>> MarkAssetsReturnedAsync(string allocationGroupId, MarkAssetsReturnedRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<Result<BlockDamagedAssetsResponse>> BlockDamagedAssetsAsync(string allocationGroupId, BlockDamagedAssetsRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    internal sealed record ReleaseCall(string AllocationGroupId, ReleaseAllocationRequest Request, CancellationToken Token);
}
