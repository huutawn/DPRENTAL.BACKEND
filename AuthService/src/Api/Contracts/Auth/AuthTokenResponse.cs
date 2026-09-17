namespace Dprental.AuthService.Api.Contracts.Auth;

public sealed record AuthTokenResponse(string AccessToken, string RefreshToken);
