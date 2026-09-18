using Dprental.AuthService.Domain.DTOs.Auth;
using Dprental.AuthService.Domain.Models;

namespace Dprental.AuthService.Application.Mapping;


public static class AuthTokenMappingExtensions
{
    public static AuthTokenResponse ToDto(this AuthTokenPair tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return new AuthTokenResponse(tokens.AccessToken, tokens.RefreshToken);
    }
}
