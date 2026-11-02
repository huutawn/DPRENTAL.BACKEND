#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.IServices.Overdue;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Operations;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Overdue;

/// <remarks>
/// Tìm thiết bị đã quá hạn thuê nhưng chưa trả, tạo receipt bảo vệ và giao executor gửi MarkAssetsOverdue.
/// Việc xử lý giữ mục tiêu bảo vệ occupancy ở Inventory; không tự giải phóng thiết bị vì đã qua ngày kết thúc dự kiến.
/// </remarks>
public sealed class OverdueProtectionService(
    IRentalOrderRepository orders,
    IOrderLockManager locks,
    IUnitOfWork unitOfWork,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OverdueProtectionService> logger) : IOverdueProtectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Lấy batch order có máy quá hạn chưa được bảo vệ để worker xử lý, không tự đổi trạng thái trong bước quét.
    public Task<IReadOnlyList<Guid>> GetDueOrderIdsAsync(int batchSize, CancellationToken cancellationToken = default) =>
        orders.GetOverdueUnprotectedOrderIdsAsync(timeProvider.GetUtcNow(), batchSize, cancellationToken);

    // Lập receipt bảo vệ dưới khóa rồi thực thi qua executor; tách bước schedule với bước gọi Inventory.
    public async Task<OverdueOrderResult> ProcessOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        // Lưu receipt trước trong bước schedule rồi mới thực thi descriptor; executor sẽ tự lấy khóa và đọc lại journal.
        IReadOnlyList<OperationReceiptDescriptor> descriptors =
            await ScheduleAsync(orderId, timeProvider.GetUtcNow(), cancellationToken);

        // Cập nhật số lượng xử lý của batch để báo cáo tiến độ; không dùng bộ đếm thay cho trạng thái đã lưu của từng tác vụ.
        int succeeded = 0, failed = 0;
        // Xử lý từng receipt theo journal đã lưu, giữ định danh và trạng thái riêng của mỗi tác vụ.
        foreach (OperationReceiptDescriptor d in descriptors)
        {
            // Scope mới: executor phải đọc dữ liệu mới nhất, không dùng entity đang được tracking ở scope này.
            using IServiceScope scope = scopeFactory.CreateScope();
            var executor = scope.ServiceProvider.GetRequiredService<IRentalOperationExecutor>();

            // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
            var result = await executor.ExecuteScheduledOperationAsync(d, cancellationToken);
            // Xử lý kết quả thành công ở nhánh này; nhánh còn lại giữ cách trả lỗi hoặc đếm thất bại của caller.
            if (result.IsSuccess) succeeded++;
            else
            {
                // Cập nhật số lượng xử lý của batch để báo cáo tiến độ; không dùng bộ đếm thay cho trạng thái đã lưu của từng tác vụ.
                failed++;   // receipt đã có lịch retry; OperationRecoveryWorker sẽ tiếp tục
                logger.LogWarning("Overdue protection {OpId} chưa thành công: {Code}", d.OperationId, result.Error.Code);
            }
        }
        return new OverdueOrderResult(descriptors.Count, succeeded, failed);
    }

    // Khóa Order -> đọc lại -> tạo receipt Pending -> commit -> nhả khóa.
    private async Task<IReadOnlyList<OperationReceiptDescriptor>> ScheduleAsync(
        Guid orderId, DateTimeOffset now, CancellationToken ct)
    {
        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), ct);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        RentalOrder? order = await orders.GetByIdWithDetailsAsync(orderId, ct);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null) return [];

        // Chuẩn bị bản ghi journal/Inbox để lưu định danh và nội dung xử lý; chỉ được xem là đã ghi nhận sau lần lưu thành công.
        var created = new List<OperationReceiptDescriptor>();

        // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
        foreach (RentalItem item in order.Items.Where(i => i.IsOverdue(now)))
        {
            // Xử lý từng unit/assignment trong tập đã chọn; giữ định danh từng máy khi kiểm tra và ghi bằng chứng.
            foreach (RentalUnitAssignment unit in item.GetUnreturnedAssignments())
            {
                // Nhận diện các thế hệ receipt overdue của cùng unit; thế hệ mới cấp thêm retry local nhưng giữ key provider ổn định.
                string prefix = $"overdue:{unit.Id}:";
                var existing = order.OperationReceipts
                    .Where(r => r.Action == RentalItem.OverdueProtectionAction
                             && r.OperationId.StartsWith(prefix, StringComparison.Ordinal))
                    .ToList();

                // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
                if (existing.Any(r => r.IsSuccess && r.Status == RentalOperationReceipt.StatusSucceeded)) continue;

                // Còn receipt đang chạy / chờ retry / lỗi nghiệp vụ thì không tạo thêm.
                // Chỉ khi mọi receipt trước đều đã hết ngân sách retry mới mở thế hệ mới.
                if (existing.Any(r => !IsExhausted(r))) continue;

                // Nhận diện các thế hệ receipt overdue của cùng unit; thế hệ mới cấp thêm retry local nhưng giữ key provider ổn định.
                string operationId = $"{prefix}g{existing.Count}";   // 47 ký tự, giới hạn cột là 64
                // Thế hệ receipt mới chỉ cấp thêm ngân sách retry tại Rental. Key Inventory
                // vẫn theo unit, vì lần cũ có thể đã bảo vệ lịch thành công nhưng mất phản hồi.
                string providerKey = RentalOperationExecutor.ComputeOperationKey(
                    RentalItem.OverdueProtectionAction, unit.Id.ToString("D"));
                string payload = JsonSerializer.Serialize(new OverdueProtectionCommand(
                    unit.Id, new MarkAssetsOverdueRequest([unit.AllocationId], providerKey)), JsonOptions);

                // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
                var receipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(), order.Id, operationId, RentalItem.OverdueProtectionAction,
                    RentalOperationExecutor.ComputeFingerprint(payload), now,
                    targetItemId: item.Id, payloadJson: payload);
                receipt.SetInventoryOperationKey(providerKey);

                // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
                order.AddOperationReceipt(receipt);
                created.Add(new OperationReceiptDescriptor(
                    receipt.Id, order.Id, receipt.OperationId, receipt.Action, receipt.TargetItemId,
                    receipt.Status, receipt.RetryCount, receipt.NextRetryAt, receipt.CreatedAt,
                    receipt.PrerequisiteOperationId, receipt.IsOutcomeUncertain));
            }
        }

        // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
        if (created.Count == 0) return [];

        // Cập nhật version/mốc sửa dữ liệu đọc để client và event nhận biết một thay đổi mới của order.
        order.RecordReadModelUpdate(now);
        await orders.UpdateAsync(order, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return created;
    }

    // Chỉ xem lỗi tạm thời/không rõ kết quả đã hết ngân sách là exhausted; lỗi nghiệp vụ không tự sinh vòng retry mới.
    private static bool IsExhausted(RentalOperationReceipt r) =>
        r.Status == RentalOperationReceipt.StatusFailed &&
        r.ErrorClassification is RentalOperationReceipt.ClassificationTransientExhausted
                              or RentalOperationReceipt.ClassificationUncertainExhausted;
}
