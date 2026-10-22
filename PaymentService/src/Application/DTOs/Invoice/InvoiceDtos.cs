using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Utils;

namespace Dprental.PaymentService.Application.DTOs.Invoice;

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
    public decimal TotalAmount => Items?.Sum(i => i.TotalAmount) ?? 0m;

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
    [Required] string Status,
    string? QrUrl,
    string? Bank,
    string? Account,
    string? TransferContent,
    bool IsReplay);

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

        if (!string.IsNullOrWhiteSpace(CancellationOrigin) &&
            !Domain.Entities.InvoiceCancellationOrigins.IsValid(CancellationOrigin))
        {
            return Error.Validation("Payment.InvalidCancellationOrigin",
                $"CancellationOrigin '{CancellationOrigin}' không hợp lệ. Giá trị hợp lệ: TIMEOUT, CUSTOMER_REQUESTED.");
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
    DateTimeOffset AcceptedAt,
    Guid RentalOrderId = default,
    string? PaymentRef = null,
    string? RecoveryId = null);

public sealed record GetInvoiceResponse(
    string InvoiceRef,
    decimal TotalAmount,
    string Currency,
    string? QrUrl,
    string Bank,
    string Account,
    string TransferContent,
    DateTimeOffset PaymentDeadline,
    string Status);
