using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Application.DTOs.User;

/// <summary>Request liên kết tài khoản bên ngoài bằng Firebase ID token.</summary>
public sealed record LinkExternalLoginRequest(
    [property: Required, StringLength(4096)] string? IdToken);
