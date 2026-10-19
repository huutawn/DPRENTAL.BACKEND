using System.Text.Json;
using Json.Schema;

namespace Dprental.RecommendationService.Application.Validation;

public sealed partial class EventSchemaValidator : IEventSchemaValidator
{
    private static readonly HashSet<string> FivePhase1Types = new(StringComparer.Ordinal)
    {
        "ListingViewed",
        "SearchPerformed",
        "CartItemAdded",
        "RentalItemPaid",
        "RentalItemCompleted"
    };

    private static readonly HashSet<string> InventoryAllowedTypes = new(StringComparer.Ordinal)
    {
        "ListingViewed",
        "SearchPerformed"
    };

    private static readonly HashSet<string> RentalAllowedTypes = new(StringComparer.Ordinal)
    {
        "CartItemAdded",
        "RentalItemPaid",
        "RentalItemCompleted"
    };

    private static readonly Dictionary<string, JsonSchema> Schemas = LoadSchemas();
    private readonly EvaluationOptions _evaluationOptions;

    public EventSchemaValidator()
    {
        _evaluationOptions = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true
        };
    }

    public SchemaValidationResult Validate(string rawJson, InteractionEventSource source)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new SchemaValidationResult(EventValidationClassification.Invalid, null, "Message payload is empty or whitespace.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            return new SchemaValidationResult(EventValidationClassification.Invalid, null, $"JSON parse error: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new SchemaValidationResult(EventValidationClassification.Invalid, null, "Root JSON must be an object envelope.");
            }

            if (!root.TryGetProperty("messageType", out var msgTypeProp) || msgTypeProp.ValueKind != JsonValueKind.String)
            {
                return new SchemaValidationResult(EventValidationClassification.Invalid, null, "Envelope missing valid 'messageType' property.");
            }

            string? messageType = msgTypeProp.GetString();
            if (string.IsNullOrWhiteSpace(messageType))
            {
                return new SchemaValidationResult(EventValidationClassification.Invalid, null, "Envelope 'messageType' is empty.");
            }

            if (source == InteractionEventSource.Rental)
            {
                // If messageType is outside the five Phase 1 types on Rental source -> Unrelated (skip without DLQ)
                if (!FivePhase1Types.Contains(messageType))
                {
                    return new SchemaValidationResult(
                        EventValidationClassification.Unrelated,
                        messageType,
                        $"Event type '{messageType}' is outside Phase 1 recommendation scope on Rental source.");
                }

                // If messageType is one of the five, but not allowed on Rental (e.g. ListingViewed, SearchPerformed) -> Invalid
                if (!RentalAllowedTypes.Contains(messageType))
                {
                    return new SchemaValidationResult(
                        EventValidationClassification.Invalid,
                        messageType,
                        $"Event type '{messageType}' is an Inventory event emitted on Rental source.");
                }
            }
            else // source == InteractionEventSource.Inventory
            {
                // Any event on Inventory that is not ListingViewed or SearchPerformed is Invalid
                if (!InventoryAllowedTypes.Contains(messageType))
                {
                    return new SchemaValidationResult(
                        EventValidationClassification.Invalid,
                        messageType,
                        $"Event type '{messageType}' is not allowed on Inventory source.");
                }
            }

            if (!Schemas.TryGetValue(messageType, out var schema))
            {
                return new SchemaValidationResult(
                    EventValidationClassification.Invalid,
                    messageType,
                    $"Unknown or unregistered event schema for '{messageType}'.");
            }

            var eval = schema.Evaluate(root, _evaluationOptions);
            if (!eval.IsValid)
            {
                string errors = string.Join("; ", GetAllErrors(eval));
                return new SchemaValidationResult(
                    EventValidationClassification.Invalid,
                    messageType,
                    $"Schema validation failed: {errors}");
            }

            return new SchemaValidationResult(EventValidationClassification.Valid, messageType, null);
        }
    }

    private static IEnumerable<string> GetAllErrors(EvaluationResults results)
    {
        if (results.Errors != null)
        {
            foreach (var val in results.Errors.Values)
            {
                yield return val;
            }
        }

        if (results.Details != null)
        {
            foreach (var detail in results.Details)
            {
                foreach (var err in GetAllErrors(detail))
                {
                    yield return err;
                }
            }
        }
    }

    private static Dictionary<string, JsonSchema> LoadSchemas()
    {
        var schemas = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ListingViewed"] = "listing-viewed.schema.json",
            ["SearchPerformed"] = "search-performed.schema.json",
            ["CartItemAdded"] = "cart-item-added.schema.json",
            ["RentalItemPaid"] = "rental-item-paid.schema.json",
            ["RentalItemCompleted"] = "rental-item-completed.schema.json"
        };

        var assembly = typeof(EventSchemaValidator).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();

        foreach (var (msgType, filename) in mapping)
        {
            string? resourceName = resourceNames.FirstOrDefault(r => r.EndsWith(filename, StringComparison.OrdinalIgnoreCase));
            string? content = null;

            if (resourceName != null)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    content = reader.ReadToEnd();
                }
            }

            if (content == null)
            {
                string[] searchPaths =
                [
                    Path.Combine(AppContext.BaseDirectory, "docs", "contracts", "schemas", filename),
                    Path.Combine(AppContext.BaseDirectory, filename),
                    Path.Combine(Directory.GetCurrentDirectory(), "docs", "contracts", "schemas", filename)
                ];

                foreach (var path in searchPaths)
                {
                    if (File.Exists(path))
                    {
                        content = File.ReadAllText(path);
                        break;
                    }
                }
            }

            if (content != null)
            {
                schemas[msgType] = JsonSchema.FromText(content);
            }
            else
            {
                throw new InvalidOperationException($"Could not load required JSON schema '{filename}' for event type '{msgType}'.");
            }
        }

        return schemas;
    }
}
