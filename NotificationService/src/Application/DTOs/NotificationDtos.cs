namespace Dprental.NotificationService.Application.DTOs;

public sealed record NotificationListItemDto(
    Guid Id,
    string NotificationType,
    string Category,
    string Severity,
    string Title,
    string Body,
    string ResourceType,
    string ResourceId,
    string? DeepLinkUrl,
    bool IsActionable,
    string? ActionUrl,
    bool IsRead,
    DateTimeOffset? ReadAt,
    DateTimeOffset OccurredAt,
    DateTimeOffset CreatedAt);

public sealed record NotificationListResponseDto(
    IReadOnlyList<NotificationListItemDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    bool HasPrevious,
    bool HasNext);

public sealed record UnreadCountResponseDto(int UnreadCount);

public sealed record MarkAllReadResponseDto(
    int MarkedCount,
    DateTimeOffset CutoffUtc,
    DateTimeOffset ReadAt);

public sealed record PreferenceDto(
    string Category,
    bool EmailEnabled);

public sealed record PreferencesResponseDto(
    IReadOnlyList<PreferenceDto> Preferences);

public sealed record UpdatePreferencesRequestDto(
    IReadOnlyList<PreferenceDto> Preferences);
