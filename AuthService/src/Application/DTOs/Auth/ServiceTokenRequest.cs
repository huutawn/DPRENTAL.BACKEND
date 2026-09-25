using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Application.DTOs.Auth;

/// <summary>Yêu cầu OAuth2 client_credentials — service tự xin token, không có user đứng sau.</summary>
public sealed record ServiceTokenRequest(
    [Required, StringLength(100)] string ClientId,
    [Required, StringLength(200)] string ClientSecret,
    [Required, StringLength(200)] string Audience,
    [Required, StringLength(500)] string Scope);
