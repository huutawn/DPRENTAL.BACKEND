#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.IServices.Cancellation;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.IServices.Overdue;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Overdue;

public sealed class HandoverOverdueService(
    IRentalOrderRepository orders,
    IOrderLockManager locks,
    IItemCancellationService cancellationService,
    IOutboxRepository outboxRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<HandoverOverdueService> logger) : IHandoverOverdueService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public Task<IReadOnlyList<Guid>> GetDueOrderIdsAsync(int batchSize, CancellationToken cancellationToken = default) =>
        orders.GetHandoverOverdueOrderIdsAsync(timeProvider.GetUtcNow(), batchSize, cancellationToken);

    public async Task<HandoverOverdueResult> ProcessOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await orders.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order is null) return new(0, 0, 0);

        DateTimeOffset now = timeProvider.GetUtcNow();
        int autoCancelled = 0;
        int noticesSent = 0;
        int processedItems = 0;

        var candidateItems = order.Items.Where(i => i.Status == RentalItemStatus.AwaitingHandover).ToList();

        foreach (var item in candidateItems)
        {
            bool handoverInProgress = order.OperationReceipts.Any(r =>
                r.TargetItemId == item.Id &&
                r.Action == "MarkAssetsHandedOver" &&
                r.Status is RentalOperationReceipt.StatusPending or RentalOperationReceipt.StatusProcessing);

            if (handoverInProgress)
            {
                logger.LogInformation("Item {ItemId} trong Order {OrderId} đang có handover dở dang, bỏ qua.", item.Id, order.Id);
                continue;
            }

            // 1. Quá hạn toàn bộ kỳ thuê (now >= E + 1) -> Tự động hủy (CancelBySystemAsync tự khóa orderId)
            if (item.IsHandoverAutoCancellable(now))
            {
                string opKey = RentalOperationExecutor.ComputeOperationKey("AutoCancel", $"{order.Id}:{item.Id}");
                var cancelResult = await cancellationService.CancelBySystemAsync(
                    item.Id,
                    "Hết kỳ thuê mà chưa bàn giao thiết bị",
                    opKey,
                    cancellationToken);

                if (cancelResult.IsSuccess)
                {
                    autoCancelled++;
                    processedItems++;
                    logger.LogInformation("Tự động hủy thành công Item {ItemId} trong Order {OrderId}.", item.Id, order.Id);
                }
                else
                {
                    logger.LogWarning("Tự động hủy Item {ItemId} không thành công: {Code}", item.Id, cancelResult.Error.Code);
                }
                continue;
            }

            // 2. Mới qua mốc quá hạn giao (now >= S + 1) -> Phát cảnh báo 1 lần
            if (item.IsHandoverOverdue(now))
            {
                await using var orderLock = await locks.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(15), cancellationToken);

                var freshOrder = await orders.GetByIdWithDetailsAsync(orderId, cancellationToken);
                if (freshOrder is null) continue;

                var freshItem = freshOrder.Items.FirstOrDefault(i => i.Id == item.Id);
                if (freshItem is null || freshItem.Status != RentalItemStatus.AwaitingHandover) continue;

                string noticeOpKey = RentalOperationExecutor.ComputeOperationKey("HandoverOverdueNotice", $"{freshOrder.Id}:{freshItem.Id}");
                bool noticeAlreadySent = freshOrder.OperationReceipts.Any(r =>
                    r.Action == "HandoverOverdueNotice" &&
                    (r.OperationId == noticeOpKey || r.TargetItemId == freshItem.Id));

                if (!noticeAlreadySent)
                {
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

                    var notifyEnvelope = new KafkaMessageEnvelope<RentalHandoverOverduePayload>(
                        Guid.NewGuid(),
                        KafkaContractSpec.EventTypes.RentalHandoverOverdue,
                        KafkaContractSpec.CurrentEnvelopeVersion,
                        KafkaContractSpec.Producers.RentalService,
                        freshOrder.Id.ToString("D"),
                        now,
                        noticeOpKey,
                        null,
                        new RentalHandoverOverduePayload(
                            freshOrder.Id,
                            freshItem.Id,
                            freshItem.SupplierId,
                            freshOrder.RenterId,
                            freshItem.ExpectedStartDate,
                            RentalItem.CalculateHandoverOverdueAt(freshItem.ExpectedStartDate)));

                    var outbox = new RentalOutboxMessage(
                        notifyEnvelope.MessageId,
                        KafkaContractSpec.Topics.RentalEvents,
                        freshOrder.Id.ToString("D"),
                        KafkaContractSpec.EventTypes.RentalHandoverOverdue,
                        JsonSerializer.Serialize(notifyEnvelope, JsonOptions),
                        now);

                    await outboxRepository.AddAsync(outbox, cancellationToken);

                    freshOrder.IncrementVersion();
                    await orders.UpdateAsync(freshOrder, cancellationToken);
                    await unitOfWork.SaveChangesAsync(cancellationToken);

                    noticesSent++;
                    processedItems++;
                    logger.LogInformation("Đã phát sự kiện cảnh báo quá hạn giao cho Item {ItemId} trong Order {OrderId}.", freshItem.Id, freshOrder.Id);
                }
            }
        }

        return new(processedItems, autoCancelled, noticesSent);
    }
}
