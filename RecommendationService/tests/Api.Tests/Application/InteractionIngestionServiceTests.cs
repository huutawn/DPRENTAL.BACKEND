using Dprental.RecommendationService.Application.DTOs;
using Dprental.RecommendationService.Application.Services;
using Dprental.RecommendationService.Application.Validation;
using Dprental.RecommendationService.Domain.Entities;
using Dprental.RecommendationService.Domain.IRepositories;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests.Application;

public sealed class InteractionIngestionServiceTests
{
    private readonly Mock<IInteractionEventRepository> _eventRepoMock = new();
    private readonly Mock<IUserEquipmentTypeInterestRepository> _userInterestRepoMock = new();
    private readonly Mock<IListingPopularityRepository> _listingPopularityRepoMock = new();
    private readonly Mock<IInteractionUnitOfWork> _unitOfWorkMock = new();
    private readonly EventSchemaValidator _validator = new();

    private readonly InteractionIngestionService _service;

    public InteractionIngestionServiceTests()
    {
        _unitOfWorkMock
            .Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task<EventProcessResult>>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task<EventProcessResult>>, CancellationToken>((func, ct) => func(ct));

        _service = new InteractionIngestionService(
            _validator,
            _eventRepoMock.Object,
            _userInterestRepoMock.Object,
            _listingPopularityRepoMock.Object,
            _unitOfWorkMock.Object,
            NullLogger<InteractionIngestionService>.Instance);
    }

