using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Policies;

/// <summary>
/// Một khoản hoàn cho người thuê của một dòng thiết bị, kèm xác nhận chuyển khoản của Admin nếu đã có.
/// </summary>
public sealed record RentalRefundLine(
    RentalItem Item,
    RentalOperationReceipt Request,
    RentalRefundKind Kind,
    decimal DeductionAmount,
    decimal RefundAmount,
    RentalRefundConfirmation? Confirmation);

/// <summary>
/// Khoản nào phải hoàn cho người thuê và hoàn bao nhiêu. Rental tạo yêu cầu <c>RequestRefund</c> khi dòng bị hủy,
/// bị từ chối hoặc quá hạn sau khi đã thanh toán: hoàn toàn bộ tiền thuê và tiền cọc. Rental tạo <c>SettleRental</c> khi dòng
/// đã trả máy và chốt khấu trừ: hoàn phần cọc còn lại; phần đó bằng 0 thì không có khoản hoàn.
/// Khoản đã hoàn khi Admin xác nhận chuyển khoản ở Rental. Truy vấn danh sách trong repository lọc theo đúng các luật này.
/// </summary>
public static class RentalRefundPolicy
{
    public const string RequestRefundAction = "RequestRefund";

    public static RentalRefundKind? KindOf(string requestAction) => requestAction switch
    {
        RequestRefundAction => RentalRefundKind.FullRefund,
        RentalItem.SettleRentalAction => RentalRefundKind.DepositReturn,
        _ => null
    };

    public static decimal DeductionOf(RentalRefundKind kind, decimal? finalDeductionAmount) =>
        kind == RentalRefundKind.DepositReturn ? finalDeductionAmount ?? 0m : 0m;

    public static decimal AmountOf(RentalRefundKind kind, decimal rentalAmount, decimal depositAmount, decimal deductionAmount) =>
        kind == RentalRefundKind.FullRefund ? rentalAmount + depositAmount : depositAmount - deductionAmount;

    /// <summary>
    /// Các khoản hoàn của đơn theo thứ tự yêu cầu, mỗi dòng thiết bị tối đa một khoản. Cần đơn đã nạp các receipt yêu cầu
    /// hoàn và các xác nhận chuyển khoản; <paramref name="items"/> là các dòng đã nạp của đơn.
    /// </summary>
    public static IReadOnlyList<RentalRefundLine> LinesOf(RentalOrder order, IEnumerable<RentalItem> items)
    {
        ArgumentNullException.ThrowIfNull(order);
        Dictionary<Guid, RentalItem> itemsById = items.DistinctBy(i => i.Id).ToDictionary(i => i.Id);

        List<RentalRefundLine> lines = [];
        foreach (RentalOperationReceipt request in order.OperationReceipts.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id))
        {
            if (request.TargetItemId is not { } itemId ||
                KindOf(request.Action) is not { } kind ||
                !itemsById.TryGetValue(itemId, out RentalItem? item) ||
                lines.Any(l => l.Item.Id == itemId))
            {
                continue;
            }

            decimal deduction = DeductionOf(kind, item.FinalDeductionAmount);
            decimal amount = AmountOf(kind, item.RentalAmount, item.DepositAmount, deduction);
            if (amount <= 0m)
            {
                continue;
            }

            lines.Add(new RentalRefundLine(
                item,
                request,
                kind,
                deduction,
                amount,
                order.RefundConfirmations.FirstOrDefault(c => c.RentalItemId == itemId)));
        }

        return lines;
    }
}
