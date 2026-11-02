using Dprental.RentalService.Application.Hubs;
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
    TimeProvider timeProvider,
    IOrderHubNotifier? notifier = null) : IRenterDeliveryNoteService
{
    private static readonly RentalOrderStatus[] EditableOrderStatuses =
        [RentalOrderStatus.PendingPayment, RentalOrderStatus.Paid, RentalOrderStatus.AwaitingHandover];

    private static readonly RentalItemStatus[] BeforeHandoverItemStatuses =
    [
        RentalItemStatus.PendingPayment, RentalItemStatus.PendingSupplierConfirmation, RentalItemStatus.AwaitingHandover,
        RentalItemStatus.Rejected, RentalItemStatus.Expired, RentalItemStatus.Cancelled
    ];

    // Xác thực renter, kiểm tra trạng thái cho phép sửa và dùng optimistic concurrency khi lưu; supplier chỉ được báo khi order đã qua chờ tiền.
    public async Task<Result<OrderDeliveryResponse>> UpdateDeliveryNoteAsync(
        Guid orderId,
        UpdateDeliveryNoteRequest request,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(request);

        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        Result<Guid> renter = RentalAuthorizationPolicy.AuthorizeRenterCaller(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (renter.IsFailure)
        {
            return renter.Error;
        }

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string? note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (note is not null && note.Length > CheckoutRequestValidator.MaxDeliveryNoteLength)
        {
            return Error.Validation("Order.InvalidDeliveryNote",
                $"Ghi chú tối đa {CheckoutRequestValidator.MaxDeliveryNoteLength} ký tự.");
        }

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        RentalOrder? order = await orders.GetByIdAsync(orderId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null || order.RenterId != renter.Value)
        {
            return OrderQueryErrors.OrderNotFound;
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (!EditableOrderStatuses.Contains(order.Status) ||
            order.Items.Any(i => !BeforeHandoverItemStatuses.Contains(i.Status)))
        {
            return Error.Conflict("Order.DeliveryNoteLocked",
                "Không sửa được ghi chú khi máy đã được bàn giao hoặc đơn đã kết thúc.");
        }

        // Đổi ghi chú và mốc sửa của order; nếu version đã bị thay đổi đồng thời, nhánh SaveChanges bên dưới trả conflict.
        order.UpdateDeliveryNote(note, timeProvider.GetUtcNow());
        await orders.UpdateAsync(order, cancellationToken);
        // Thực hiện lời gọi có thể phát sinh exception; nhánh catch phía dưới quyết định trả lỗi, retry hoặc chuyển tiếp exception.
        try
        {
            // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        // Bỏ thay đổi tracked khi lưu thất bại; giữ cách trả conflict hoặc chuyển tiếp exception của use case.
        catch (DbUpdateConcurrencyException)
        {
            // Bỏ thay đổi đang tracked sau lỗi để lần xử lý tiếp theo không lưu nhầm dữ liệu của nhánh đã thất bại.
            unitOfWork.DiscardChanges();
            return Error.Conflict("Order.ConcurrencyConflict", "Đơn hàng vừa được cập nhật. Vui lòng tải lại rồi sửa.");
        }

        // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
        if (notifier != null)
        {
            // Dùng thời điểm thay đổi đã lưu trên order cho notification, không đổi mốc nghiệp vụ thành lúc gửi thông báo.
            DateTimeOffset occurredAt = order.UpdatedAt;
            await notifier.NotifyRenterOrderChangedAsync(
                order.RenterId,
                new RenterOrderChangedNotification(
                    Guid.NewGuid(),
                    order.Id,
                    order.Version,
                    OrderChangeTypes.DeliveryNoteUpdated,
                    occurredAt),
                cancellationToken);

            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (order.Status != RentalOrderStatus.PendingPayment)
            {
                // Xử lý riêng từng đơn con thuộc order; không trộn thông tin hoặc thông báo giữa các supplier.
                foreach (var subOrder in order.SubOrders)
                {
                    // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                    await notifier.NotifySupplierSubOrderChangedAsync(
                        subOrder.SupplierId,
                        new SupplierSubOrderChangedNotification(
                            Guid.NewGuid(),
                            order.Id,
                            subOrder.Id,
                            order.Version,
                            OrderChangeTypes.DeliveryNoteUpdated,
                            occurredAt),
                        cancellationToken);
                }
            }
        }

        return OrderQueryMapper.ToDelivery(order);
    }
}
