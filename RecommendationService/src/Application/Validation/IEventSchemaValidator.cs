namespace Dprental.RecommendationService.Application.Validation;

public enum EventValidationClassification
{
    Valid,
    Invalid,
    Unrelated
}

public sealed record SchemaValidationResult(
    EventValidationClassification Classification,
    string? MessageType,
    string? ErrorReason);

public interface IEventSchemaValidator
{
    SchemaValidationResult Validate(string rawJson, InteractionEventSource source);
}
