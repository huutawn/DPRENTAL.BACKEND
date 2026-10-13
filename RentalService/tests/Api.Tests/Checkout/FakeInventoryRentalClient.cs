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
    private readonly HashSet<string> _releasedGroups = [];

    public List<CreateHoldRequest> HoldRequests { get; } = [];

    public List<CancellationToken> HoldTokens { get; } = [];

    public List<ReleaseCall> Releases { get; } = [];

    public Dictionary<Guid, Error> HoldErrors { get; } = [];

    public Dictionary<Guid, int> AssignedCountOverrides { get; } = [];

    public bool FailRelease { get; set; }

    /// <summary>
    /// Số máy của từng Listing. Listing có khai báo ở đây thì hold vượt số máy còn trống bị từ chối
    /// và release trả máy lại; Listing không khai báo thì giữ được không giới hạn.
    /// </summary>
    public Dictionary<Guid, int> Capacities { get; } = [];

    public int CreatedGroupCount => _groupsByReference.Count;

    /// <summary>Số máy của Listing đang bị giữ bởi các nhóm chưa được nhả.</summary>
    public int HeldUnits(Guid listingId) => _groupsByReference.Values
        .Where(g => g.ListingId == listingId && !_releasedGroups.Contains(g.AllocationGroupId))
        .Sum(g => g.AssignedAssets.Count);

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

        if (Capacities.TryGetValue(request.ListingId, out int capacity) &&
            HeldUnits(request.ListingId) + request.Quantity > capacity)
        {
            return Task.FromResult(Result.Failure<CreateHoldResponse>(Error.Conflict(
                InventoryContractSpec.ErrorCodes.InsufficientAvailability, "Không đủ máy trống.")));
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
        if (!FailRelease)
        {
            _releasedGroups.Add(allocationGroupId);
        }

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
