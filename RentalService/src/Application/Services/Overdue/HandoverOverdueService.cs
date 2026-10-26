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

public sealed class HandoverOverdueService(
    IRentalOrderRepository orders,
    IOrderLockManager locks,
    IItemCancellationService cancellationService,
    IOutboxRepository outboxRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    IOptions<HandoverOverdueOptions> options,
    ILogger<HandoverOverdueService> logger,
    IRentalEventProducer? rentalEvents = null) : IHandoverOverdueService
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

    public Task<IReadOnlyList<Guid>> GetDueOrderIdsAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        return orders.GetHandoverOverdueOrderIdsAsync(
            HandoverDeadlinePolicy.VietnamDate(now),
            HandoverDeadlinePolicy.AutoExpireStartBefore(now, _autoExpireGraceHours),
            batchSize,
            cancellationToken);
    }

    public async Task<HandoverOverdueResult> ProcessOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await orders.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order is null) return new(0, 0, 0);

        DateTimeOffset now = timeProvider.GetUtcNow();
        int autoExpired = 0;
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

            // Kết thúc mục thuê chưa bàn giao sau thời gian gia hạn kể từ 00:00 ngày D+1.
            if (HandoverDeadlinePolicy.IsAutoExpireDue(item, now, _autoExpireGraceHours))
            {
                string opKey = RentalOperationExecutor.ComputeOperationKey("HandoverDeadlineExpired", $"{order.Id}:{item.Id}");
                var expiryResult = await cancellationService.ExpireNoHandoverAsync(
                    item.Id,
                    "Hết hạn bàn giao thiết bị",
                    opKey,
                    cancellationToken);

                if (expiryResult.IsSuccess)
                {
                    if (expiryResult.Value)
                    {
                        autoExpired++;
                        processedItems++;
                        logger.LogInformation("Đã kết thúc Item {ItemId} quá hạn bàn giao trong Order {OrderId}.", item.Id, order.Id);
                    }
                }
                else
                {
                    logger.LogWarning("Kết thúc Item {ItemId} quá hạn bàn giao không thành công: {Code}", item.Id, expiryResult.Error.Code);
                }
                continue;
            }

            // 2. Mới qua mốc quá hạn giao (now >= S + 1) -> Phát cảnh báo 1 lần
            if (HandoverDeadlinePolicy.IsNoticeDue(item, now))
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

                    freshOrder.RecordReadModelUpdate(now);

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

                    if (notifyResult.IsFailure)
                    {
                        logger.LogWarning("Không thể enqueue sự kiện cảnh báo quá hạn giao cho Item {ItemId}: {Error}", freshItem.Id, notifyResult.Error.Message);
                        continue;
                    }

                    await orders.UpdateAsync(freshOrder, cancellationToken);
                    await unitOfWork.SaveChangesAsync(cancellationToken);

                    noticesSent++;
                    processedItems++;
                    logger.LogInformation("Đã phát sự kiện cảnh báo quá hạn giao cho Item {ItemId} trong Order {OrderId}.", freshItem.Id, freshOrder.Id);
                }
            }
        }

        return new(processedItems, autoExpired, noticesSent);
    }
}
