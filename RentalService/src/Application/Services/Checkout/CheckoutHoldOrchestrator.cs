#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Checkout;

public sealed class CheckoutHoldOrchestrator : ICheckoutHoldOrchestrator
{
    private const string ReleaseReason = "Checkout aborted: not all lines could be held";

    private readonly IInventoryRentalClient _inventory;
    private readonly ILogger<CheckoutHoldOrchestrator> _logger;

    public CheckoutHoldOrchestrator(IInventoryRentalClient inventory, ILogger<CheckoutHoldOrchestrator> logger)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CheckoutHoldResult> HoldAllLinesAsync(
        CheckoutPlan plan,
        string checkoutOperationId,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutOperationId);

        DateTimeOffset holdExpiresAt = utcNow.Add(CheckoutRules.PaymentWindow);

        // Inventory yêu cầu hold hết hạn trước 00:00 ngày giao; kiểm tra trước để không phải giữ rồi nhả.
        List<CheckoutHoldConflict> tooSoon = plan.Lines
            .Where(l => RentalInventoryDateAdapter.ValidateHoldDeadline(utcNow, holdExpiresAt, l.ExpectedStartDate).IsFailure)
            .Select(l => Conflict(l, CheckoutHoldConflictCode.StartDateTooSoon,
                "Ngày giao quá gần, không còn đủ thời gian chờ thanh toán."))
            .ToList();
        if (tooSoon.Count > 0)
        {
            return CheckoutHoldResult.Rejected(tooSoon);
        }

        List<CheckoutHeldLine> held = [];
        foreach (CheckoutPlanLine line in plan.Lines)
        {
            Guid rentalItemId = CheckoutRules.DeriveRentalItemId(plan.RenterId, checkoutOperationId.Trim(), line.CartItemId);
            CreateHoldRequest request = new(
                line.EquipmentListingId,
                line.Quantity,
                line.ExpectedStartDate,
                line.ExpectedEndDate,
                $"rental-item:{rentalItemId}",
                holdExpiresAt);

            Result<CreateHoldResponse> result = await _inventory.CreateHoldAsync(request, cancellationToken);

            if (result.IsSuccess)
            {
                held.Add(new(line, rentalItemId, result.Value.AllocationGroupId, result.Value.AssignedAssets));
                if (result.Value.AssignedAssets.Count == line.Quantity)
                {
                    continue;
                }

                // Inventory trả sai số lượng máy: lỗi phía phụ thuộc, nhả luôn cả nhóm vừa giữ.
                await ReleaseAllLinesAsync(held);
                return CheckoutHoldResult.DependencyFailed();
            }

            await ReleaseAllLinesAsync(held);
            return result.Error.Type is ErrorType.ServiceUnavailable or ErrorType.Failure
                ? CheckoutHoldResult.DependencyFailed()
                : CheckoutHoldResult.Rejected([MapConflict(line, result.Error)]);
        }

        return CheckoutHoldResult.Success(held, holdExpiresAt);
    }

    /// <summary>
    /// Nhả theo thứ tự ngược với lúc giữ. Không ném lỗi: nhóm nhả hỏng sẽ tự hết hạn theo HoldExpiresAt.
    /// </summary>
    public async Task ReleaseAllLinesAsync(IReadOnlyList<CheckoutHeldLine> heldLines)
    {
        ArgumentNullException.ThrowIfNull(heldLines);

        for (int i = heldLines.Count - 1; i >= 0; i--)
        {
            string allocationGroupId = heldLines[i].AllocationGroupId;
            string operationKey = RentalOperationExecutor.ComputeOperationKey("ReleaseCheckoutHold", allocationGroupId);

            // Không dùng token của request: renter ngắt kết nối giữa chừng vẫn phải nhả cho xong.
            Result<ReleaseAllocationResponse> release = await _inventory.ReleaseAllocationAsync(
                allocationGroupId,
                new ReleaseAllocationRequest(operationKey, ReleaseReason),
                CancellationToken.None);

            if (release.IsFailure)
            {
                _logger.LogWarning(
                    "Không nhả được hold {AllocationGroupId} khi hủy checkout: {ErrorCode}. Hold sẽ tự hết hạn.",
                    allocationGroupId,
                    release.Error.Code);
            }
        }
    }

    private static CheckoutHoldConflict MapConflict(CheckoutPlanLine line, Error error) => error.Code switch
    {
        InventoryContractSpec.ErrorCodes.InsufficientAvailability =>
            Conflict(line, CheckoutHoldConflictCode.InsufficientAvailability, "Thiết bị không còn đủ số lượng trống."),
        InventoryContractSpec.ErrorCodes.SnapshotVersionMismatch =>
            Conflict(line, CheckoutHoldConflictCode.ListingChanged, "Thông tin thiết bị vừa thay đổi. Vui lòng xem lại."),
        InventoryContractSpec.ErrorCodes.ListingNotFound =>
            Conflict(line, CheckoutHoldConflictCode.ListingUnavailable, "Thiết bị không còn cho thuê."),
        InventoryContractSpec.ErrorCodes.InvalidHoldExpiry =>
            Conflict(line, CheckoutHoldConflictCode.StartDateTooSoon, "Ngày giao quá gần, không còn đủ thời gian chờ thanh toán."),
        InventoryContractSpec.ErrorCodes.HoldExpired or InventoryContractSpec.ErrorCodes.ExternalReferenceReused =>
            Conflict(line, CheckoutHoldConflictCode.OperationNotReusable, "Lần gửi này đã hết hiệu lực. Vui lòng gửi lại."),
        _ => Conflict(line, CheckoutHoldConflictCode.HoldRejected, "Không giữ được lịch cho thiết bị.")
    };

    private static CheckoutHoldConflict Conflict(CheckoutPlanLine line, CheckoutHoldConflictCode code, string message) =>
        new(line.CartItemId, line.EquipmentListingId, line.Quantity, code, message);
}
