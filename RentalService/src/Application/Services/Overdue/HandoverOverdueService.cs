#pragma warning disable CS9113
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

        }

        return new(processedItems, autoExpired, noticesSent);
    }
}
