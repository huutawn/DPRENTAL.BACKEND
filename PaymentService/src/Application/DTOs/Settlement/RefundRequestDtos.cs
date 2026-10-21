using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Utils;
namespace Dprental.PaymentService.Application.DTOs.Settlement;

public sealed record RefundItemRequest(
    [Required] Guid RentalItemId,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal RefundRentalAmount,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal RefundDepositAmount)
{
    [JsonIgnore]
    public decimal TotalRefundAmount => RefundRentalAmount + RefundDepositAmount;

    public Result Validate()
    {
        if (RentalItemId == Guid.Empty)
        {
            return Error.Validation("Payment.InvalidId", "RentalItemId phải là GUID hợp lệ.");
        }

        var rentalCheck = VndAmountValidator.ValidateVndAmount(RefundRentalAmount, "Tiền thuê hoàn lại");
        if (rentalCheck.IsFailure) return rentalCheck.Error;

        var depositCheck = VndAmountValidator.ValidateVndAmount(RefundDepositAmount, "Tiền cọc hoàn lại");
        if (depositCheck.IsFailure) return depositCheck.Error;

        return Result.Success();
    }
}

public sealed record RequestRefundRequest(
    [Required] Guid RentalOrderId,
    [Required][MaxLength(256)] string InvoiceRef,
    [Required][MaxLength(256)] string PaymentRef,
    [Required] IReadOnlyList<RefundItemRequest> Items,
    [Required][MaxLength(500)] string Reason,
    [Required][MaxLength(64)] string OperationKey)
{
    [JsonIgnore]
    public decimal TotalRefundAmount => Items.Sum(i => i.TotalRefundAmount);

    public Result Validate()
    {
        if (RentalOrderId == Guid.Empty)
        {
            return Error.Validation("Payment.InvalidId", "RentalOrderId phải là GUID hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(InvoiceRef) || InvoiceRef.Trim().Length > 256 ||
            string.IsNullOrWhiteSpace(PaymentRef) || PaymentRef.Trim().Length > 256)
        {
            return Error.Validation("Payment.InvalidReference", "InvoiceRef và PaymentRef không được rỗng và tối đa 256 ký tự.");
        }

        if (string.IsNullOrWhiteSpace(OperationKey) || OperationKey.Trim().Length > 64)
        {
            return Error.Validation("Payment.InvalidOperationKey", "OperationKey không được rỗng và tối đa 64 ký tự.");
        }

        if (string.IsNullOrWhiteSpace(Reason) || Reason.Trim().Length > 500)
        {
            return Error.Validation("Payment.InvalidReason", "Reason không được rỗng và tối đa 500 ký tự.");
        }

        if (Items is null || Items.Count == 0)
        {
            return Error.Validation("Payment.EmptyItems", "Danh sách items hoàn tiền không được rỗng.");
        }

        if (Items.Any(i => i is null))
        {
            return Error.Validation("Payment.NullItem", "Danh sách items hoàn tiền không được chứa phần tử null.");
        }

        if (TotalRefundAmount <= 0)
        {
            return Error.Validation("Payment.InvalidRefundAmount", "Tổng tiền hoàn lại phải lớn hơn 0.");
        }

        if (Items.Select(i => i.RentalItemId).Distinct().Count() != Items.Count)
        {
            return Error.Conflict("Payment.DuplicateItemId", "Danh sách items hoàn tiền chứa RentalItemId bị trùng lặp.");
        }

        foreach (var item in Items)
        {
            var itemVal = item.Validate();
            if (itemVal.IsFailure) return itemVal.Error;
        }

        return Result.Success();
    }
}

public sealed record RequestRefundResponse(
    [Required] string RefundRef,
    [Required] string OperationKey,
    [Required] string Status,
    decimal TotalRefundAmount,
    [Required] string Currency,
    bool IsReplay);
