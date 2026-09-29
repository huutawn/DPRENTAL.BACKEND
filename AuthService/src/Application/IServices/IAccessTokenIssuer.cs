using Dprental.AuthService.Application.DTOs.Auth;

namespace Dprental.AuthService.Application.IServices;

public interface IAccessTokenIssuer
{
    IssuedAccessToken Issue(AccessTokenRequest request);
}