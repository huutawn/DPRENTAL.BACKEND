namespace Dprental.ReviewService.Application.DTOs.Reviews;

public sealed record AttachMediaRequest(Guid MediaId, string? MediaType);

public sealed record ReviewMediaResponse(
    Guid Id,
    Guid ReviewId,
    Guid MediaId,
    string MediaType,
    DateTimeOffset CreatedAt);
