using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Checkout;

/// <summary>Một dòng đã giữ lịch thành công, đủ dữ liệu để tạo RentalItem và RentalUnitAssignment.</summary>
public sealed record CheckoutHeldLine(
	CheckoutPlanLine Line,
	Guid RentalItemId,
	string AllocationGroupId,
	IReadOnlyList<AssignedAssetDto> AssignedAssets);

public enum CheckoutHoldConflictCode
{
	InsufficientAvailability = 1,
	ListingChanged = 2,
	ListingUnavailable = 3,
	StartDateTooSoon = 4,
	OperationNotReusable = 5,
	HoldRejected = 6
}

public sealed record CheckoutHoldConflict(
	Guid CartItemId,
	Guid EquipmentListingId,
	int RequestedQuantity,
	CheckoutHoldConflictCode Code,
	string Message);

public sealed class CheckoutHoldResult
{
	private CheckoutHoldResult(Error error, IReadOnlyList<CheckoutHeldLine> heldLines,
		DateTimeOffset? holdExpiresAt, IReadOnlyList<CheckoutHoldConflict> conflicts)
	{
		Error = error;
		HeldLines = heldLines;
		HoldExpiresAt = holdExpiresAt;
		Conflicts = conflicts;
	}

	public bool IsSuccess => Error == Error.None;
	public Error Error { get; }
	public IReadOnlyList<CheckoutHeldLine> HeldLines { get; }
	public DateTimeOffset? HoldExpiresAt { get; }
	public IReadOnlyList<CheckoutHoldConflict> Conflicts { get; }

	public static CheckoutHoldResult Success(IReadOnlyList<CheckoutHeldLine> heldLines, DateTimeOffset holdExpiresAt) =>
		new(Error.None, heldLines, holdExpiresAt, []);

	public static CheckoutHoldResult Rejected(IReadOnlyList<CheckoutHoldConflict> conflicts) =>
		new(CheckoutErrors.HoldFailed, [], null, conflicts);

	public static CheckoutHoldResult DependencyFailed() =>
		new(CheckoutErrors.DependencyUnavailable, [], null, []);
}