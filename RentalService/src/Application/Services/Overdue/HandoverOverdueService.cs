using Dprental.RentalService.Application.Hubs;
#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.IServices.Cancellation;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.IServices.Overdue;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Services.Overdue;

/// <remarks>
/// Quét item AwaitingHandover theo mốc giờ Việt Nam: cảnh báo trước, tự hết hạn sau thời gian gia hạn.
/// Đọc lại dưới order lock; tác vụ bàn giao đang chờ Inventory phải được đối soát trước khi hủy và hoàn tiền.
/// </remarks>
public sealed class HandoverOverdueService(
    IRentalOrderRepository orders,
    IOrderLockManager locks,
    IItemCancellationService cancellationService,
    IOutboxRepository outboxRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    IOptions<HandoverOverdueOptions> options,
    ILogger<HandoverOverdueService> logger,
    IRentalEventProducer? rentalEvents = null,
    IOrderHubNotifier? notifier = null) : IHandoverOverdueService
{
    private readonly IRentalEventProducer _rentalEvents = rentalEvents ?? new Dprental.RentalService.Application.Kafka.Producers.RentalEventProducer(
        outboxRepository,
        Microsoft.Extensions.Options.Options.Create(new Dprental.RentalService.Application.Kafka.Configuration.KafkaOptions { RentalEventsTopic = KafkaContractSpec.Topics.RentalEvents }),
        timeProvider);
    private readonly int _autoExpireGraceHours = options.Value.AutoExpireGraceHours;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // Tính mốc ngày Việt Nam và ngày tự hết hạn theo grace period để repository chọn batch order ứng viên.
    public Task<IReadOnlyList<Guid>> GetDueOrderIdsAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset now = timeProvider.GetUtcNow();
        return orders.GetHandoverOverdueOrderIdsAsync(
            HandoverDeadlinePolicy.VietnamDate(now),
            HandoverDeadlinePolicy.AutoExpireStartBefore(now, _autoExpireGraceHours),
            batchSize,
            cancellationToken);
    }

    // Khóa và đọc lại order, lập cảnh báo cho item quá hạn giao; tách danh sách cần tự hết hạn để xử lý theo use case hủy riêng.
    public async Task<HandoverOverdueResult> ProcessOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var order = await orders.GetByIdWithDetailsAsync(orderId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null) return new(0, 0, 0);

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset now = timeProvider.GetUtcNow();
        int autoExpired = 0;
        int noticesSent = 0;
        int processedItems = 0;

        // Chọn, sắp xếp hoặc chiếu dữ liệu đã có thành tập phục vụ bước tiếp theo; không thay đổi nguồn dữ liệu gốc.
        var candidateItems = order.Items.Where(i => i.Status == RentalItemStatus.AwaitingHandover).ToList();

        // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
        foreach (var item in candidateItems)
        {
            // Tra cứu intent bàn giao còn Pending/Processing/cần đối soát; trạng thái local chưa đổi không chứng minh máy chưa được giao.
            bool handoverInProgress = order.OperationReceipts.Any(r =>
                r.TargetItemId == item.Id &&
                r.Action == "MarkAssetsHandedOver" &&
                r.Status is RentalOperationReceipt.StatusPending or RentalOperationReceipt.StatusProcessing or RentalOperationReceipt.StatusManualReconciliationRequired);

            // Bảo vệ thao tác đang chạy hoặc chưa rõ kết quả; cần đối soát trước khi tạo quyết định có thể mâu thuẫn.
            if (handoverInProgress)
            {
                // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                logger.LogInformation("Item {ItemId} trong Order {OrderId} đang có handover dở dang, bỏ qua.", item.Id, order.Id);
                continue;
            }

            // Kết thúc mục thuê chưa bàn giao sau thời gian gia hạn kể từ 00:00 ngày D+1.
            if (HandoverDeadlinePolicy.IsAutoExpireDue(item, now, _autoExpireGraceHours))
            {
                // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
                string opKey = RentalOperationExecutor.ComputeOperationKey("HandoverDeadlineExpired", $"{order.Id}:{item.Id}");
                var expiryResult = await cancellationService.ExpireNoHandoverAsync(
                    item.Id,
                    "Hết hạn bàn giao thiết bị",
                    opKey,
                    cancellationToken);

                // Xử lý kết quả thành công ở nhánh này; nhánh còn lại giữ cách trả lỗi hoặc đếm thất bại của caller.
                if (expiryResult.IsSuccess)
                {
                    // Chỉ tăng bộ đếm auto-expired khi use case báo thực sự áp dụng hết hạn, không đếm nhánh no-op như một lần đổi trạng thái.
                    if (expiryResult.Value)
                    {
                        // Cập nhật số lượng xử lý của batch để báo cáo tiến độ; không dùng bộ đếm thay cho trạng thái đã lưu của từng tác vụ.
                        autoExpired++;
                        processedItems++;
                        logger.LogInformation("Đã kết thúc Item {ItemId} quá hạn bàn giao trong Order {OrderId}.", item.Id, order.Id);
                    }
                }
                else
                {
                    // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                    logger.LogWarning("Kết thúc Item {ItemId} quá hạn bàn giao không thành công: {Code}", item.Id, expiryResult.Error.Code);
                }
                continue;
            }

            // 2. Mới qua mốc quá hạn giao (now >= S + 1) -> Phát cảnh báo 1 lần
            if (HandoverDeadlinePolicy.IsNoticeDue(item, now))
            {
                // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
                await using var orderLock = await locks.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(15), cancellationToken);

                // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
                var freshOrder = await orders.GetByIdWithDetailsAsync(orderId, cancellationToken);
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (freshOrder is null) continue;

                // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
                var freshItem = freshOrder.Items.FirstOrDefault(i => i.Id == item.Id);
                // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
                if (freshItem is null || freshItem.Status != RentalItemStatus.AwaitingHandover) continue;

                // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
                string noticeOpKey = RentalOperationExecutor.ComputeOperationKey("HandoverOverdueNotice", $"{freshOrder.Id}:{freshItem.Id}");
                bool noticeAlreadySent = freshOrder.OperationReceipts.Any(r =>
                    r.Action == "HandoverOverdueNotice" &&
                    (r.OperationId == noticeOpKey || r.TargetItemId == freshItem.Id));

                // Chỉ tạo cảnh báo khi chưa có receipt cảnh báo tương ứng; worker quét lặp không được phát cùng cảnh báo mãi.
                if (!noticeAlreadySent)
                {
                    // Chuẩn bị bản ghi journal/Inbox để lưu định danh và nội dung xử lý; chỉ được xem là đã ghi nhận sau lần lưu thành công.
                    var noticeReceipt = new RentalOperationReceipt(
                        Guid.NewGuid(),
                        freshOrder.Id,
                        noticeOpKey,
                        "HandoverOverdueNotice",
                        RentalOperationExecutor.ComputeFingerprint($"HandoverOverdueNotice:{freshOrder.Id}:{freshItem.Id}"),
                        isSuccess: true,
                        errorCode: null,
                        errorMessage: null,
                        createdAt: now,
                        status: RentalOperationReceipt.StatusSucceeded,
                        completedAt: now,
                        targetItemId: freshItem.Id);
                    freshOrder.AddOperationReceipt(noticeReceipt);

                    // Cập nhật version/mốc sửa dữ liệu đọc để client và event nhận biết một thay đổi mới của order.
                    freshOrder.RecordReadModelUpdate(now);

                    // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                    var notifyResult = await _rentalEvents.EnqueueRentalHandoverOverdueAsync(
                        new RentalHandoverOverduePayload(
                            freshOrder.Id,
                            freshItem.Id,
                            freshItem.SupplierId,
                            freshOrder.RenterId,
                            freshItem.ExpectedStartDate,
                            HandoverDeadlinePolicy.NoticeAt(freshItem.ExpectedStartDate),
                            freshOrder.Version),
                        correlationId: noticeOpKey,
                        cancellationToken: cancellationToken);

                    // Trả lỗi từ bước kiểm tra vừa chạy; không thực hiện phần ghi hoặc gọi provider tiếp theo khi điều kiện chưa đạt.
                    if (notifyResult.IsFailure)
                    {
                        // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                        logger.LogWarning("Không thể enqueue sự kiện cảnh báo quá hạn giao cho Item {ItemId}: {Error}", freshItem.Id, notifyResult.Error.Message);
                        continue;
                    }

                    // Lưu graph vừa được đọc lại dưới khóa, không ghi đè bằng bản order của lần quét trước.
                    await orders.UpdateAsync(freshOrder, cancellationToken);
                    await unitOfWork.SaveChangesAsync(cancellationToken);

                    // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
                    if (notifier != null)
                    {
                        // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                        await notifier.NotifyRenterOrderChangedAsync(
                            freshOrder.RenterId,
                            new RenterOrderChangedNotification(
                                Guid.NewGuid(),
                                freshOrder.Id,
                                freshOrder.Version,
                                OrderChangeTypes.HandoverOverdue,
                                now),
                            cancellationToken);

                        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
                        var subOrder = freshOrder.SubOrders.FirstOrDefault(s => s.SupplierId == freshItem.SupplierId);
                        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                        if (subOrder != null)
                        {
                            // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                            await notifier.NotifySupplierSubOrderChangedAsync(
                                freshItem.SupplierId,
                                new SupplierSubOrderChangedNotification(
                                    Guid.NewGuid(),
                                    freshOrder.Id,
                                    subOrder.Id,
                                    freshOrder.Version,
                                    OrderChangeTypes.HandoverOverdue,
                                    now),
                                cancellationToken);
                        }
                    }

                    // Cập nhật số lượng xử lý của batch để báo cáo tiến độ; không dùng bộ đếm thay cho trạng thái đã lưu của từng tác vụ.
                    noticesSent++;
                    processedItems++;
                    logger.LogInformation("Đã phát sự kiện cảnh báo quá hạn giao cho Item {ItemId} trong Order {OrderId}.", freshItem.Id, freshOrder.Id);
                }
            }
        }

        return new(processedItems, autoExpired, noticesSent);
    }
}
