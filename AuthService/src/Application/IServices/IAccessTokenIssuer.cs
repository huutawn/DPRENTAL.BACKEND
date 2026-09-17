using Dprental.AuthService.Domain.Models;

namespace Dprental.AuthService.Application.IServices;

/// <summary>
/// Phát hành access token có thời hạn ngắn cho một chủ thể đã được xác thực.
/// Việc xác thực chủ thể thuộc trách nhiệm của use case gọi interface này.
/// </summary>
public interface IAccessTokenIssuer
{
    IssuedAccessToken Issue(AccessTokenRequest request);
}
