using System.Text.Json;
using Dprental.NotificationService.Application.IServices;
using Dprental.NotificationService.Application.Kafka.Contracts;
using Dprental.NotificationService.Application.Kafka.Handlers;
using Dprental.NotificationService.Application.Kafka.Quarantine;
using Dprental.NotificationService.Application.Kafka.Validation;
using Dprental.NotificationService.Application.Policies;
using Dprental.NotificationService.Application.Recipients;
using Dprental.NotificationService.Application.Templates;
using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Dprental.NotificationService.Application.Diagnostics;
namespace Dprental.NotificationService.Application.Kafka.Services;

public enum IngestionOutcome
{
    Processed,
    Duplicate,
    Ignored,
    Quarantined
}

public interface INotificationIngestionService
{
    Task<IngestionOutcome> IngestMessageAsync(
        string rawMessage,
        string topic,
        int partition,
        long offset,
        string? messageKey = null,
        CancellationToken cancellationToken = default);
}

public sealed class NotificationIngestionService : INotificationIngestionService
{
    private readonly string _consumerGroupName;
    private readonly IKafkaContractRegistry _contractRegistry;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IEnumerable<IKafkaEventHandler> _handlers;
    private readonly IRecipientResolutionService _recipientResolver;
    private readonly INotificationTemplateRenderer _templateRenderer;
    private readonly IInboxReceiptRepository _inboxRepo;
    private readonly INotificationRepository _notificationRepo;
    private readonly IDeliveryJobRepository _deliveryJobRepo;
    private readonly IPreferenceRepository _preferenceRepo;
    private readonly IAggregateWatermarkRepository _watermarkRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IKafkaDeadLetterPublisher _dlqPublisher;
    private readonly INotificationRealtimeNotifier _realtimeNotifier;
    private readonly ILogger<NotificationIngestionService> _logger;

