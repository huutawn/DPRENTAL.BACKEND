namespace Dprental.AuthService.Application.DTOs.Auth;

/// <summary>Yêu cầu OAuth2 client_credentials — service tự xin token, không có user đứng sau.</summary>
public sealed record ServiceTokenRequest(string ClientId, string ClientSecret, string Audience, string Scope);
