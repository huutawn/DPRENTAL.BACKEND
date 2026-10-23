using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Json.Schema;
using Xunit;

namespace Dprental.InventoryService.Api.Tests;

internal static class RecommendationContractResources
{
    private static readonly Lazy<IReadOnlyDictionary<string, JsonSchema>> Schemas = new(() =>
        new Dictionary<string, JsonSchema>
        {
            ["listing-viewed.schema.json"] = JsonSchema.FromText(Read("listing-viewed.schema.json")),
            ["search-performed.schema.json"] = JsonSchema.FromText(Read("search-performed.schema.json"))
        });

    public static JsonSchema LoadSchema(string name) => Schemas.Value[name];

    private static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RecommendationContracts." + name)
            ?? throw new InvalidOperationException("Missing embedded contract: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static EvaluationResults Evaluate(string name, JsonElement payload) =>
        LoadSchema(name).Evaluate(payload, new EvaluationOptions { RequireFormatValidation = true });

    public static void VerifyProvenance()
    {
        using var manifest = JsonDocument.Parse(Read("manifest.json"));
        Assert.Equal("RecommendationService", manifest.RootElement.GetProperty("owner").GetString());
        Assert.Equal(40, manifest.RootElement.GetProperty("revision").GetString()!.Length);
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RecommendationContracts." + file.Name)!;
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            Assert.Equal(file.Value.GetProperty("sha256").GetString(), hash);
        }
    }
}

public sealed class RecommendationContractResourceTests
{
    [Fact]
    public void EmbeddedSchemasMatchPinnedCanonicalChecksums() => RecommendationContractResources.VerifyProvenance();

    [Theory]
    [InlineData("2026-10-23T10:00:00+00:00")]
    [InlineData("not-a-timestamp")]
    public void InvalidTimestampIsRejected(string timestamp)
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            messageId = Guid.NewGuid(),
            messageType = "ListingViewed",
            version = 1,
            producer = "InventoryService",
            occurredAt = timestamp,
            payload = new { userId = (Guid?)null, equipmentListingId = Guid.NewGuid(), equipmentTypeId = Guid.NewGuid() }
        });
        Assert.False(RecommendationContractResources.Evaluate("listing-viewed.schema.json", json).IsValid);
    }
}
