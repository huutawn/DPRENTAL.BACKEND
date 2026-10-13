#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Application.Operations;
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
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CheckoutInvoiceIssuer> _logger;

    public CheckoutInvoiceIssuer(
        IPaymentRentalClient payment,
        IRentalOrderRepository orders,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<CheckoutInvoiceIssuer> logger)
    {
        _payment = payment ?? throw new ArgumentNullException(nameof(payment));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Result> EnsureInvoiceAsync(RentalOrder order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (!string.IsNullOrWhiteSpace(order.InvoiceRef))
        {
            return Result.Success();
        }

        if (order.Status != RentalOrderStatus.PendingPayment || _timeProvider.GetUtcNow() >= order.PaymentDeadline)
        {
            return CheckoutErrors.OrderExpired;
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
        if (invoice.TotalAmount != order.TotalPayableAmount ||
            !string.Equals(invoice.Currency, order.Currency, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "Hóa đơn Payment trả về cho đơn {OrderCode} lệch tổng tiền hoặc loại tiền so với đơn.", order.OrderCode);
            return CheckoutErrors.InvoiceRejected;
        }

        Result attached = order.AttachInvoice(invoice.InvoiceRef, _timeProvider.GetUtcNow());
        if (attached.IsFailure)
        {
            _logger.LogError("Không gắn được hóa đơn vào đơn {OrderCode}: {ErrorCode}.", order.OrderCode, attached.Error.Code);
            return CheckoutErrors.InvoiceRejected;
        }

        await _orders.UpdateAsync(order, cancellationToken);
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Đơn vừa bị luồng khác sửa (ví dụ quét hết hạn); lần gửi lại sẽ đọc trạng thái mới của đơn.
            _unitOfWork.DiscardChanges();
            return CheckoutErrors.InvoiceTemporarilyUnavailable;
        }

        return Result.Success();
    }

    /// <summary>Không kết nối được hoặc Payment lỗi phía máy chủ: gọi lại có thể thành công.</summary>
    private static bool IsTemporary(Error error) =>
        error.Type is ErrorType.ServiceUnavailable or ErrorType.Failure;
}
