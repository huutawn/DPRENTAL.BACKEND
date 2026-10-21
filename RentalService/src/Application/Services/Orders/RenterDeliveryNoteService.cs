using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.Orders;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Application.Services.Orders;

/// <summary>
/// Người thuê sửa ghi chú giao hàng của đơn mình. Chỉ sửa được khi chưa có máy nào được bàn giao
/// và đơn chưa kết thúc; ghi chú sau đó không còn tác dụng với người giao.
/// Hai lần sửa cùng lúc được chặn bằng Version của đơn (concurrency token).
/// </summary>
public sealed class RenterDeliveryNoteService(
    IRentalOrderRepository orders,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IRenterDeliveryNoteService
{
    private static readonly RentalOrderStatus[] EditableOrderStatuses =
        [RentalOrderStatus.PendingPayment, RentalOrderStatus.Paid, RentalOrderStatus.AwaitingHandover];

    private static readonly RentalItemStatus[] BeforeHandoverItemStatuses =
    [
        RentalItemStatus.PendingPayment, RentalItemStatus.PendingSupplierConfirmation, RentalItemStatus.AwaitingHandover,
        RentalItemStatus.Rejected, RentalItemStatus.Expired, RentalItemStatus.Cancelled
    ];

    public async Task<Result<OrderDeliveryResponse>> UpdateDeliveryNoteAsync(
        Guid orderId,
        UpdateDeliveryNoteRequest request,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result<Guid> renter = RentalAuthorizationPolicy.AuthorizeRenterCaller(caller);
        if (renter.IsFailure)
        {
            return renter.Error;
        }

        string? note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is not null && note.Length > CheckoutRequestValidator.MaxDeliveryNoteLength)
        {
            return Error.Validation("Order.InvalidDeliveryNote",
                $"Ghi chú tối đa {CheckoutRequestValidator.MaxDeliveryNoteLength} ký tự.");
        }

        RentalOrder? order = await orders.GetByIdAsync(orderId, cancellationToken);
        if (order is null || order.RenterId != renter.Value)
        {
            return OrderQueryErrors.OrderNotFound;
        }

        if (!EditableOrderStatuses.Contains(order.Status) ||
            order.Items.Any(i => !BeforeHandoverItemStatuses.Contains(i.Status)))
        {
            return Error.Conflict("Order.DeliveryNoteLocked",
                "Không sửa được ghi chú khi máy đã được bàn giao hoặc đơn đã kết thúc.");
        }

        order.UpdateDeliveryNote(note, timeProvider.GetUtcNow());
        await orders.UpdateAsync(order, cancellationToken);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            unitOfWork.DiscardChanges();
            return Error.Conflict("Order.ConcurrencyConflict", "Đơn hàng vừa được cập nhật. Vui lòng tải lại rồi sửa.");
        }

        return OrderQueryMapper.ToDelivery(order);
    }
}
