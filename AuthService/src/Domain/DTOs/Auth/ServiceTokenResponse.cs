namespace Dprental.AuthService.Domain.DTOs.Auth;

/// <summary>Service token — không có refresh token, service tự xin lại bằng client_credentials khi hết hạn.</summary>
public sealed record ServiceTokenResponse(string AccessToken, DateTimeOffset ExpiresAtUtc);
