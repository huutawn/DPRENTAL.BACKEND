using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Allocations;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Calendar;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.DTOs.Warehouse;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Transport;
using Dprental.InventoryService.Common.Enums;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Contracts;

public sealed class DtoAnnotationSimplificationContractTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void UpdateWarehouseRequest_JsonRequired_And_CamelCase_Preserved()
    {
        // 1. Valid payload deserializes and serializes
        string validJson = """
        {
            "addressDetail": "123 Main St",
            "provinceCode": "79",
            "wardCode": "001",
            "latitude": 10.7769,
            "longitude": 106.7009,
            "expectedVersion": 1
        }
        """;
        var req = JsonSerializer.Deserialize<UpdateWarehouseRequest>(validJson, WebOptions);
        Assert.NotNull(req);
        Assert.Equal("123 Main St", req.AddressDetail);
        Assert.Equal(10.7769, req.Latitude);
        Assert.Equal(106.7009, req.Longitude);
        Assert.Equal(1u, req.ExpectedVersion);

        // 2. Missing required field throws JsonException
        string missingLatJson = """
        {
            "addressDetail": "123 Main St",
            "provinceCode": "79",
            "wardCode": "001",
            "longitude": 106.7009
        }
        """;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpdateWarehouseRequest>(missingLatJson, WebOptions));
    }

    [Fact]
    public void RentalRateDto_StrictConverter_And_CamelCase_Preserved()
    {
        string json = """
        {
            "dailyRate": 500000,
            "weeklyRate": 3000000,
            "currency": "VND"
        }
        """;
        var dto = JsonSerializer.Deserialize<RentalRateDto>(json, WebOptions);
        Assert.NotNull(dto);
        Assert.Equal(500000m, dto.DailyRate);
        Assert.Equal(3000000m, dto.WeeklyRate);
        Assert.Equal("VND", dto.Currency);

        string serialized = JsonSerializer.Serialize(dto, WebOptions);
        Assert.Contains("\"dailyRate\":500000", serialized);
        Assert.Contains("\"weeklyRate\":3000000", serialized);
        Assert.Contains("\"currency\":\"VND\"", serialized);

        // Decimal rate rejected by strict integer converter
        string fractionalJson = """
        {
            "dailyRate": 500.5,
            "currency": "VND"
        }
        """;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RentalRateDto>(fractionalJson, WebOptions));
    }

    [Fact]
    public void CreateEquipmentListingRequest_StrictDeposit_And_CamelCase_Preserved()
    {
        string json = """
        {
            "equipmentTypeId": "11111111-1111-1111-1111-111111111111",
            "brandId": "22222222-2222-2222-2222-222222222222",
            "name": "Listing Name",
            "model": "Model X",
            "serialNumber": "SN-12345",
            "specValues": {},
            "rentalRate": {
                "dailyRate": 500000,
                "currency": "VND"
            },
            "depositAmount": 2000000
        }
        """;
        var req = JsonSerializer.Deserialize<CreateEquipmentListingRequest>(json, WebOptions);
        Assert.NotNull(req);
        Assert.Equal("Listing Name", req.Name);
        Assert.Equal(2000000m, req.DepositAmount);

        // Fractional deposit rejected
        string fractionalDepositJson = json.Replace("2000000", "2000000.5");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateEquipmentListingRequest>(fractionalDepositJson, WebOptions));
    }

    [Fact]
    public void CreateCalendarBlockRequest_StrictEnum_And_CamelCase_Preserved()
    {
        string json = """
        {
            "startDate": "2026-11-01",
            "endDate": "2026-11-05",
            "reason": "Maintenance",
            "note": "Routine check"
        }
        """;
        var req = JsonSerializer.Deserialize<CreateCalendarBlockRequest>(json, WebOptions);
        Assert.NotNull(req);
        Assert.Equal(CalendarBlockReason.Maintenance, req.Reason);
        Assert.Equal("Routine check", req.Note);

        string invalidEnumJson = """
        {
            "startDate": "2026-11-01",
            "endDate": "2026-11-05",
            "reason": "InvalidReason"
        }
        """;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateCalendarBlockRequest>(invalidEnumJson, WebOptions));
    }

    [Fact]
    public void MessageEnvelope_KafkaSerialization_PreservesWireContract()
    {
        var payload = new SearchProjectionChangedPayload(
            "EquipmentListing",
            Guid.NewGuid(),
            "Upsert",
            42,
            DateTimeOffset.UtcNow,
            false,
            null
        );

        var envelope = new MessageEnvelope<SearchProjectionChangedPayload>
        {
            MessageId = Guid.NewGuid(),
            MessageType = "SearchProjectionChanged",
            Version = 1,
            CorrelationId = "corr-test",
            AggregateId = "agg-123",
            OccurredAt = DateTimeOffset.UtcNow,
            Producer = "InventoryService",
            Payload = payload
        };

        string wireJson = KafkaEnvelopeSerializer.Serialize(envelope);
        using var doc = JsonDocument.Parse(wireJson);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("messageId", out _));
        Assert.True(root.TryGetProperty("messageType", out _));
        Assert.True(root.TryGetProperty("version", out _));
        Assert.True(root.TryGetProperty("correlationId", out _));
        Assert.True(root.TryGetProperty("aggregateId", out _));
        Assert.True(root.TryGetProperty("occurredAt", out _));
        Assert.True(root.TryGetProperty("producer", out _));
        Assert.True(root.TryGetProperty("payload", out var payloadElem));

        Assert.True(payloadElem.TryGetProperty("entityType", out _));
        Assert.True(payloadElem.TryGetProperty("entityId", out _));
        Assert.True(payloadElem.TryGetProperty("action", out _));
        Assert.True(payloadElem.TryGetProperty("revision", out _));
    }

    [Fact]
    public void Responses_SerializeToCamelCase_MatchingWireContract()
    {
        var alloc = new AllocationResponse(
            Guid.NewGuid(), Guid.NewGuid(), "rental", "ref-1",
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5),
            "Held", DateTimeOffset.UtcNow.AddHours(2), "notes",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var paged = new PagedResponse<AllocationResponse>([alloc], 1, 10, 1, 1);
        string json = JsonSerializer.Serialize(paged, WebOptions);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("items", out var items));
        Assert.True(root.TryGetProperty("page", out _));
        Assert.True(root.TryGetProperty("pageSize", out _));
        Assert.True(root.TryGetProperty("totalCount", out _));
        Assert.True(root.TryGetProperty("totalPages", out _));

        var item = items[0];
        Assert.True(item.TryGetProperty("assetId", out _));
        Assert.True(item.TryGetProperty("sourceSystem", out _));
        Assert.True(item.TryGetProperty("holdExpiresAt", out _));
    }
}
