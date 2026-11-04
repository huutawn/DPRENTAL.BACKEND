namespace Dprental.AuthService.Application.DTOs.User;

public sealed record NotificationContactBatchRequest(IReadOnlyList<Guid> UserIds);

public sealed record NotificationContactDto(
    Guid UserId,
    string Email,
    bool IsActive,
    bool IsEmailVerified);

public sealed record NotificationContactBatchResponse(
    IReadOnlyList<NotificationContactDto> Contacts);
