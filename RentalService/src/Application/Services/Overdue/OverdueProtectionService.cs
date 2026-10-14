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

public sealed class OverdueProtectionService(
    IRentalOrderRepository orders,
    IOrderLockManager locks,
    IUnitOfWork unitOfWork,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OverdueProtectionService> logger) : IOverdueProtectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<Guid>> GetDueOrderIdsAsync(int batchSize, CancellationToken cancellationToken = default) =>
        orders.GetOverdueUnprotectedOrderIdsAsync(timeProvider.GetUtcNow(), batchSize, cancellationToken);

    public async Task<OverdueOrderResult> ProcessOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OperationReceiptDescriptor> descriptors =
            await ScheduleAsync(orderId, timeProvider.GetUtcNow(), cancellationToken);

        int succeeded = 0, failed = 0;
        foreach (OperationReceiptDescriptor d in descriptors)
        {
            // Scope mới: executor phải đọc dữ liệu mới nhất, không dùng entity đang được tracking ở scope này.
            using IServiceScope scope = scopeFactory.CreateScope();
            var executor = scope.ServiceProvider.GetRequiredService<IRentalOperationExecutor>();

            var result = await executor.ExecuteScheduledOperationAsync(d, cancellationToken);
            if (result.IsSuccess) succeeded++;
            else
            {
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
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), ct);

        RentalOrder? order = await orders.GetByIdWithDetailsAsync(orderId, ct);
        if (order is null) return [];

        var created = new List<OperationReceiptDescriptor>();

        foreach (RentalItem item in order.Items.Where(i => i.IsOverdue(now)))
        {
            foreach (RentalUnitAssignment unit in item.GetUnreturnedAssignments())
            {
                string prefix = $"overdue:{unit.Id}:";
                var existing = order.OperationReceipts
                    .Where(r => r.Action == RentalItem.OverdueProtectionAction
                             && r.OperationId.StartsWith(prefix, StringComparison.Ordinal))
                    .ToList();

                if (existing.Any(r => r.IsSuccess && r.Status == RentalOperationReceipt.StatusSucceeded)) continue;

                // Còn receipt đang chạy / chờ retry / lỗi nghiệp vụ thì không tạo thêm.
                // Chỉ khi mọi receipt trước đều đã hết ngân sách retry mới mở thế hệ mới.
                if (existing.Any(r => !IsExhausted(r))) continue;

                string operationId = $"{prefix}g{existing.Count}";   // 47 ký tự, giới hạn cột là 64
                // A later receipt generation reuses the same Inventory operation key.
                // This covers the case where Inventory applied protection but its reply was lost.
                string providerKey = RentalOperationExecutor.ComputeOperationKey(
                    RentalItem.OverdueProtectionAction, unit.Id.ToString("D"));
                string payload = JsonSerializer.Serialize(new OverdueProtectionCommand(
                    unit.Id, new MarkAssetsOverdueRequest([unit.AllocationId], providerKey)), JsonOptions);

                var receipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(), order.Id, operationId, RentalItem.OverdueProtectionAction,
                    RentalOperationExecutor.ComputeFingerprint(payload), now,
                    targetItemId: item.Id, payloadJson: payload);

                order.AddOperationReceipt(receipt);
                created.Add(new OperationReceiptDescriptor(
                    receipt.Id, order.Id, receipt.OperationId, receipt.Action, receipt.TargetItemId,
                    receipt.Status, receipt.RetryCount, receipt.NextRetryAt, receipt.CreatedAt,
                    receipt.PrerequisiteOperationId, receipt.IsOutcomeUncertain));
            }
        }

        if (created.Count == 0) return [];

        order.IncrementVersion();
        await orders.UpdateAsync(order, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return created;
    }

    private static bool IsExhausted(RentalOperationReceipt r) =>
        r.Status == RentalOperationReceipt.StatusFailed &&
        r.ErrorClassification is RentalOperationReceipt.ClassificationTransientExhausted
                              or RentalOperationReceipt.ClassificationUncertainExhausted;
}
