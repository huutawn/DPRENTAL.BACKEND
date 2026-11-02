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
    // Renter xác nhận đã nhận máy trên biên bản có sẵn; retry trả xác nhận cũ, không tạo lần bàn giao hay state thuê mới.
    public async Task<Result<AcknowledgeReceiptResponse>> AcknowledgeAsync(
        Guid itemId,
        AcknowledgeReceiptRequest? request,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(caller);

        // Dừng nhánh không hợp lệ với mã INVALID_NOTE; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (request?.Note?.Length > 2000)
        {
            return Error.Validation("INVALID_NOTE", "Ghi chú không được vượt quá 2000 ký tự.");
        }

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var orderId = await orderRepository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (!orderId.HasValue)
        {
            return RentalErrors.ItemNotFound;
        }

        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await lockManager.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var order = await orderRepository.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null)
        {
            return RentalErrors.ItemNotFound;
        }

        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (item is null)
        {
            return RentalErrors.ItemNotFound;
        }

        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        var auth = RentalAuthorizationPolicy.AuthorizeRenterAction(caller, order);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (auth.IsFailure)
        {
            return auth.Error.Type == ErrorType.NotFound ? RentalErrors.ItemNotFound : auth.Error;
        }

        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
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

        // Giữ định danh/vai trò đã xác định cho thao tác để audit và các bước tiếp theo dùng cùng chủ thể.
        Guid renterId = caller.UserId!.Value;
        DateTimeOffset utcNow = timeProvider.GetUtcNow();
        string opKey = $"ack-{item.Id:N}";

        // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
        item.HandoverRecord.Acknowledge(renterId, utcNow, request?.Note);

        // Lưu dấu chuyển trạng thái kèm actor và operation để đối soát lịch sử của quyết định này.
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

        // Cập nhật version/mốc sửa dữ liệu đọc để client và event nhận biết một thay đổi mới của order.
        order.RecordReadModelUpdate(utcNow);

        // Theo dõi transaction do chính hàm mở để chỉ commit/rollback/dispose phạm vi mình sở hữu.
        IUnitOfWorkTransaction? tx = null;
        // Chỉ tự mở transaction khi caller chưa sở hữu một transaction, tránh commit nhầm phạm vi do bên ngoài quản lý.
        if (!unitOfWork.HasActiveTransaction)
        {
            // Mở transaction cho phần ghi local; các thay đổi và Outbox trong phạm vi này phải được commit/rollback cùng nhau.
            tx = await unitOfWork.BeginTransactionAsync(cancellationToken);
        }

        // Thực hiện phần ghi có xử lý lỗi: nhánh catch rollback/bỏ thay đổi theo ranh giới transaction của luồng này.
        try
        {
            // Đăng ký thay đổi aggregate với repository trước khi lưu; không truy cập DbContext trực tiếp từ service.
            await orderRepository.UpdateAsync(order, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (tx != null) await tx.CommitAsync(cancellationToken);
        }
        // Lỗi trong phạm vi ghi: rollback transaction và bỏ graph đang tracked trước khi trả lỗi hoặc ném lại exception.
        catch
        {
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (tx != null) await tx.RollbackAsync(cancellationToken);
            unitOfWork.DiscardChanges();
            throw;
        }
        // Luôn dọn tài nguyên do phạm vi này sở hữu, kể cả khi return sớm hoặc phát sinh exception.
        finally
        {
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (tx != null) await tx.DisposeAsync();
        }

        // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
        if (notifier != null)
        {
            // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
            await notifier.NotifyRenterOrderChangedAsync(
                order.RenterId,
                new RenterOrderChangedNotification(
                    Guid.NewGuid(),
                    order.Id,
                    order.Version,
                    OrderChangeTypes.HandoverAcknowledged,
                    utcNow),
                cancellationToken);

            // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
            var subOrder = order.SubOrders.FirstOrDefault(s => s.SupplierId == item.SupplierId);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (subOrder != null)
            {
                // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
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

        // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
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
