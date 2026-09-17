using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Api.Contracts.Auth;

public sealed record RegisterRequest(
    [Required, StringLength(254)] string EmailOrUsername,
    [Required, StringLength(128, MinimumLength = 8)] string Password);
