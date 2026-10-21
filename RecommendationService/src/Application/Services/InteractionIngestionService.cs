using System.Text.Json;
using Dprental.RecommendationService.Application.DTOs;
using Dprental.RecommendationService.Application.IServices;
using Dprental.RecommendationService.Application.Validation;
using Dprental.RecommendationService.Domain.Entities;
using Dprental.RecommendationService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RecommendationService.Application.Services;

public sealed class InteractionIngestionService : IInteractionIngestionService
{
    private readonly IEventSchemaValidator _validator;
    private readonly IInteractionEventRepository _eventRepository;
    private readonly IUserEquipmentTypeInterestRepository _userInterestRepository;
    private readonly IListingPopularityRepository _listingPopularityRepository;
    private readonly IInteractionUnitOfWork _unitOfWork;
    private readonly ISnapshotPublishingService _publishingService;
    private readonly ILogger<InteractionIngestionService> _logger;

    public InteractionIngestionService(
        IEventSchemaValidator validator,
        IInteractionEventRepository eventRepository,
        IUserEquipmentTypeInterestRepository userInterestRepository,
        IListingPopularityRepository listingPopularityRepository,
        IInteractionUnitOfWork unitOfWork,
        ISnapshotPublishingService publishingService,
        ILogger<InteractionIngestionService> logger)
    {
        _validator = validator;
        _eventRepository = eventRepository;
        _userInterestRepository = userInterestRepository;
        _listingPopularityRepository = listingPopularityRepository;
        _unitOfWork = unitOfWork;
        _publishingService = publishingService;
        _logger = logger;
    }
    public async Task<EventProcessResult> ProcessEventAsync(string rawJson, InteractionEventSource source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = _validator.Validate(rawJson, source);
        if (validation.Classification == EventValidationClassification.Unrelated)
        {
            return new EventProcessResult(EventProcessOutcome.SkippedUnrelated, validation.ErrorReason);
        }

        if (validation.Classification == EventValidationClassification.Invalid)
        {
            return new EventProcessResult(EventProcessOutcome.SchemaValidationError, validation.ErrorReason);
        }

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        var messageId = Guid.Parse(root.GetProperty("messageId").GetString()!);
        var messageType = root.GetProperty("messageType").GetString()!;
        var version = root.GetProperty("version").GetInt32();
        var producer = root.GetProperty("producer").GetString()!;
        var occurredAt = DateTimeOffset.Parse(root.GetProperty("occurredAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        string? correlationId = null;
        if (root.TryGetProperty("correlationId", out var corrProp) && corrProp.ValueKind == JsonValueKind.String)
        {
            correlationId = corrProp.GetString();
        }

        var payload = root.GetProperty("payload");

        Guid? userId = null;
        if (payload.TryGetProperty("userId", out var userProp) && userProp.ValueKind == JsonValueKind.String)
        {
            if (Guid.TryParse(userProp.GetString(), out var parsedUser))
            {
                userId = parsedUser;
            }
        }

        Guid? equipmentListingId = null;
        if (payload.TryGetProperty("equipmentListingId", out var listingProp) && listingProp.ValueKind == JsonValueKind.String)
        {
            if (Guid.TryParse(listingProp.GetString(), out var parsedListing))
            {
                equipmentListingId = parsedListing;
            }
        }

        Guid? equipmentTypeId = null;
        if (payload.TryGetProperty("equipmentTypeId", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
        {
            if (Guid.TryParse(typeProp.GetString(), out var parsedType))
            {
                equipmentTypeId = parsedType;
            }
        }

        short scoreWeight = messageType switch
        {
            "SearchPerformed" => 1,
            "ListingViewed" => 2,
            "CartItemAdded" => 5,
            "RentalItemPaid" => 8,
            "RentalItemCompleted" => 10,
            _ => throw new InvalidOperationException($"Unexpected valid messageType '{messageType}'.")
        };

        var rawEvent = new InteractionEvent
        {
            MessageId = messageId,
            MessageType = messageType,
            Version = version,
            Producer = producer,
            OccurredAt = occurredAt,
            CorrelationId = correlationId,
            UserId = userId,
            EquipmentListingId = equipmentListingId,
            EquipmentTypeId = equipmentTypeId,
            ScoreWeight = scoreWeight,
            Envelope = rawJson
        };

        var result = await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var ingestResult = await _eventRepository.IngestAsync(rawEvent, ct);

            if (ingestResult.Status == InteractionIngestStatus.DuplicateIgnored)
            {
                _logger.LogInformation("Event {MessageId} ({MessageType}) is an idempotent duplicate. Ignored.", messageId, messageType);
                return new EventProcessResult(EventProcessOutcome.DuplicateIgnored);
            }

            if (ingestResult.Status == InteractionIngestStatus.ConflictMismatch)
            {
                _logger.LogWarning("Event {MessageId} ({MessageType}) has conflicting payload: {Reason}", messageId, messageType, ingestResult.ConflictReason);
                return new EventProcessResult(EventProcessOutcome.ConflictMismatch, ingestResult.ConflictReason);
            }

            // IngestStatus.Inserted -> Update projections
            if (userId.HasValue && equipmentTypeId.HasValue)
            {
                await _userInterestRepository.UpsertAsync(userId.Value, equipmentTypeId.Value, scoreWeight, occurredAt, ct);
            }

            if (equipmentListingId.HasValue)
            {
                await _listingPopularityRepository.UpsertAsync(equipmentListingId.Value, scoreWeight, occurredAt, ct);
            }

            return new EventProcessResult(EventProcessOutcome.Processed);
        }, cancellationToken);

        if (result.Outcome is not (EventProcessOutcome.Processed or EventProcessOutcome.DuplicateIgnored))
        {
            return result;
        }

        bool shouldPublishUser = userId.HasValue && equipmentTypeId.HasValue;
        bool shouldPublishGlobal = equipmentListingId.HasValue;
        bool isValkeyUnavailable = false;

        if (shouldPublishUser && userId.HasValue)
        {
            try
            {
                var userPublishResult = await _publishingService.PublishUserAsync(userId.Value, cancellationToken);
                if (userPublishResult.Outcome == PublishOutcome.Unavailable)
                {
                    isValkeyUnavailable = true;
                }
                else if (userPublishResult.Outcome is PublishOutcome.Failed)
                {
                    _logger.LogWarning("Post-commit user cache publish failed for user {UserId}: {Reason}", userId.Value, userPublishResult.Reason);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception during post-commit user cache publish for user {UserId}. Ingestion remains successful.", userId.Value);
            }
        }

        if (shouldPublishGlobal && !isValkeyUnavailable)
        {
            try
            {
                var globalPublishResult = await _publishingService.PublishGlobalAsync(cancellationToken);
                if (globalPublishResult.Outcome is PublishOutcome.Failed or PublishOutcome.Unavailable)
                {
                    _logger.LogWarning("Post-commit global cache publish failed: {Reason}", globalPublishResult.Reason);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception during post-commit global cache publish. Ingestion remains successful.");
            }
        }

        return result;
    }
}
