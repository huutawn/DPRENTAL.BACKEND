using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Domain.DTOs.Auth;

public sealed record RegisterRequest(
    [Required, StringLength(254)] string EmailOrUsername,
    [Required, StringLength(128, MinimumLength = 8)] string Password);
