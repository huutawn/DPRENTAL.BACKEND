using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Application.DTOs.Auth;

public sealed record LoginRequest(
    [Required, EmailAddress, StringLength(254)] string Email,
    [Required, StringLength(128)] string Password);
