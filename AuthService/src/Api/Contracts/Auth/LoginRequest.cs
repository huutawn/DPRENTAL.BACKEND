using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Api.Contracts.Auth;

public sealed record LoginRequest(
    [Required, StringLength(254)] string EmailOrUsername,
    [Required, StringLength(128)] string Password);
