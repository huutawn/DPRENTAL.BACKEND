using System.Text.Json;
#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Checkout;

public sealed class CheckoutInvoiceIssuer : ICheckoutInvoiceIssuer
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);

    private readonly IPaymentRentalClient _payment;
    private readonly IRentalOrderRepository _orders;
    private readonly IRentalCheckoutOperationRepository _checkoutOperations;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CheckoutInvoiceIssuer> _logger;

    public CheckoutInvoiceIssuer(
        IPaymentRentalClient payment,
        IRentalOrderRepository orders,
        IRentalCheckoutOperationRepository checkoutOperations,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<CheckoutInvoiceIssuer> logger)
    {
        _payment = payment ?? throw new ArgumentNullException(nameof(payment));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _checkoutOperations = checkoutOperations ?? throw new ArgumentNullException(nameof(checkoutOperations));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Result<CreateInvoiceResponse>> EnsureInvoiceAsync(RentalOrder order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (order.Status != RentalOrderStatus.PendingPayment || _timeProvider.GetUtcNow() >= order.PaymentDeadline)
        {
            return CheckoutErrors.OrderExpired;
        }

        if (!string.IsNullOrWhiteSpace(order.InvoiceRef))
        {
            string opKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", order.Id.ToString());
            var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == opKey);
            string? cachedJson = receipt?.ApiResponseJson ?? receipt?.ResponseJson;
            if (!string.IsNullOrWhiteSpace(cachedJson))
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<CreateInvoiceResponse>(cachedJson);
                    if (cached != null && MatchesOrder(cached, order) && IsReady(cached, order))
                    {
                        return cached;
                    }
                }
                catch (JsonException)
                {
                    _logger.LogWarning("Receipt hóa đơn của đơn {OrderId} không đọc được; gọi lại Payment cùng operation key.", order.Id);
                }
            }
        }
        // OperationKey tính từ mã đơn nên gọi lại bao nhiêu lần Payment cũng chỉ tạo một hóa đơn.
        CreateInvoiceRequest request = new(
            order.Id,
            order.OrderCode,
            order.RenterId,
            order.Currency,
            order.PaymentDeadline,
            order.Items
                .Select(i => new CreateInvoiceItemRequest(i.Id, i.SupplierId, i.RentalAmount, i.DepositAmount))
                .ToList(),
            RentalOperationExecutor.ComputeOperationKey("CreateInvoice", order.Id.ToString()));

        Result validation = request.Validate();
        if (validation.IsFailure)
        {
            _logger.LogError(
                "Đơn {OrderCode} không dựng được yêu cầu tạo hóa đơn hợp lệ: {ErrorCode}.", order.OrderCode, validation.Error.Code);
            return CheckoutErrors.InvoiceRejected;
        }

        Result<CreateInvoiceResponse> response = await _payment.CreateInvoiceAsync(request, cancellationToken);
        if (response.IsFailure && IsTemporary(response.Error))
        {
            await Task.Delay(RetryDelay, cancellationToken);
            if (_timeProvider.GetUtcNow() >= order.PaymentDeadline)
            {
                return CheckoutErrors.OrderExpired;
            }
            response = await _payment.CreateInvoiceAsync(request, cancellationToken);
        }

        if (response.IsFailure)
        {
            if (IsTemporary(response.Error))
            {
                _logger.LogWarning(
                    "Chưa tạo được hóa đơn cho đơn {OrderCode}: {ErrorCode}. Đơn được giữ để tạo lại.", order.OrderCode, response.Error.Code);
                return CheckoutErrors.InvoiceTemporarilyUnavailable;
            }

            _logger.LogError("Payment từ chối tạo hóa đơn cho đơn {OrderCode}: {ErrorCode}.", order.OrderCode, response.Error.Code);
            return CheckoutErrors.InvoiceRejected;
        }

        CreateInvoiceResponse invoice = response.Value;
        if (!MatchesOrder(invoice, order))
        {
            _logger.LogError(
                "Hóa đơn Payment trả về cho đơn {OrderId} ({OrderCode}) không hợp lệ hoặc lệch số tiền/loại tiền/deadline. OperationKey: {OperationKey}, Error: {ErrorCode}.",
                order.Id, order.OrderCode, request.OperationKey, CheckoutErrors.InvoiceRejected.Code);
            return CheckoutErrors.InvoiceRejected;
        }
        DateTimeOffset completedAt = _timeProvider.GetUtcNow();
        if (completedAt >= order.PaymentDeadline)
        {
            return CheckoutErrors.OrderExpired;
        }
        if (string.IsNullOrWhiteSpace(order.InvoiceRef))
        {
            Result attached = order.AttachInvoice(invoice.InvoiceRef, completedAt);
            if (attached.IsFailure)
            {
                _logger.LogError("Không gắn được hóa đơn vào đơn {OrderCode}: {ErrorCode}.", order.OrderCode, attached.Error.Code);
                return CheckoutErrors.InvoiceRejected;
            }
        }

        var localResult = await CompleteInvoiceLocalAsync(order, completedAt, invoice, cancellationToken);
        if (localResult.IsFailure)
        {
            return localResult.Error;
        }

        if (!IsReady(invoice, order) &&
            !string.Equals(invoice.Status, "Paid", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(invoice.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(invoice.Status, "Expired", StringComparison.OrdinalIgnoreCase))
        {
            return CheckoutErrors.InvoiceTemporarilyUnavailable;
        }

        return invoice;
    }

    private async Task<Result> CompleteInvoiceLocalAsync(
        RentalOrder order,
        DateTimeOffset completedAt,
        CreateInvoiceResponse invoiceResponse,
        CancellationToken cancellationToken)
    {
        string opKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", order.Id.ToString());
        string responseJson = JsonSerializer.Serialize(invoiceResponse);
        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == opKey);
        if (receipt != null)
        {
            receipt.MarkSucceeded(completedAt, responseJson, responseJson);
        }
        else
        {
            order.AddOperationReceipt(new RentalOperationReceipt(
                Guid.NewGuid(), order.Id, opKey, "CreateInvoice", opKey,
                isSuccess: true, errorCode: null, errorMessage: null,
                completedAt, RentalOperationReceipt.StatusSucceeded,
                completedAt: completedAt,
                responseJson: responseJson,
                apiResponseJson: responseJson));
        }

        var op = await _checkoutOperations.GetByExpectedOrderIdAsync(order.Id, cancellationToken);
        string invoiceStatus = IsReady(invoiceResponse, order) ? "Ready" : "Pending";
        if (op != null && op.InvoiceStatus != invoiceStatus)
        {
            op.SetInvoiceStatus(invoiceStatus, completedAt);
            await _checkoutOperations.UpdateAsync(op, cancellationToken);
        }

        await _orders.UpdateAsync(order, cancellationToken);
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _unitOfWork.DiscardChanges();
            return CheckoutErrors.InvoicePersistenceConflict;
        }

        return Result.Success();
    }

    private static bool MatchesOrder(CreateInvoiceResponse invoice, RentalOrder order) =>
        !string.IsNullOrWhiteSpace(invoice.InvoiceRef) &&
        (string.IsNullOrWhiteSpace(order.InvoiceRef) || string.Equals(invoice.InvoiceRef, order.InvoiceRef, StringComparison.Ordinal)) &&
        invoice.TotalAmount == order.TotalPayableAmount &&
        string.Equals(invoice.Currency, order.Currency, StringComparison.OrdinalIgnoreCase) &&
        invoice.PaymentDeadline.UtcTicks / TimeSpan.TicksPerMicrosecond ==
        order.PaymentDeadline.UtcTicks / TimeSpan.TicksPerMicrosecond;

    private static bool IsReady(CreateInvoiceResponse invoice, RentalOrder order) =>
        order.TotalPayableAmount == 0
            ? string.Equals(invoice.Status, "Paid", StringComparison.OrdinalIgnoreCase)
            : string.Equals(invoice.Status, "Pending", StringComparison.OrdinalIgnoreCase) &&
              Uri.TryCreate(invoice.QrUrl, UriKind.Absolute, out var qrUri) &&
              qrUri.Scheme == Uri.UriSchemeHttps &&
              !string.IsNullOrWhiteSpace(invoice.Bank) &&
              !string.IsNullOrWhiteSpace(invoice.Account) &&
              !string.IsNullOrWhiteSpace(invoice.TransferContent);

    /// <summary>Không kết nối được hoặc Payment lỗi phía máy chủ: gọi lại có thể thành công.</summary>
    private static bool IsTemporary(Error error) =>
        error.Type is ErrorType.ServiceUnavailable or ErrorType.Failure;
}