    [Fact]
    public async Task ProcessEvent_ListingViewed_AppliesWeight2ToUserAndPopularity()
    {
        var msgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        string json = $$"""
        {
            "messageId": "{{msgId}}",
            "messageType": "ListingViewed",
            "version": 1,
            "producer": "InventoryService",
            "occurredAt": "2026-10-19T10:00:00Z",
            "payload": {
                "userId": "{{userId}}",
                "equipmentListingId": "{{listingId}}",
                "equipmentTypeId": "{{typeId}}"
            }
        }
        """;

        _eventRepoMock
            .Setup(r => r.IngestAsync(It.IsAny<InteractionEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractionIngestResult(InteractionIngestStatus.Inserted));

        var result = await _service.ProcessEventAsync(json, InteractionEventSource.Inventory, CancellationToken.None);

        Assert.Equal(EventProcessOutcome.Processed, result.Outcome);

        _eventRepoMock.Verify(r => r.IngestAsync(
            It.Is<InteractionEvent>(e => e.ScoreWeight == 2 && e.MessageId == msgId),
            It.IsAny<CancellationToken>()), Times.Once);

        _userInterestRepoMock.Verify(r => r.UpsertAsync(userId, typeId, 2, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _listingPopularityRepoMock.Verify(r => r.UpsertAsync(listingId, 2, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEvent_SearchPerformed_WithType_AppliesWeight1ToUserOnly()
    {
        var msgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        string json = $$"""
        {
            "messageId": "{{msgId}}",
            "messageType": "SearchPerformed",
            "version": 1,
            "producer": "InventoryService",
            "occurredAt": "2026-10-19T10:00:00Z",
            "payload": {
                "userId": "{{userId}}",
                "equipmentTypeId": "{{typeId}}"
            }
        }
        """;

        _eventRepoMock
            .Setup(r => r.IngestAsync(It.IsAny<InteractionEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractionIngestResult(InteractionIngestStatus.Inserted));

        var result = await _service.ProcessEventAsync(json, InteractionEventSource.Inventory, CancellationToken.None);

        Assert.Equal(EventProcessOutcome.Processed, result.Outcome);

        _eventRepoMock.Verify(r => r.IngestAsync(
            It.Is<InteractionEvent>(e => e.ScoreWeight == 1),
            It.IsAny<CancellationToken>()), Times.Once);

        _userInterestRepoMock.Verify(r => r.UpsertAsync(userId, typeId, 1, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _listingPopularityRepoMock.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessEvent_SearchPerformed_WithoutType_DoesNotUpdateAnyProjection()
    {
        var msgId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        string json = $$"""
        {
            "messageId": "{{msgId}}",
            "messageType": "SearchPerformed",
            "version": 1,
            "producer": "InventoryService",
            "occurredAt": "2026-10-19T10:00:00Z",
            "payload": {
                "userId": "{{userId}}",
                "equipmentTypeId": null
            }
        }
        """;

        _eventRepoMock
            .Setup(r => r.IngestAsync(It.IsAny<InteractionEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractionIngestResult(InteractionIngestStatus.Inserted));

        var result = await _service.ProcessEventAsync(json, InteractionEventSource.Inventory, CancellationToken.None);

        Assert.Equal(EventProcessOutcome.Processed, result.Outcome);

        _userInterestRepoMock.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _listingPopularityRepoMock.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessEvent_AnonymousView_UpdatesPopularity_SkipsUserInterest()
    {
        var msgId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        string json = $$"""
        {
            "messageId": "{{msgId}}",
            "messageType": "ListingViewed",
            "version": 1,
            "producer": "InventoryService",
            "occurredAt": "2026-10-19T10:00:00Z",
            "payload": {
                "userId": null,
                "equipmentListingId": "{{listingId}}",
                "equipmentTypeId": "{{typeId}}"
            }
        }
        """;

        _eventRepoMock
            .Setup(r => r.IngestAsync(It.IsAny<InteractionEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractionIngestResult(InteractionIngestStatus.Inserted));

        var result = await _service.ProcessEventAsync(json, InteractionEventSource.Inventory, CancellationToken.None);

        Assert.Equal(EventProcessOutcome.Processed, result.Outcome);

        _userInterestRepoMock.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _listingPopularityRepoMock.Verify(r => r.UpsertAsync(listingId, 2, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEvent_CartItemAdded_AppliesWeight5()
    {
        var msgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        string json = $$"""
        {
            "messageId": "{{msgId}}",
            "messageType": "CartItemAdded",
            "version": 1,
            "producer": "RentalService",
            "occurredAt": "2026-10-19T10:00:00Z",
            "payload": {
                "userId": "{{userId}}",
                "equipmentListingId": "{{listingId}}",
                "equipmentTypeId": "{{typeId}}",
                "cartId": "{{Guid.NewGuid()}}",
                "cartItemId": "{{Guid.NewGuid()}}"
            }
        }
        """;

        _eventRepoMock
            .Setup(r => r.IngestAsync(It.IsAny<InteractionEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractionIngestResult(InteractionIngestStatus.Inserted));

        var result = await _service.ProcessEventAsync(json, InteractionEventSource.Rental, CancellationToken.None);

        Assert.Equal(EventProcessOutcome.Processed, result.Outcome);

        _userInterestRepoMock.Verify(r => r.UpsertAsync(userId, typeId, 5, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _listingPopularityRepoMock.Verify(r => r.UpsertAsync(listingId, 5, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEvent_RentalItemPaid_AppliesWeight8()
    {
        var msgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        string json = $$"""
        {
            "messageId": "{{msgId}}",
            "messageType": "RentalItemPaid",
            "version": 1,
            "producer": "RentalService",
            "occurredAt": "2026-10-19T10:00:00Z",
            "payload": {
                "userId": "{{userId}}",
                "equipmentListingId": "{{listingId}}",
                "equipmentTypeId": "{{typeId}}",
                "rentalOrderId": "{{Guid.NewGuid()}}",
                "rentalItemId": "{{Guid.NewGuid()}}"
            }
        }
        """;

        _eventRepoMock
            .Setup(r => r.IngestAsync(It.IsAny<InteractionEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractionIngestResult(InteractionIngestStatus.Inserted));

        var result = await _service.ProcessEventAsync(json, InteractionEventSource.Rental, CancellationToken.None);

        Assert.Equal(EventProcessOutcome.Processed, result.Outcome);

        _userInterestRepoMock.Verify(r => r.UpsertAsync(userId, typeId, 8, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _listingPopularityRepoMock.Verify(r => r.UpsertAsync(listingId, 8, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEvent_RentalItemCompleted_AppliesWeight10()
    {
        var msgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        string json = $$"""
        {
            "messageId": "{{msgId}}",
            "messageType": "RentalItemCompleted",
            "version": 1,
            "producer": "RentalService",
            "occurredAt": "2026-10-19T10:00:00Z",
            "payload": {
                "userId": "{{userId}}",
                "equipmentListingId": "{{listingId}}",
                "equipmentTypeId": "{{typeId}}",
                "rentalOrderId": "{{Guid.NewGuid()}}",
                "rentalItemId": "{{Guid.NewGuid()}}"
            }
        }
        """;

        _eventRepoMock
            .Setup(r => r.IngestAsync(It.IsAny<InteractionEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractionIngestResult(InteractionIngestStatus.Inserted));

        var result = await _service.ProcessEventAsync(json, InteractionEventSource.Rental, CancellationToken.None);

        Assert.Equal(EventProcessOutcome.Processed, result.Outcome);

        _userInterestRepoMock.Verify(r => r.UpsertAsync(userId, typeId, 10, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _listingPopularityRepoMock.Verify(r => r.UpsertAsync(listingId, 10, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEvent_DuplicateIgnored_DoesNotUpdateProjections()
    {
        var msgId = Guid.NewGuid();
        string json = $$"""
        {
            "messageId": "{{msgId}}",
            "messageType": "ListingViewed",
            "version": 1,
            "producer": "InventoryService",
            "occurredAt": "2026-10-19T10:00:00Z",
            "payload": {
                "userId": "{{Guid.NewGuid()}}",
                "equipmentListingId": "{{Guid.NewGuid()}}",
                "equipmentTypeId": "{{Guid.NewGuid()}}"
            }
        }
        """;

        _eventRepoMock
            .Setup(r => r.IngestAsync(It.IsAny<InteractionEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractionIngestResult(InteractionIngestStatus.DuplicateIgnored));

        var result = await _service.ProcessEventAsync(json, InteractionEventSource.Inventory, CancellationToken.None);

        Assert.Equal(EventProcessOutcome.DuplicateIgnored, result.Outcome);

        _userInterestRepoMock.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _listingPopularityRepoMock.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessEvent_ConflictMismatch_DoesNotUpdateProjections()
    {
        var msgId = Guid.NewGuid();
        string json = $$"""
        {
            "messageId": "{{msgId}}",
            "messageType": "ListingViewed",
            "version": 1,
            "producer": "InventoryService",
            "occurredAt": "2026-10-19T10:00:00Z",
            "payload": {
                "userId": "{{Guid.NewGuid()}}",
                "equipmentListingId": "{{Guid.NewGuid()}}",
                "equipmentTypeId": "{{Guid.NewGuid()}}"
            }
        }
        """;

        _eventRepoMock
            .Setup(r => r.IngestAsync(It.IsAny<InteractionEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractionIngestResult(InteractionIngestStatus.ConflictMismatch, "Different payload"));

        var result = await _service.ProcessEventAsync(json, InteractionEventSource.Inventory, CancellationToken.None);

        Assert.Equal(EventProcessOutcome.ConflictMismatch, result.Outcome);
        Assert.NotNull(result.ErrorReason);

        _userInterestRepoMock.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _listingPopularityRepoMock.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessEvent_OutOfScopeRentalEvent_ReturnsSkippedUnrelated()
    {
        string json = """
        {
            "messageId": "11111111-1111-4111-8111-111111111111",
            "messageType": "RentalOrderPlaced",
            "version": 1,
            "producer": "RentalService",
            "occurredAt": "2026-10-19T10:00:00Z",
            "payload": {
                "orderId": "22222222-2222-4222-8222-222222222222"
            }
        }
        """;

        var result = await _service.ProcessEventAsync(json, InteractionEventSource.Rental, CancellationToken.None);

        Assert.Equal(EventProcessOutcome.SkippedUnrelated, result.Outcome);
        _eventRepoMock.Verify(r => r.IngestAsync(It.IsAny<InteractionEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessEvent_SchemaValidationError_ReturnsSchemaValidationError()
    {
        string brokenJson = "{ invalid: true }";

        var result = await _service.ProcessEventAsync(brokenJson, InteractionEventSource.Inventory, CancellationToken.None);

        Assert.Equal(EventProcessOutcome.SchemaValidationError, result.Outcome);
        Assert.NotNull(result.ErrorReason);
        _eventRepoMock.Verify(r => r.IngestAsync(It.IsAny<InteractionEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessEvent_SearchPerformed_WithExpandedAnalyticsAndType_AppliesWeight1ToUserOnly_AndPreservesRawEnvelope()
    {
        var msgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        string json = $$"""
        {
            "messageId": "{{msgId}}",
            "messageType": "SearchPerformed",
            "version": 1,
            "producer": "InventoryService",
            "occurredAt": "2026-10-19T10:01:00.0000000Z",
            "correlationId": "corr-expanded-001",
            "aggregateId": "{{msgId}}",
            "payload": {
                "userId": "{{userId}}",
                "equipmentTypeId": "{{typeId}}",
                "query": "máy khoan bê tông",
                "categoryId": "{{categoryId}}",
                "filterContext": {
                    "brandId": "{{brandId}}",
                    "provinceCode": "79",
                    "wardCode": "790101",
                    "sort": "price_asc",
                    "filters": "{\"price\": {\"gte\": 1000000 } }",
                    "from": "2026-11-01",
                    "to": "2026-11-10",
                    "requestedQuantity": 2,
                    "page": 1,
                    "pageSize": 20
                },
                "resultCount": 15
            }
        }
        """;

        _eventRepoMock
            .Setup(r => r.IngestAsync(It.IsAny<InteractionEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractionIngestResult(InteractionIngestStatus.Inserted));

        var result = await _service.ProcessEventAsync(json, InteractionEventSource.Inventory, CancellationToken.None);

        Assert.Equal(EventProcessOutcome.Processed, result.Outcome);

        _eventRepoMock.Verify(r => r.IngestAsync(
            It.Is<InteractionEvent>(e =>
                e.ScoreWeight == 1 &&
                e.MessageId == msgId &&
                e.UserId == userId &&
                e.EquipmentTypeId == typeId &&
                e.EquipmentListingId == null &&
                e.Envelope.Contains("máy khoan bê tông") &&
                e.Envelope.Contains("790101") &&
                e.Envelope.Contains("\"resultCount\": 15")),
            It.IsAny<CancellationToken>()), Times.Once);

        _userInterestRepoMock.Verify(r => r.UpsertAsync(userId, typeId, 1, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _listingPopularityRepoMock.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessEvent_SearchPerformed_WithExpandedAnalyticsKeywordOnly_DoesNotUpdateAnyProjection()
    {
        var msgId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        string json = $$"""
        {
            "messageId": "{{msgId}}",
            "messageType": "SearchPerformed",
            "version": 1,
            "producer": "InventoryService",
            "occurredAt": "2026-10-19T10:01:00.0000000Z",
            "correlationId": "corr-expanded-002",
            "aggregateId": "{{msgId}}",
            "payload": {
                "userId": "{{userId}}",
                "equipmentTypeId": null,
                "query": "máy khoan",
                "categoryId": null,
                "filterContext": {
                    "brandId": null,
                    "provinceCode": null,
                    "wardCode": null,
                    "sort": null,
                    "filters": null,
                    "from": null,
                    "to": null,
                    "requestedQuantity": null,
                    "page": 1,
                    "pageSize": 20
                },
                "resultCount": 37
            }
        }
        """;

        _eventRepoMock
            .Setup(r => r.IngestAsync(It.IsAny<InteractionEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractionIngestResult(InteractionIngestStatus.Inserted));

        var result = await _service.ProcessEventAsync(json, InteractionEventSource.Inventory, CancellationToken.None);

        Assert.Equal(EventProcessOutcome.Processed, result.Outcome);

        _eventRepoMock.Verify(r => r.IngestAsync(
            It.Is<InteractionEvent>(e =>
                e.ScoreWeight == 1 &&
                e.MessageId == msgId &&
                e.UserId == userId &&
                e.EquipmentTypeId == null &&
                e.EquipmentListingId == null &&
                e.Envelope.Contains("máy khoan")),
            It.IsAny<CancellationToken>()), Times.Once);

        _userInterestRepoMock.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _listingPopularityRepoMock.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessEvent_SearchPerformed_ZeroResult_ProcessedSuccessfully()
    {
        var msgId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        string json = $$"""
        {
            "messageId": "{{msgId}}",
            "messageType": "SearchPerformed",
            "version": 1,
            "producer": "InventoryService",
            "occurredAt": "2026-10-19T10:04:00.0000000Z",
            "payload": {
                "userId": "{{userId}}",
                "equipmentTypeId": null,
                "query": "thiết bị không tồn tại",
                "categoryId": null,
                "filterContext": {
                    "brandId": null,
                    "provinceCode": null,
                    "wardCode": null,
                    "sort": null,
                    "filters": null,
                    "from": null,
                    "to": null,
                    "requestedQuantity": null,
                    "page": 1,
                    "pageSize": 20
                },
                "resultCount": 0
            }
        }
        """;

        _eventRepoMock
            .Setup(r => r.IngestAsync(It.IsAny<InteractionEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractionIngestResult(InteractionIngestStatus.Inserted));

        var result = await _service.ProcessEventAsync(json, InteractionEventSource.Inventory, CancellationToken.None);

        Assert.Equal(EventProcessOutcome.Processed, result.Outcome);
        _eventRepoMock.Verify(r => r.IngestAsync(
            It.Is<InteractionEvent>(e => e.ScoreWeight == 1 && e.MessageId == msgId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEvent_SearchPerformed_DuplicateExpandedEvent_IgnoredWithoutUpdatingScores()
    {
        var msgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        string json = $$"""
        {
            "messageId": "{{msgId}}",
            "messageType": "SearchPerformed",
            "version": 1,
            "producer": "InventoryService",
            "occurredAt": "2026-10-19T10:01:00.0000000Z",
            "payload": {
                "userId": "{{userId}}",
                "equipmentTypeId": "{{typeId}}",
                "query": "máy khoan bê tông",
                "categoryId": null,
                "filterContext": {
                    "brandId": null,
                    "provinceCode": null,
                    "wardCode": null,
                    "sort": null,
                    "filters": null,
                    "from": null,
                    "to": null,
                    "requestedQuantity": null,
                    "page": 1,
                    "pageSize": 20
                },
                "resultCount": 15
            }
        }
        """;

        _eventRepoMock
            .Setup(r => r.IngestAsync(It.IsAny<InteractionEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractionIngestResult(InteractionIngestStatus.DuplicateIgnored));

        var result = await _service.ProcessEventAsync(json, InteractionEventSource.Inventory, CancellationToken.None);

        Assert.Equal(EventProcessOutcome.DuplicateIgnored, result.Outcome);
        _userInterestRepoMock.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _listingPopularityRepoMock.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("CartItemAdded", "cartId", "cartItemId", "null")]
    [InlineData("CartItemAdded", "cartId", "cartItemId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("RentalItemPaid", "rentalOrderId", "rentalItemId", "null")]
    [InlineData("RentalItemPaid", "rentalOrderId", "rentalItemId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("RentalItemCompleted", "rentalOrderId", "rentalItemId", "null")]
    [InlineData("RentalItemCompleted", "rentalOrderId", "rentalItemId", "\"00000000-0000-0000-0000-000000000000\"")]
    public async Task ProcessEvent_RentalEvent_WithInvalidEquipmentTypeId_ReturnsSchemaValidationError_AndDoesNotIngest(
        string messageType, string idKey1, string idKey2, string typeValueJson)
    {
        var msgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        string json = $$"""
        {
            "messageId": "{{msgId}}",
            "messageType": "{{messageType}}",
            "version": 1,
            "producer": "RentalService",
            "occurredAt": "2026-10-19T10:00:00Z",
            "payload": {
                "userId": "{{userId}}",
                "equipmentListingId": "{{listingId}}",
                "equipmentTypeId": {{typeValueJson}},
                "{{idKey1}}": "{{Guid.NewGuid()}}",
                "{{idKey2}}": "{{Guid.NewGuid()}}"
            }
        }
        """;

        var result = await _service.ProcessEventAsync(json, InteractionEventSource.Rental, CancellationToken.None);

        Assert.Equal(EventProcessOutcome.SchemaValidationError, result.Outcome);
        Assert.NotNull(result.ErrorReason);

        _eventRepoMock.Verify(r => r.IngestAsync(It.IsAny<InteractionEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        _userInterestRepoMock.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _listingPopularityRepoMock.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("CartItemAdded", "cartId", "cartItemId")]
    [InlineData("RentalItemPaid", "rentalOrderId", "rentalItemId")]
    [InlineData("RentalItemCompleted", "rentalOrderId", "rentalItemId")]
    public async Task ProcessEvent_RentalEvent_MissingEquipmentTypeIdProperty_ReturnsSchemaValidationError_AndDoesNotIngest(
        string messageType, string idKey1, string idKey2)
    {
        var msgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        string json = $$"""
        {
            "messageId": "{{msgId}}",
            "messageType": "{{messageType}}",
            "version": 1,
            "producer": "RentalService",
            "occurredAt": "2026-10-19T10:00:00Z",
            "payload": {
                "userId": "{{userId}}",
                "equipmentListingId": "{{listingId}}",
                "{{idKey1}}": "{{Guid.NewGuid()}}",
                "{{idKey2}}": "{{Guid.NewGuid()}}"
            }
        }
        """;

        var result = await _service.ProcessEventAsync(json, InteractionEventSource.Rental, CancellationToken.None);

        Assert.Equal(EventProcessOutcome.SchemaValidationError, result.Outcome);
        Assert.NotNull(result.ErrorReason);

        _eventRepoMock.Verify(r => r.IngestAsync(It.IsAny<InteractionEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        _userInterestRepoMock.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _listingPopularityRepoMock.Verify(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
