using Dprental.AuthService.Application.DTOs.Auth;

namespace Dprental.AuthService.Application.Mapping;


public static class AuthTokenMappingExtensions
{
    public static AuthTokenResponse ToDto(this AuthTokenPair tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return new AuthTokenResponse(tokens.AccessToken, tokens.RefreshToken);
    }
}
