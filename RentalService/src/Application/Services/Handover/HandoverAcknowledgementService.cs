#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.DTOs.Handover;
using Dprental.RentalService.Application.IServices.Handover;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Handover;

public sealed class HandoverAcknowledgementService(
    IRentalOrderRepository orderRepository,
    IOrderLockManager lockManager,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<HandoverAcknowledgementService> logger) : IHandoverAcknowledgementService
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
            return auth.Error;
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

        var result = item.AcknowledgeReceipt(renterId, utcNow, request?.Note, opKey);
        if (result.IsFailure)
        {
            return result.Error;
        }

        try
        {
            await orderRepository.UpdateAsync(order, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            unitOfWork.DiscardChanges();
            throw;
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
