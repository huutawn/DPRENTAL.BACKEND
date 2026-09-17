using Dprental.AuthService.Domain.DTOs.Auth;
using Dprental.AuthService.Domain.Models;

namespace Dprental.AuthService.Application.Mapping;

/// <summary>Maps Auth application models to the service's DTOs.</summary>
public static class AuthTokenMappingExtensions
{
    /// <summary>Converts an issued token pair to the response DTO.</summary>
    public static AuthTokenResponse ToDto(this AuthTokenPair tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return new AuthTokenResponse(tokens.AccessToken, tokens.RefreshToken);
    }
}
