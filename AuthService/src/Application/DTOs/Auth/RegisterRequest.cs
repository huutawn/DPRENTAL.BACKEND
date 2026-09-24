using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Application.DTOs.Auth;

public sealed record RegisterRequest(
    [Required, EmailAddress, StringLength(254)] string Email,
    [Required, StringLength(128, MinimumLength = 8)] string Password,
    [Required, StringLength(100)] string FullName);
