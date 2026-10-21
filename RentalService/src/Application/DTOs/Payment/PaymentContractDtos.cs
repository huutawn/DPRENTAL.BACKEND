using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Utils;
using Dprental.RentalService.Application.Clients;

namespace Dprental.RentalService.Application.DTOs.Payment;

// 1. Create Invoice
public sealed record CreateInvoiceItemRequest(
    [Required] Guid RentalItemId,
    [Required] Guid SupplierId,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal RentalAmount,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal DepositAmount)
{
    [JsonIgnore]
    public decimal TotalAmount => RentalAmount + DepositAmount;

    public Result Validate()
    {
        if (RentalItemId == Guid.Empty || SupplierId == Guid.Empty)
        {
            return Error.Validation("Payment.InvalidId", "RentalItemId và SupplierId phải là GUID hợp lệ.");
        }

        var rentalCheck = VndAmountValidator.ValidateVndAmount(RentalAmount, "Tiền thuê");
        if (rentalCheck.IsFailure) return rentalCheck.Error;

        var depositCheck = VndAmountValidator.ValidateVndAmount(DepositAmount, "Tiền cọc");
        if (depositCheck.IsFailure) return depositCheck.Error;

        return Result.Success();
    }
}

public sealed record CreateInvoiceRequest(
    [Required] Guid RentalOrderId,
    [Required][MaxLength(64)] string OrderCode,
    [Required] Guid RenterId,
    [Required][StringLength(3, MinimumLength = 3)] string Currency,
    [Required] DateTimeOffset PaymentDeadline,
    [Required] IReadOnlyList<CreateInvoiceItemRequest> Items,
    [Required][MaxLength(64)] string OperationKey)
{
    [JsonIgnore]
    public decimal TotalAmount => Items.Sum(i => i.TotalAmount);

    public Result Validate()
    {
        if (RentalOrderId == Guid.Empty || RenterId == Guid.Empty)
        {
            return Error.Validation("Payment.InvalidId", "RentalOrderId và RenterId phải là GUID hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(OrderCode) || OrderCode.Trim().Length > 64)
        {
            return Error.Validation("Payment.InvalidOrderCode", "OrderCode không được rỗng và tối đa 64 ký tự.");
        }

        if (string.IsNullOrWhiteSpace(Currency) || !string.Equals(Currency.Trim(), "VND", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("Payment.InvalidCurrency", "Currency bắt buộc phải là 'VND'.");
        }

        if (string.IsNullOrWhiteSpace(OperationKey) || OperationKey.Trim().Length > 64)
        {
            return Error.Validation("Payment.InvalidOperationKey", "OperationKey không được rỗng và tối đa 64 ký tự.");
        }

        if (Items is null || Items.Count == 0)
        {
            return Error.Validation("Payment.EmptyItems", "Danh sách items trong hóa đơn không được rỗng.");
        }
        if (Items.Any(i => i is null))
        {
            return Error.Validation("Payment.NullItem", "Danh sách items trong hóa đơn không được chứa phần tử null.");
        }


        if (Items.Select(i => i.RentalItemId).Distinct().Count() != Items.Count)
        {
            return Error.Conflict("Payment.DuplicateItemId", "Danh sách items chứa RentalItemId bị trùng lặp.");
        }

        foreach (var item in Items)
        {
            var itemValidation = item.Validate();
            if (itemValidation.IsFailure) return itemValidation.Error;
        }

        return Result.Success();
    }
}

public sealed record CreateInvoiceResponse(
    [Required] string InvoiceRef,
    decimal TotalAmount,
    [Required] string Currency,
    DateTimeOffset PaymentDeadline,
    bool IsReplay);

// 2. Cancel Invoice
public sealed record CancelInvoiceRequest(
    [Required] Guid RentalOrderId,
    [Required][MaxLength(256)] string InvoiceRef,
    [Required][MaxLength(500)] string Reason,
    [Required][MaxLength(64)] string OperationKey,
    string? CancellationOrigin = null)
{
    public Result Validate()
    {
        if (RentalOrderId == Guid.Empty)
        {
            return Error.Validation("Payment.InvalidId", "RentalOrderId phải là GUID hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(InvoiceRef) || InvoiceRef.Trim().Length > 256)
        {
            return Error.Validation("Payment.InvalidInvoiceRef", "InvoiceRef không được rỗng và tối đa 256 ký tự.");
        }

        if (string.IsNullOrWhiteSpace(Reason) || Reason.Trim().Length > 500)
        {
            return Error.Validation("Payment.InvalidReason", "Reason không được rỗng và tối đa 500 ký tự.");
        }

        if (string.IsNullOrWhiteSpace(OperationKey) || OperationKey.Trim().Length > 64)
        {
            return Error.Validation("Payment.InvalidOperationKey", "OperationKey không được rỗng và tối đa 64 ký tự.");
        }

        return Result.Success();
    }
}

public sealed record CancelInvoiceResponse(
    [Required] string InvoiceRef,
    bool Cancelled,
    DateTimeOffset CancelledAt,
    bool IsReplay);

public sealed record AcceptLatePaymentRequest(
    [Required][MaxLength(64)] string OperationKey,
    [Required] Guid RentalOrderId,
    [Required][MaxLength(64)] string PaymentRef,
    [Required][MaxLength(128)] string RecoveryId)
{
    public Result Validate()
    {
        if (string.IsNullOrWhiteSpace(OperationKey) || OperationKey.Trim().Length > 64)
        {
            return Error.Validation("Payment.InvalidOperationKey", "OperationKey không được rỗng và tối đa 64 ký tự.");
        }

        if (RentalOrderId == Guid.Empty)
        {
            return Error.Validation("Payment.InvalidId", "RentalOrderId phải là GUID hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(PaymentRef) || PaymentRef.Trim().Length > 64)
        {
            return Error.Validation("Payment.InvalidPaymentRef", "PaymentRef không được rỗng và tối đa 64 ký tự.");
        }

        if (string.IsNullOrWhiteSpace(RecoveryId) || RecoveryId.Trim().Length > 128)
        {
            return Error.Validation("Payment.InvalidRecoveryId", "RecoveryId không được rỗng và tối đa 128 ký tự.");
        }

        return Result.Success();
    }
}

public sealed record AcceptLatePaymentResponse(
    [Required] string InvoiceRef,
    [Required] string Status,
    bool IsReplay,
    DateTimeOffset AcceptedAt);

// 3. Request Refund
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
    public decimal TotalRefundAmount => Items?.Sum(i => i.TotalRefundAmount) ?? 0m;

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

// 4. Settle Rental
public sealed record SettleRentalRequest(
    [Required] Guid RentalOrderId,
    [Required] Guid RentalItemId,
    [Required][MaxLength(256)] string InvoiceRef,
    [Required][MaxLength(256)] string PaymentRef,
    [Required] Guid SupplierId,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal DepositAmount,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal FinalDeductionAmount,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal RefundedDepositAmount,
    [Required][StringLength(3, MinimumLength = 3)] string Currency,
    [Required][MaxLength(256)] string DecisionRef,
    [Required][MaxLength(64)] string OperationKey)
{
    public Result Validate()
    {
        if (RentalOrderId == Guid.Empty || RentalItemId == Guid.Empty || SupplierId == Guid.Empty)
        {
            return Error.Validation("Payment.InvalidId", "RentalOrderId, RentalItemId và SupplierId phải là GUID hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(InvoiceRef) || InvoiceRef.Trim().Length > 256 ||
            string.IsNullOrWhiteSpace(PaymentRef) || PaymentRef.Trim().Length > 256 ||
            string.IsNullOrWhiteSpace(DecisionRef) || DecisionRef.Trim().Length > 256)
        {
            return Error.Validation("Payment.InvalidReference", "Các mã đối chiếu không được rỗng và tối đa 256 ký tự.");
        }

        if (string.IsNullOrWhiteSpace(OperationKey) || OperationKey.Trim().Length > 64)
        {
            return Error.Validation("Payment.InvalidOperationKey", "OperationKey không được rỗng và tối đa 64 ký tự.");
        }

        if (string.IsNullOrWhiteSpace(Currency) || !string.Equals(Currency.Trim(), "VND", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("Payment.InvalidCurrency", "Currency bắt buộc phải là 'VND'.");
        }

        var depCheck = VndAmountValidator.ValidateVndAmount(DepositAmount, "Tiền cọc");
        if (depCheck.IsFailure) return depCheck.Error;

        var dedCheck = VndAmountValidator.ValidateVndAmount(FinalDeductionAmount, "Tiền khấu trừ cuối cùng");
        if (dedCheck.IsFailure) return dedCheck.Error;

        var refCheck = VndAmountValidator.ValidateVndAmount(RefundedDepositAmount, "Tiền hoàn cọc");
        if (refCheck.IsFailure) return refCheck.Error;

        if (FinalDeductionAmount > DepositAmount)
        {
            return Error.Validation(PaymentContractSpec.ErrorCodes.SettlementExceedsDeposit, "Tiền khấu trừ không thể vượt quá tiền cọc.");
        }

        if (RefundedDepositAmount != DepositAmount - FinalDeductionAmount)
        {
            return Error.Validation("Payment.SettlementBreakdownMismatch", "Tiền hoàn cọc bắt buộc bằng Tiền cọc trừ Tiền khấu trừ.");
        }

        return Result.Success();
    }
}

public sealed record SettleRentalResponse(
    [Required] string SettlementRef,
    [Required] string OperationKey,
    [Required] string Status,
    decimal FinalDeductionAmount,
    decimal RefundedDepositAmount,
    [Required] string Currency,
    bool IsReplay);
