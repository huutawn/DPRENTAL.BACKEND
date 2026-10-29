#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.DTOs.Handover;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.IServices.Handover;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Handover;

/// <remarks>
/// Ghi nhận bằng chứng người thuê xác nhận đã nhận máy sau kiểm tra quyền và trạng thái.
/// Đây là xác nhận bổ sung của renter; không thay thế lệnh bàn giao của supplier hoặc kết quả Inventory.
/// </remarks>
public sealed class HandoverAcknowledgementService(
    IRentalOrderRepository orderRepository,
    IOrderLockManager lockManager,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<HandoverAcknowledgementService> logger,
    IOrderHubNotifier? notifier = null) : IHandoverAcknowledgementService
{
    public async Task<Result<AcknowledgeReceiptResponse>> AcknowledgeAsync(
        Guid itemId,
        AcknowledgeReceiptRequest? request,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (request?.Note?.Length > 2000)
        {
            return Error.Validation("INVALID_NOTE", "Ghi chú không được vượt quá 2000 ký tự.");
        }

        var orderId = await orderRepository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (!orderId.HasValue)
        {
            return RentalErrors.ItemNotFound;
        }

        await using var orderLock = await lockManager.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await orderRepository.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        if (order is null)
        {
            return RentalErrors.ItemNotFound;
        }

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        var auth = RentalAuthorizationPolicy.AuthorizeRenterAction(caller, order);
        if (auth.IsFailure)
        {
            return auth.Error.Type == ErrorType.NotFound ? RentalErrors.ItemNotFound : auth.Error;
        }

        if (item.HandoverRecord is null)
        {
            return Error.Conflict("HANDOVER_NOT_RECORDED", "Mục thuê chưa có biên bản bàn giao từ nhà cung cấp.");
        }

        // Idempotency: nếu đã xác nhận trước đó thì trả về kết quả đã lưu mà không ghi đè
        if (item.HandoverRecord.RenterAcknowledgedAt.HasValue && item.HandoverRecord.RenterAcknowledgedById.HasValue)
        {
            return new AcknowledgeReceiptResponse(
                item.Id,
                item.HandoverRecord.Id,
                item.HandoverRecord.RenterAcknowledgedAt.Value,
                item.HandoverRecord.RenterAcknowledgedById.Value,
                item.HandoverRecord.RenterAcknowledgementNote);
        }

        Guid renterId = caller.UserId!.Value;
        DateTimeOffset utcNow = timeProvider.GetUtcNow();
        string opKey = $"ack-{item.Id:N}";

        item.HandoverRecord.Acknowledge(renterId, utcNow, request?.Note);

        order.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            order.Id,
            item.Id,
            item.Status.ToString(),
            item.Status.ToString(),
            "RenterAcknowledgeReceipt",
            renterId,
            ActorRole.Renter,
            "Renter acknowledged receipt of equipment",
            utcNow,
            opKey));

        order.RecordReadModelUpdate(utcNow);

        IUnitOfWorkTransaction? tx = null;
        if (!unitOfWork.HasActiveTransaction)
        {
            tx = await unitOfWork.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            await orderRepository.UpdateAsync(order, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            if (tx != null) await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            if (tx != null) await tx.RollbackAsync(cancellationToken);
            unitOfWork.DiscardChanges();
            throw;
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }

        if (notifier != null)
        {
            await notifier.NotifyRenterOrderChangedAsync(
                order.RenterId,
                new RenterOrderChangedNotification(
                    Guid.NewGuid(),
                    order.Id,
                    order.Version,
                    OrderChangeTypes.HandoverAcknowledged,
                    utcNow),
                cancellationToken);

            var subOrder = order.SubOrders.FirstOrDefault(s => s.SupplierId == item.SupplierId);
            if (subOrder != null)
            {
                await notifier.NotifySupplierSubOrderChangedAsync(
                    item.SupplierId,
                    new SupplierSubOrderChangedNotification(
                        Guid.NewGuid(),
                        order.Id,
                        subOrder.Id,
                        order.Version,
                        OrderChangeTypes.HandoverAcknowledged,
                        utcNow),
                    cancellationToken);
            }
        }

        logger.LogInformation(
            "Renter {RenterId} acknowledged receipt for item {ItemId} in order {OrderId}",
            renterId, item.Id, order.Id);

        return new AcknowledgeReceiptResponse(
            item.Id,
            item.HandoverRecord.Id,
            item.HandoverRecord.RenterAcknowledgedAt ?? utcNow,
            item.HandoverRecord.RenterAcknowledgedById ?? renterId,
            item.HandoverRecord.RenterAcknowledgementNote);
    }
}
