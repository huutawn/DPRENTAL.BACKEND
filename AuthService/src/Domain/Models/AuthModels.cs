namespace Dprental.AuthService.Domain.Models;

/// <summary>Minimal authenticated principal data used by the Auth session flow.</summary>
/// <param name="IsProfileCompleted">Null cho principal không phải user (vd service token) — không phát claim <c>pc</c> khi null.</param>
public sealed record AuthSessionPrincipal(
    Guid UserId,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string>? Permissions = null,
    IReadOnlyCollection<Guid>? OrganizationIds = null,
    bool? IsProfileCompleted = null)
{
    public IReadOnlyCollection<string> Permissions { get; init; } = Permissions ?? [];
    public IReadOnlyCollection<Guid> OrganizationIds { get; init; } = OrganizationIds ?? [];
}

/// <summary>Claims required to issue an access token.</summary>
/// <param name="IsProfileCompleted">
/// Null cho service token (không có hồ sơ user). True/false cho user token — phát ra claim <c>pc</c> để
/// service khác tự kiểm tra mà không cần gọi ngược AuthService; đây chỉ là gợi ý nhanh, không thay thế việc
/// AuthService tự kiểm tra lại từ DB ở các nghiệp vụ nội bộ (xem <c>ProfileCompletionGuard</c>).
/// </param>
public sealed record AccessTokenRequest(
    Guid SubjectId,
    IReadOnlyCollection<string>? Roles = null,
    IReadOnlyCollection<string>? Permissions = null,
    IReadOnlyCollection<Guid>? OrganizationIds = null,
    string? Audience = null,
    string? TokenUse = null,
    IReadOnlyCollection<string>? Scopes = null,
    bool? IsProfileCompleted = null);

/// <summary>A signed access token and its expiry.</summary>
public sealed record IssuedAccessToken(string Value, DateTimeOffset ExpiresAtUtc);

/// <summary>The access and refresh tokens returned by the Auth session flow.</summary>
public sealed record AuthTokenPair(string AccessToken, string RefreshToken);
