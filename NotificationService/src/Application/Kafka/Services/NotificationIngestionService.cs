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
using Microsoft.Extensions.Logging;

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
    private const string ConsumerGroupName = "notification-service-v1";
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
        ILogger<NotificationIngestionService> logger)
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

        // 3. Check Unknown Event Type from allowed Producer
        var handler = _handlers.FirstOrDefault(h => string.Equals(h.EventType, envelope.MessageType, StringComparison.OrdinalIgnoreCase));
        if (handler == null)
        {
            _logger.LogInformation(
                "Bỏ qua sự kiện không thuộc danh sách quan tâm: MessageType={MessageType}, MessageId={MessageId}, Producer={Producer}",
                envelope.MessageType,
                envelope.MessageId,
                envelope.Producer);

            return IngestionOutcome.Ignored;
        }

        // 4. Idempotency Pre-Check
        var exists = await _inboxRepo.ExistsAsync(ConsumerGroupName, messageId, cancellationToken);
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
        var recipientsResult = _recipientResolver.ResolveRecipients(
            intent.EventType,
            intent.AggregateId,
            intent.RenterId,
            intent.SupplierId,
            intent.ReturnRequestId);

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

        // 7. Atomic Transaction: Read Watermarks, Evaluate Suppression, Render & Persist
        try
        {
            await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

            // Double check idempotency inside transaction
            var alreadyProcessed = await _inboxRepo.ExistsAsync(ConsumerGroupName, messageId, cancellationToken);
            if (alreadyProcessed)
            {
                _logger.LogInformation("Bỏ qua tin nhắn trùng lặp (inside transaction): MessageId={MessageId}", messageId);
                return IngestionOutcome.Duplicate;
            }

            var isFinancialEvent = string.Equals(intent.EventType, "RefundCompletedFinancial", StringComparison.OrdinalIgnoreCase)
                || string.Equals(intent.EventType, "SupplierPayoutCompleted", StringComparison.OrdinalIgnoreCase);

            var scope = ResolveScope(intent.EventType);

            // Advisory lock BEFORE reading watermarks (decoupled from financial events)
            if (!isFinancialEvent)
            {
                await _watermarkRepo.AcquireAdvisoryLockAsync(intent.AggregateId, cancellationToken);
            }

            foreach (var recipient in recipients)
            {
                NotificationAggregateWatermark? watermark = null;
                NotificationAggregateWatermark? parentOrderWatermark = null;

                if (!isFinancialEvent)
                {
                    watermark = await _watermarkRepo.GetWatermarkAsync(intent.AggregateId, recipient.RecipientUserId, scope, cancellationToken);

                    // Nếu recipient mới xuất hiện nhưng aggregate đã có watermark từ trước trong cùng scope
                    if (watermark == null)
                    {
                        var knownWatermarks = await _watermarkRepo.GetWatermarksForAggregateAsync(intent.AggregateId, scope, cancellationToken);
                        var terminalWatermark = knownWatermarks.FirstOrDefault(w => w.IsTerminal);
                        var paidWatermark = knownWatermarks.FirstOrDefault(w => w.IsPaid);
                        if (terminalWatermark != null || paidWatermark != null)
                        {
                            watermark = new NotificationAggregateWatermark
                            {
                                Scope = scope,
                                AggregateId = intent.AggregateId,
                                RecipientUserId = recipient.RecipientUserId,
                                LastOrderVersion = terminalWatermark?.LastOrderVersion ?? paidWatermark?.LastOrderVersion ?? 0,
                                IsTerminal = terminalWatermark != null,
                                TerminalState = terminalWatermark?.TerminalState,
                                IsPaid = paidWatermark != null
                            };
                        }
                    }

                    // Conflicting state check at same version:
                    // Two different messageIds with same version are processed; conflicting states at same version return validation error.
                    if (watermark != null && watermark.LastOrderVersion == intent.OrderVersion)
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

                    // Terminal order suppresses pending actions of items under it
                    if (scope != NotificationWatermarkScope.RentalOrder)
                    {
                        parentOrderWatermark = await _watermarkRepo.GetWatermarkAsync(intent.AggregateId, recipient.RecipientUserId, NotificationWatermarkScope.RentalOrder, cancellationToken);
                    }
                }

                StaleEvaluationResult staleEval;
                if (parentOrderWatermark?.IsTerminal == true)
                {
                    staleEval = new StaleEvaluationResult(
                        IsStale: true,
                        SuppressAction: true,
                        SuppressEmail: true,
                        Reason: $"Đơn thuê đã ở trạng thái kết thúc ({parentOrderWatermark.TerminalState}).");
                }
                else
                {
                    staleEval = StaleActionSuppressionPolicy.Evaluate(recipient.NotificationType, intent.OrderVersion, watermark);
                }
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
                    NotificationType = recipient.NotificationType,
                    Category = recipient.Category,
                    Severity = recipient.Severity,
                    Title = rendered.Title,
                    Body = rendered.Body,
                    ResourceType = "RentalOrder",
                    ResourceId = intent.AggregateId.ToString(),
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
                var shouldSendEmail = NotificationChannelPolicy.ShouldSendEmail(recipient.NotificationType, recipient.RecipientRole);
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
                ConsumerName = ConsumerGroupName,
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

            // Terminal order suppresses pending actions of items under it
            if (intent.IsTerminal)
            {
                await _notificationRepo.SuppressPendingActionsForResourceAsync("RentalOrder", intent.AggregateId.ToString(), cancellationToken);
            }

            // Update watermarks for all resolved recipients (decoupled from financial events)
            if (!isFinancialEvent)
            {
                var isPaidEvent = string.Equals(intent.EventType, "RentalOrderPaid", StringComparison.OrdinalIgnoreCase);
                var recipientIds = recipients.Select(r => r.RecipientUserId).Distinct().ToList();
                await _watermarkRepo.UpsertWatermarksForRecipientsAsync(
                    intent.AggregateId,
                    recipientIds,
                    intent.OrderVersion,
                    envelope.MessageId.ToString(),
                    intent.IsTerminal,
                    intent.TerminalState,
                    scope,
                    isPaid: isPaidEvent,
                    cancellationToken);

                // If terminal event, advance watermark terminal state for any previously known recipients of this aggregate
                if (intent.IsTerminal)
                {
                    var allKnown = await _watermarkRepo.GetWatermarksForAggregateAsync(intent.AggregateId, scope, cancellationToken);
                    var otherRecipients = allKnown.Select(w => w.RecipientUserId).Except(recipientIds).ToList();
                    if (otherRecipients.Count > 0)
                    {
                        await _watermarkRepo.UpsertWatermarksForRecipientsAsync(
                            intent.AggregateId,
                            otherRecipients,
                            intent.OrderVersion,
                            envelope.MessageId.ToString(),
                            isTerminal: true,
                            intent.TerminalState,
                            scope,
                            isPaid: false,
                            cancellationToken);
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
            var isDuplicate = await _inboxRepo.ExistsAsync(ConsumerGroupName, messageId, cancellationToken);
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

        return IngestionOutcome.Processed;
    }

    private static string ResolveScope(string eventType)
    {
        return eventType switch
        {
            "RentalItemHandoverCompleted" or "RentalHandoverOverdue" or "RentalItemConfirmed" or "RentalItemRejected"
                => NotificationWatermarkScope.RentalItem,
            "ReturnConfirmationOverdue"
                => NotificationWatermarkScope.ReturnRequest,
            _ => NotificationWatermarkScope.RentalOrder
        };
    }
}
