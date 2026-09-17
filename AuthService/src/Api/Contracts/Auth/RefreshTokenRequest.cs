using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Api.Contracts.Auth;

public sealed record RefreshTokenRequest([Required] string RefreshToken);
