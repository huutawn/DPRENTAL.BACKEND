using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Domain.DTOs.Auth;

public sealed record LoginRequest(
    [Required, StringLength(254)] string EmailOrUsername,
    [Required, StringLength(128)] string Password);
