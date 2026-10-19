using System.Text.Json;
using Dprental.RecommendationService.Application.Validation;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests.Application;

public sealed class EventSchemaValidatorTests
{
    private readonly EventSchemaValidator _validator = new();

    private static string FindFixturesDirectory(string subDir)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "docs", "contracts", "schemas", "fixtures", subDir);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"Fixtures directory '{subDir}' not found.");
    }

    [Fact]
    public void AllValidFixtures_PassValidation()
    {
        string validDir = FindFixturesDirectory("valid");
        string[] files = Directory.GetFiles(validDir, "*.json");

        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            string filename = Path.GetFileName(file);
            if (filename.Contains("cache", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string json = File.ReadAllText(file);
            using var doc = JsonDocument.Parse(json);
            string messageType = doc.RootElement.GetProperty("messageType").GetString()!;

            var source = messageType is "ListingViewed" or "SearchPerformed"
                ? InteractionEventSource.Inventory
                : InteractionEventSource.Rental;

            var result = _validator.Validate(json, source);
            Assert.Equal(EventValidationClassification.Valid, result.Classification);
            Assert.Null(result.ErrorReason);
        }
    }

    [Fact]
    public void AllInvalidFixtures_AreRejected()
    {
        string invalidDir = FindFixturesDirectory("invalid");
        string[] files = Directory.GetFiles(invalidDir, "*.json");

        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            string filename = Path.GetFileName(file);
            if (filename.Contains("cache", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string json = File.ReadAllText(file);

            var source = filename.Contains("cart", StringComparison.OrdinalIgnoreCase) ||
                         filename.Contains("rental", StringComparison.OrdinalIgnoreCase)
                ? InteractionEventSource.Rental
                : InteractionEventSource.Inventory;

            var result = _validator.Validate(json, source);
            Assert.Equal(EventValidationClassification.Invalid, result.Classification);
            Assert.NotNull(result.ErrorReason);
        }
    }

    [Fact]
    public void OutOfScopeRentalEvent_IsIdentifiedAsUnrelated()
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

        var result = _validator.Validate(json, InteractionEventSource.Rental);
        Assert.Equal(EventValidationClassification.Unrelated, result.Classification);
        Assert.Equal("RentalOrderPlaced", result.MessageType);
    }

    [Fact]
    public void CorruptedJson_OnRental_IsInvalid_NotUnrelated()
    {
        string brokenJson = "{ this is not json }";

        var result = _validator.Validate(brokenJson, InteractionEventSource.Rental);
        Assert.Equal(EventValidationClassification.Invalid, result.Classification);
    }

    [Fact]
    public void MissingOrEmptyMessageType_OnRental_IsInvalid_NotUnrelated()
    {
        string jsonMissing = """
        {
            "messageId": "11111111-1111-4111-8111-111111111111",
            "version": 1,
            "producer": "RentalService",
            "occurredAt": "2026-10-19T10:00:00Z",
            "payload": {}
        }
        """;

        var result1 = _validator.Validate(jsonMissing, InteractionEventSource.Rental);
        Assert.Equal(EventValidationClassification.Invalid, result1.Classification);

        string jsonEmpty = """
        {
            "messageId": "11111111-1111-4111-8111-111111111111",
            "messageType": "",
            "version": 1,
            "producer": "RentalService",
            "occurredAt": "2026-10-19T10:00:00Z",
            "payload": {}
        }
        """;

        var result2 = _validator.Validate(jsonEmpty, InteractionEventSource.Rental);
        Assert.Equal(EventValidationClassification.Invalid, result2.Classification);
    }

    [Fact]
    public void KnownInventoryEvent_EmittedOnRental_IsInvalid()
    {
        string json = """
        {
            "messageId": "11111111-1111-4111-8111-111111111111",
            "messageType": "ListingViewed",
            "version": 1,
            "producer": "InventoryService",
            "occurredAt": "2026-10-19T10:00:00Z",
            "payload": {
                "userId": "22222222-2222-4222-8222-222222222222",
                "equipmentListingId": "33333333-3333-4333-8333-333333333333",
                "equipmentTypeId": "44444444-4444-4444-8444-444444444444"
            }
        }
        """;

        var result = _validator.Validate(json, InteractionEventSource.Rental);
        Assert.Equal(EventValidationClassification.Invalid, result.Classification);
    }

    [Fact]
    public void KnownRentalEvent_EmittedOnInventory_IsInvalid()
    {
        string json = """
        {
            "messageId": "11111111-1111-4111-8111-111111111111",
            "messageType": "CartItemAdded",
            "version": 1,
            "producer": "RentalService",
            "occurredAt": "2026-10-19T10:00:00Z",
            "payload": {
                "userId": "22222222-2222-4222-8222-222222222222",
                "equipmentListingId": "33333333-3333-4333-8333-333333333333",
                "cartId": "55555555-5555-4555-8555-555555555555",
                "cartItemId": "66666666-6666-4666-8666-666666666666"
            }
        }
        """;

        var result = _validator.Validate(json, InteractionEventSource.Inventory);
        Assert.Equal(EventValidationClassification.Invalid, result.Classification);
    }
}
