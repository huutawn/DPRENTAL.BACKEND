using Dprental.AuthService.Domain.Models;

namespace Dprental.AuthService.Application.IServices;

public interface IAccessTokenIssuer
{
    IssuedAccessToken Issue(AccessTokenRequest request);
}