    public NotificationIngestionService(
        IEnumerable<IKafkaEventHandler> handlers,
        IRecipientResolutionService recipientResolver,
        INotificationTemplateRenderer templateRenderer,
        IInboxReceiptRepository inboxRepo,
        INotificationRepository notificationRepo,
        IDeliveryJobRepository deliveryJobRepo,
        IPreferenceRepository preferenceRepo,
        IAggregateWatermarkRepository watermarkRepo,
        IUnitOfWork unitOfWork,
        IKafkaDeadLetterPublisher dlqPublisher,
        INotificationRealtimeNotifier realtimeNotifier,
        ILogger<NotificationIngestionService> logger,
        IConfiguration? configuration = null,
        IKafkaContractRegistry? contractRegistry = null)
    {
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _recipientResolver = recipientResolver ?? throw new ArgumentNullException(nameof(recipientResolver));
        _templateRenderer = templateRenderer ?? throw new ArgumentNullException(nameof(templateRenderer));
        _inboxRepo = inboxRepo ?? throw new ArgumentNullException(nameof(inboxRepo));
        _notificationRepo = notificationRepo ?? throw new ArgumentNullException(nameof(notificationRepo));
        _deliveryJobRepo = deliveryJobRepo ?? throw new ArgumentNullException(nameof(deliveryJobRepo));
        _preferenceRepo = preferenceRepo ?? throw new ArgumentNullException(nameof(preferenceRepo));
        _watermarkRepo = watermarkRepo ?? throw new ArgumentNullException(nameof(watermarkRepo));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _dlqPublisher = dlqPublisher ?? throw new ArgumentNullException(nameof(dlqPublisher));
        _realtimeNotifier = realtimeNotifier ?? throw new ArgumentNullException(nameof(realtimeNotifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _consumerGroupName = configuration?["Kafka:GroupId"] ?? "notification-service-v1";
        _contractRegistry = contractRegistry ?? new KafkaContractRegistry(handlers);
    }

    public async Task<IngestionOutcome> IngestMessageAsync(
        string rawMessage,
        string topic,
        int partition,
        long offset,
        string? messageKey = null,
        CancellationToken cancellationToken = default)
    {
        var fallbackQuarantineId = $"{topic}:{partition}:{offset}";

        // 1. Parse JSON Envelope
        KafkaMessageEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<KafkaMessageEnvelope>(rawMessage, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi phân tích JSON envelope. Đẩy vào DLQ: {QuarantineId}", fallbackQuarantineId);
            await _dlqPublisher.PublishToDeadLetterQueueAsync(rawMessage, fallbackQuarantineId, "Lỗi phân tích cú pháp JSON", "INVALID_JSON", cancellationToken);
            return IngestionOutcome.Quarantined;
        }

        // 2. Validate Envelope
        var validationResult = KafkaEnvelopeValidator.Validate(envelope);
        if (validationResult.IsFailure)
        {
            var qId = (envelope?.MessageId != Guid.Empty && envelope?.MessageId != null)
                ? envelope.MessageId.ToString()
                : fallbackQuarantineId;

            _logger.LogWarning("Envelope không hợp lệ ({Code}): {Message}. Đẩy vào DLQ: {QuarantineId}", validationResult.Error.Code, validationResult.Error.Message, qId);
            await _dlqPublisher.PublishToDeadLetterQueueAsync(rawMessage, qId, validationResult.Error.Message, validationResult.Error.Code, cancellationToken);
            return IngestionOutcome.Quarantined;
        }

        var messageId = envelope!.MessageId;

        // 3. Contract Registry Validation & Handler Resolution
        var resolution = _contractRegistry.Resolve(topic, envelope!);
        if (resolution.Status == ContractResolutionStatus.Quarantined)
        {
            var qId = envelope!.MessageId.ToString();
            _logger.LogWarning("Contract vi phạm quy tắc ({Code}): {Message}. Đẩy vào DLQ: {QuarantineId}",
                resolution.ErrorCode, resolution.ErrorMessage, qId);
            await _dlqPublisher.PublishToDeadLetterQueueAsync(
                rawMessage,
                qId,
                resolution.ErrorMessage ?? "Lỗi xác thực hợp đồng Kafka.",
                resolution.ErrorCode ?? "CONTRACT_VIOLATION",
                cancellationToken);
            return IngestionOutcome.Quarantined;
        }

        if (resolution.Status == ContractResolutionStatus.Ignored)
        {
            _logger.LogInformation(
                "Bỏ qua sự kiện không thuộc danh sách quan tâm: Topic={Topic}, MessageType={MessageType}, MessageId={MessageId}, Producer={Producer}",
                topic,
                envelope!.MessageType,
                envelope.MessageId,
                envelope.Producer);

            return IngestionOutcome.Ignored;
        }

        var handler = resolution.Handler!;

        // 4. Idempotency Pre-Check
        var exists = await _inboxRepo.ExistsAsync(_consumerGroupName, messageId, cancellationToken);
        if (exists)
        {
            _logger.LogInformation("Bỏ qua tin nhắn trùng lặp (idempotent pre-check): MessageId={MessageId}", messageId);
            return IngestionOutcome.Duplicate;
        }

        // 5. Parse Event Intent
        var intentResult = handler.ParseIntent(envelope);
        if (intentResult.IsFailure)
        {
            _logger.LogWarning("Không thể parse intent sự kiện ({Code}): {Message}. Đẩy vào DLQ: {MessageId}", intentResult.Error.Code, intentResult.Error.Message, envelope.MessageId);
            await _dlqPublisher.PublishToDeadLetterQueueAsync(rawMessage, envelope.MessageId.ToString(), intentResult.Error.Message, intentResult.Error.Code, cancellationToken);
            return IngestionOutcome.Quarantined;
        }

        var intent = intentResult.Value;

        // 6. Recipient Resolution
        var primaryId = string.Equals(intent.ResourceType, "Settlement", StringComparison.OrdinalIgnoreCase)
            ? (Guid.TryParse(intent.ResourceId, out var resGuid) ? resGuid : (intent.FinancialId ?? intent.OrderId ?? Guid.Empty))
            : (intent.OrderId ?? intent.FinancialId ?? Guid.Empty);

        string? subType = intent.RenderData.TryGetValue("reminderType", out var st) ? st?.ToString() : null;
        var recipientsResult = _recipientResolver.ResolveRecipients(
            intent.EventType,
            primaryId,
            intent.RenterId,
            intent.SupplierId,
            intent.ReturnRequestId,
            subType);
        if (recipientsResult.IsFailure)
        {
            _logger.LogWarning("Không xác định được người nhận hợp lệ ({Code}): {Message}. Đẩy vào DLQ: {MessageId}", recipientsResult.Error.Code, recipientsResult.Error.Message, envelope.MessageId);
            await _dlqPublisher.PublishToDeadLetterQueueAsync(rawMessage, envelope.MessageId.ToString(), recipientsResult.Error.Message, recipientsResult.Error.Code, cancellationToken);
            return IngestionOutcome.Quarantined;
        }

        var recipients = recipientsResult.Value;
        var now = DateTimeOffset.UtcNow;

        var notificationsToCreate = new List<Notification>();
        var deliveryJobsToCreate = new List<NotificationDeliveryJob>();
        var suppressedCtaNotifications = new List<Notification>();
        // 7. Atomic Transaction: Read Watermarks, Evaluate Suppression, Render & Persist
        try
        {
            await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

            // Double check idempotency inside transaction
            var alreadyProcessed = await _inboxRepo.ExistsAsync(_consumerGroupName, messageId, cancellationToken);
            if (alreadyProcessed)
            {
                _logger.LogInformation("Bỏ qua tin nhắn trùng lặp (inside transaction): MessageId={MessageId}", messageId);
                return IngestionOutcome.Duplicate;
            }

            var hasRentalOrder = intent.OrderId.HasValue && intent.OrderId.Value != Guid.Empty && intent.WatermarkScope != null;
            if (hasRentalOrder)
            {
                await _watermarkRepo.AcquireAdvisoryLockAsync(intent.OrderId!.Value, cancellationToken);
            }

            if (string.Equals(intent.EventType, "RentalReminderDue", StringComparison.OrdinalIgnoreCase))
            {
                var vnOffset = TimeSpan.FromHours(7);
                var todayVn = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(vnOffset).DateTime);
                var reminderDateStr = intent.RenderData.TryGetValue("reminderDate", out var rd) ? rd?.ToString() : null;

                if (DateOnly.TryParseExact(reminderDateStr, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var remDate))
                {
                    if (remDate < todayVn)
                    {
                        _logger.LogInformation("Reminder ngày cũ ({ReminderDate} < {TodayVn}) đã hết hiệu lực, bỏ qua và lưu inbox receipt: MessageId={MessageId}",
                            remDate, todayVn, envelope.MessageId);

                        await _inboxRepo.AddAsync(new NotificationInboxReceipt
                        {
                            Id = Guid.NewGuid(),
                            ConsumerName = _consumerGroupName,
                            MessageId = envelope.MessageId,
                            Producer = envelope.Producer,
                            MessageType = envelope.MessageType,
                            Status = "DismissedStaleReminder",
                            ProcessedAt = DateTimeOffset.UtcNow,
                            ErrorCode = null,
                            ErrorDetail = $"Reminder date {remDate} is in the past compared to current Vietnam date {todayVn}"
                        }, cancellationToken);

                        await _unitOfWork.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        NotificationMetrics.IngestionCounter.Add(1, new KeyValuePair<string, object?>("status", "processed"));
                        return IngestionOutcome.Processed;
                    }
                    else if (remDate > todayVn)
                    {
                        var errDetail = $"Reminder date {remDate} is in the future compared to current Vietnam date {todayVn}";
                        _logger.LogWarning("Reminder ngày tương lai: {Detail}. Đẩy vào DLQ: {MessageId}", errDetail, envelope.MessageId);
                        await transaction.RollbackAsync(cancellationToken);
                        await _dlqPublisher.PublishToDeadLetterQueueAsync(rawMessage, envelope.MessageId.ToString(), errDetail, "FUTURE_REMINDER_DATE", cancellationToken);
                        return IngestionOutcome.Quarantined;
                    }
                }
            }

            if (string.Equals(intent.EventType, "RentalItemHandoverCompleted", StringComparison.OrdinalIgnoreCase))
            {
                var targetItemId = intent.RentalItemId ?? (intent.WatermarkScope == NotificationWatermarkScope.RentalItem ? intent.WatermarkId : null);
                if (targetItemId.HasValue)
                {
                    var hoSuppressed = await _notificationRepo.DismissHandoverActionsForRentalItemAsync(targetItemId.Value, cancellationToken);
                    suppressedCtaNotifications.AddRange(hoSuppressed);
                    await _deliveryJobRepo.SuppressPendingAndUndispatchedJobsForRentalItemAsync(targetItemId.Value, "Bàn giao thiết bị đã hoàn tất.", cancellationToken);
                }
            }

            if (string.Equals(intent.EventType, "RentalItemReturned", StringComparison.OrdinalIgnoreCase))
            {
                var targetItemId = intent.RentalItemId ?? (intent.WatermarkScope == NotificationWatermarkScope.RentalItem ? intent.WatermarkId : null);
                if (targetItemId.HasValue)
                {
                    var retSuppressed = await _notificationRepo.DismissReturnOverdueActionsForRentalItemAsync(targetItemId.Value, cancellationToken);
                    suppressedCtaNotifications.AddRange(retSuppressed);
                    await _deliveryJobRepo.SuppressPendingAndUndispatchedJobsForRentalItemAsync(targetItemId.Value, "Hoàn trả thiết bị đã hoàn tất.", cancellationToken);
                }
            }

            AggregateStateProjection? currentAggregateState = null;
            AggregateStateProjection? parentOrderAggregateState = null;
            AggregateStateProjection? parentItemAggregateState = null;

            if (intent.WatermarkScope != null && intent.WatermarkId.HasValue)
            {
                currentAggregateState = await _watermarkRepo.GetAggregateStateAsync(
                    intent.WatermarkScope,
                    intent.WatermarkId.Value,
                    cancellationToken);

                if (intent.OrderId.HasValue && intent.WatermarkScope != NotificationWatermarkScope.RentalOrder)
                {
                    parentOrderAggregateState = await _watermarkRepo.GetAggregateStateAsync(
                        NotificationWatermarkScope.RentalOrder,
                        intent.OrderId.Value,
                        cancellationToken);
                }

                if (intent.WatermarkScope == NotificationWatermarkScope.ReturnRequest)
                {
                    var parentItemId = intent.RentalItemId;
                    if (!parentItemId.HasValue)
                    {
                        var existingWm = await _watermarkRepo.GetWatermarksForAggregateAsync(NotificationWatermarkScope.ReturnRequest, intent.WatermarkId.Value, cancellationToken);
                        parentItemId = existingWm.FirstOrDefault(w => w.ParentItemId.HasValue)?.ParentItemId;
                    }

                    if (parentItemId.HasValue && parentItemId.Value != Guid.Empty)
                    {
                        parentItemAggregateState = await _watermarkRepo.GetAggregateStateAsync(
                            NotificationWatermarkScope.RentalItem,
                            parentItemId.Value,
                            cancellationToken);
                    }
                }
            }

            if (string.Equals(intent.EventType, "RentalReminderDue", StringComparison.OrdinalIgnoreCase))
            {
                var orderTerminalState = parentOrderAggregateState?.TerminalState;
                var isOrderTerminal = StaleActionSuppressionPolicy.IsTerminalState(orderTerminalState) ||
                                      (parentOrderAggregateState?.IsTerminal == true && StaleActionSuppressionPolicy.IsTerminalState(orderTerminalState));

                var itemTerminalState = currentAggregateState?.TerminalState;
                var isItemTerminal = StaleActionSuppressionPolicy.IsTerminalState(itemTerminalState) ||
                                     (currentAggregateState?.IsTerminal == true && StaleActionSuppressionPolicy.IsTerminalState(itemTerminalState));

                var reminderType = intent.RenderData.TryGetValue("reminderType", out var rt) ? rt?.ToString() : null;
                var isHandoverReminder = reminderType is "HandoverReminder" or "HandoverOverdue";
                var isReturnReminder = reminderType is "ReturnReminder" or "ReturnOverdue";

                var handoverAlreadyCompleted = isHandoverReminder && intent.RentalItemId.HasValue &&
                    await _notificationRepo.HasItemHandoverCompletedAsync(intent.RentalItemId.Value, cancellationToken);

                var returnAlreadyCompleted = isReturnReminder && intent.RentalItemId.HasValue &&
                    (string.Equals(currentAggregateState?.TerminalState, "RETURNED", StringComparison.OrdinalIgnoreCase) ||
                     await _notificationRepo.HasItemReturnedAsync(intent.RentalItemId.Value, cancellationToken));

                if (isOrderTerminal || isItemTerminal || handoverAlreadyCompleted || returnAlreadyCompleted)
                {
                    var reason = isOrderTerminal
                        ? $"Đơn thuê đã ở trạng thái kết thúc ({orderTerminalState})."
                        : isItemTerminal
                            ? $"Thiết bị thuê đã ở trạng thái kết thúc ({itemTerminalState})."
                            : handoverAlreadyCompleted
                                ? "Bàn giao thiết bị đã hoàn tất."
                                : "Hoàn trả thiết bị đã hoàn tất.";

                    _logger.LogInformation("Nhắc nhở {Key} không khả dụng do chuyển trạng thái ({Reason}), bỏ qua: MessageId={MessageId}",
                        intent.ResourceId, reason, envelope.MessageId);

                    await _inboxRepo.AddAsync(new NotificationInboxReceipt
                    {
                        Id = Guid.NewGuid(),
                        ConsumerName = _consumerGroupName,
                        MessageId = envelope.MessageId,
                        Producer = envelope.Producer,
                        MessageType = envelope.MessageType,
                        Status = "DismissedStaleReminder",
                        ProcessedAt = DateTimeOffset.UtcNow,
                        ErrorCode = null,
                        ErrorDetail = reason
                    }, cancellationToken);

                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    NotificationMetrics.IngestionCounter.Add(1, new KeyValuePair<string, object?>("status", "processed"));
                    return IngestionOutcome.Processed;
                }
            }

            foreach (var recipient in recipients)
            {
                if (string.Equals(intent.EventType, "RentalReminderDue", StringComparison.OrdinalIgnoreCase) && intent.RentalItemId.HasValue)
                {
                    var alreadyDelivered = await _notificationRepo.ExistsReminderNotificationAsync(
                        recipient.RecipientUserId,
                        intent.RentalItemId.Value,
                        recipient.NotificationType,
                        intent.ResourceId,
                        cancellationToken);
                    if (alreadyDelivered)
                    {
                        _logger.LogInformation("Nhắc nhở {Key} đã được gửi cho người nhận {UserId}, bỏ qua.", intent.ResourceId, recipient.RecipientUserId);
                        continue;
                    }
                }
                NotificationAggregateWatermark? watermark = null;

                if (intent.WatermarkScope != null && intent.WatermarkId.HasValue)
                {
                    watermark = await _watermarkRepo.GetWatermarkAsync(
                        intent.WatermarkScope,
                        intent.WatermarkId.Value,
                        recipient.RecipientUserId,
                        cancellationToken);

                    // Conflicting state check at same version:
                    // Two different messageIds with same version are processed; conflicting states at same version return validation error.
                    if (intent.RaisesWatermark && watermark != null && watermark.LastOrderVersion == intent.OrderVersion)
                    {
                        if (!string.IsNullOrEmpty(watermark.LastMessageId) &&
                            !string.Equals(watermark.LastMessageId, envelope.MessageId.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            var isConflicting = (watermark.IsTerminal != intent.IsTerminal)
                                || (watermark.IsTerminal && intent.IsTerminal && !string.Equals(watermark.TerminalState, intent.TerminalState, StringComparison.OrdinalIgnoreCase));

                            if (isConflicting)
                            {
                                var conflictMsg = $"Xung đột trạng thái sự kiện tại cùng phiên bản {intent.OrderVersion}: hiện tại (Terminal={watermark.IsTerminal}, State={watermark.TerminalState}), sự kiện mới (Terminal={intent.IsTerminal}, State={intent.TerminalState}).";
                                _logger.LogWarning("Envelope có trạng thái xung đột tại cùng phiên bản ({Code}): {Message}. Đẩy vào DLQ: {MessageId}",
                                    "CONFLICTING_STATE", conflictMsg, envelope.MessageId);
                                await transaction.RollbackAsync(cancellationToken);
                                await _dlqPublisher.PublishToDeadLetterQueueAsync(rawMessage, envelope.MessageId.ToString(), conflictMsg, "CONFLICTING_STATE", cancellationToken);
                                return IngestionOutcome.Quarantined;
                            }
                        }
                    }
                }

                var staleEval = StaleActionSuppressionPolicy.EvaluateWithParentState(
                    recipient.NotificationType,
                    intent.OrderVersion,
                    currentAggregateState,
                    parentItemAggregateState,
                    parentOrderAggregateState);
                var renderResult = _templateRenderer.Render(
                    recipient.NotificationType,
                    intent.RenderData,
                    isStale: staleEval.IsStale);

                if (renderResult.IsFailure)
                {
                    _logger.LogError("Lỗi render template ({Code}): {Message}. Đẩy vào DLQ: {MessageId}",
                        renderResult.Error.Code, renderResult.Error.Message, envelope.MessageId);
                    await transaction.RollbackAsync(cancellationToken);
                    await _dlqPublisher.PublishToDeadLetterQueueAsync(rawMessage, envelope.MessageId.ToString(), renderResult.Error.Message, renderResult.Error.Code, cancellationToken);
                    return IngestionOutcome.Quarantined;
                }

                var rendered = renderResult.Value;
                var isActionable = recipient.IsActionable && !staleEval.SuppressAction;
                var actionUrl = isActionable ? recipient.ActionUrl : null;

                var notification = new Notification
                {
                    Id = Guid.NewGuid(),
                    RecipientUserId = recipient.RecipientUserId,
                    OrderId = intent.OrderId,
                    RentalItemId = intent.RentalItemId,
                    NotificationType = recipient.NotificationType,
                    Category = recipient.Category,
                    Severity = recipient.Severity,
                    Title = rendered.Title,
                    Body = rendered.Body,
                    ResourceType = intent.ResourceType,
                    ResourceId = intent.ResourceId,
                    SourceEventId = messageId,
                    SourceProducer = envelope.Producer,
                    SourceEventType = envelope.MessageType,
                    EventVersion = intent.OrderVersion,
                    TemplateCode = rendered.TemplateCode,
                    TemplateVersion = rendered.TemplateVersion,
                    RenderDataJson = rendered.RenderDataJson,
                    DeepLinkUrl = recipient.DeepLinkUrl,
                    IsActionable = isActionable,
                    ActionUrl = actionUrl,
                    OccurredAt = envelope.OccurredAt,
                    CreatedAt = now
                };
                notificationsToCreate.Add(notification);

                // Channel policy and preference check for Email
                var shouldSendEmail = !string.Equals(envelope.MessageType, "RentalReminderDue", StringComparison.OrdinalIgnoreCase)
                    && NotificationChannelPolicy.ShouldSendEmail(recipient.NotificationType, recipient.RecipientRole);
                if (shouldSendEmail && !staleEval.SuppressEmail)
                {
                    var emailAllowed = await _preferenceRepo.IsEmailEnabledAsync(recipient.RecipientUserId, recipient.Category, cancellationToken);
                    var jobStatus = emailAllowed ? "Pending" : "Suppressed";
                    var errorDetail = emailAllowed ? null : "Người dùng đã tắt nhận email cho category này trong preferences.";

                    deliveryJobsToCreate.Add(new NotificationDeliveryJob
                    {
                        Id = Guid.NewGuid(),
                        NotificationId = notification.Id,
                        Channel = "EMAIL",
                        RecipientUserId = recipient.RecipientUserId,
                        Status = jobStatus,
                        MaxAttempts = 5,
                        AttemptCount = 0,
                        NextAttemptAt = now,
                        LastErrorCode = emailAllowed ? null : "PREFERENCE_DISABLED",
                        LastErrorDetail = errorDetail,
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                }
            }

            // Inbox receipt
            await _inboxRepo.AddAsync(new NotificationInboxReceipt
            {
                Id = Guid.NewGuid(),
                ConsumerName = _consumerGroupName,
                MessageId = messageId,
                Producer = envelope.Producer,
                MessageType = envelope.MessageType,
                Status = "Processed",
                ProcessedAt = now
            }, cancellationToken);

            // Notifications
            if (notificationsToCreate.Count > 0)
            {
                await _notificationRepo.AddRangeAsync(notificationsToCreate, cancellationToken);
            }

            // Delivery jobs
            if (deliveryJobsToCreate.Count > 0)
            {
                await _deliveryJobRepo.AddRangeAsync(deliveryJobsToCreate, cancellationToken);
            }

            if (intent.IsTerminal)
            {
                var terminalReason = $"Đơn thuê / thiết bị đã ở trạng thái kết thúc ({intent.TerminalState ?? "TERMINATED"}).";
                if (intent.WatermarkScope == NotificationWatermarkScope.RentalOrder && intent.OrderId.HasValue)
                {
                    var suppressed = await _notificationRepo.SuppressPendingActionsForOrderAsync(intent.OrderId.Value, cancellationToken);
                    suppressedCtaNotifications.AddRange(suppressed);
                    await _deliveryJobRepo.SuppressPendingAndUndispatchedJobsForOrderAsync(intent.OrderId.Value, terminalReason, cancellationToken);

                    // Feature 35: On RentalOrderCancelled / RentalOrderExpired, dismiss both handover and return action reminders and suppress their delivery jobs across all items of the order
                    var itemIds = await _notificationRepo.GetRentalItemIdsForOrderAsync(intent.OrderId.Value, cancellationToken);
                    foreach (var itemId in itemIds)
                    {
                        var hoDismissed = await _notificationRepo.DismissHandoverActionsForRentalItemAsync(itemId, cancellationToken);
                        suppressedCtaNotifications.AddRange(hoDismissed);

                        var retDismissed = await _notificationRepo.DismissReturnOverdueActionsForRentalItemAsync(itemId, cancellationToken);
                        suppressedCtaNotifications.AddRange(retDismissed);

                        await _deliveryJobRepo.SuppressPendingAndUndispatchedJobsForRentalItemAsync(itemId, terminalReason, cancellationToken);
                    }
                }
                else if (intent.WatermarkScope == NotificationWatermarkScope.RentalItem)
                {
                    var itemGuid = intent.RentalItemId ?? (intent.WatermarkScope == NotificationWatermarkScope.RentalItem ? intent.WatermarkId : null);
                    if (itemGuid.HasValue)
                    {
                        var suppressed = await _notificationRepo.SuppressPendingActionsForRentalItemAsync(itemGuid.Value, cancellationToken);
                        suppressedCtaNotifications.AddRange(suppressed);
                        await _deliveryJobRepo.SuppressPendingAndUndispatchedJobsForRentalItemAsync(itemGuid.Value, terminalReason, cancellationToken);

                        var overdueSuppressed = await _notificationRepo.DismissReturnOverdueActionsForRentalItemAsync(itemGuid.Value, cancellationToken);
                        suppressedCtaNotifications.AddRange(overdueSuppressed);
                    }
                }
            }

            if (intent.RaisesWatermark && intent.WatermarkScope != null && intent.WatermarkId.HasValue)
            {
                var isPaidEvent = intent.IsPaid;
                var recipientIds = recipients.Select(r => r.RecipientUserId).Distinct().ToList();
                await _watermarkRepo.UpsertWatermarksForRecipientsAsync(
                    intent.WatermarkScope,
                    intent.WatermarkId.Value,
                    recipientIds,
                    intent.OrderVersion,
                    envelope.MessageId.ToString(),
                    intent.IsTerminal,
                    intent.TerminalState,
                    isPaid: isPaidEvent,
                    parentOrderId: intent.OrderId,
                    parentItemId: intent.RentalItemId,
                    cancellationToken: cancellationToken);

                if (intent.IsTerminal)
                {
                    var allKnown = await _watermarkRepo.GetWatermarksForAggregateAsync(intent.WatermarkScope, intent.WatermarkId.Value, cancellationToken);
                    var otherRecipients = allKnown.Select(w => w.RecipientUserId).Except(recipientIds).ToList();
                    if (otherRecipients.Count > 0)
                    {
                        await _watermarkRepo.UpsertWatermarksForRecipientsAsync(
                            intent.WatermarkScope,
                            intent.WatermarkId.Value,
                            otherRecipients,
                            intent.OrderVersion,
                            envelope.MessageId.ToString(),
                            isTerminal: true,
                            terminalState: intent.TerminalState,
                            isPaid: false,
                            parentOrderId: intent.OrderId,
                            parentItemId: intent.RentalItemId,
                            cancellationToken: cancellationToken);
                    }
                }
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Lỗi vi phạm constraint database (có thể là duplicate concurrent): MessageId={MessageId}", messageId);
            // Verify if inbox receipt already exists
            var isDuplicate = await _inboxRepo.ExistsAsync(_consumerGroupName, messageId, cancellationToken);
            if (isDuplicate)
            {
                return IngestionOutcome.Duplicate;
            }
            throw; // Re-throw if unexpected constraint violation
        }

        // 8. Post-commit Realtime Dispatch
        foreach (var notification in notificationsToCreate)
        {
            try
            {
                await _realtimeNotifier.NotifyInboxChangedAsync(
                    notification.RecipientUserId,
                    notification.Id,
                    "CREATED",
                    notification.Category,
                    notification.OccurredAt,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Realtime notification dispatch thất bại cho UserId={UserId}, NotificationId={Id}. Không ảnh hưởng tới kết quả commit.", notification.RecipientUserId, notification.Id);
            }
        }

        foreach (var suppressed in suppressedCtaNotifications)
        {
            try
            {
                await _realtimeNotifier.NotifyInboxChangedAsync(
                    suppressed.RecipientUserId,
                    suppressed.Id,
                    "UPDATED",
                    suppressed.Category,
                    suppressed.OccurredAt,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Realtime UPDATED dispatch thất bại cho UserId={UserId}, NotificationId={Id}.", suppressed.RecipientUserId, suppressed.Id);
            }
        }
        NotificationMetrics.IngestionCounter.Add(1, new KeyValuePair<string, object?>("status", "processed"));
        return IngestionOutcome.Processed;
    }
}
