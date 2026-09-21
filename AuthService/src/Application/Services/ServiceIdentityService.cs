using System.Security.Cryptography;
using System.Text;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Auth;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Models;
using Dprental.AuthService.Domain.Utils;

namespace Dprental.AuthService.Application.Services;

/// <summary>Phát service token qua OAuth2 client_credentials — chủ thể là service, không phải user.</summary>
public sealed class ServiceIdentityService(
    IServicePrincipalRepository servicePrincipalRepository,
    IAccessTokenIssuer accessTokenIssuer) : IServiceIdentityService
{
    /// <summary>Giá trị claim <c>token_use</c> (rút gọn thành "u") đánh dấu đây là service token.</summary>
    private const string ServiceTokenUse = JwtClaimCatalog.TokenUses.Service;

    public async Task<Result<ServiceTokenResponse>> IssueTokenAsync(ServiceTokenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ServicePrincipal? principal = await servicePrincipalRepository.GetByClientIdAsync(request.ClientId, cancellationToken);
        if (principal is null || !principal.IsActive || !SecretMatches(request.ClientSecret, principal.SecretHash))
        {
            // Sai client_id, sai secret hay principal bị khóa đều trả cùng một lỗi để không lộ lý do cụ thể.
            return AuthErrors.ServicePrincipal.InvalidClient;
        }

        if (!await servicePrincipalRepository.HasScopeAsync(principal.Id, request.Scope, request.Audience, cancellationToken))
        {
            return AuthErrors.ServicePrincipal.InvalidScope;
        }

        IssuedAccessToken token = accessTokenIssuer.Issue(new AccessTokenRequest(
            principal.Id,
            Audience: request.Audience,
            TokenUse: ServiceTokenUse,
            Scopes: [request.Scope]));

        return new ServiceTokenResponse(token.Value, token.ExpiresAtUtc);
    }

    /// <summary>So khớp bằng thời gian cố định để không lộ độ dài hash qua timing; secret không lưu ở dạng thô.</summary>
    private static bool SecretMatches(string candidateSecret, string storedHash)
    {
        string candidateHash = RefreshTokenUtility.ComputeHash(candidateSecret);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidateHash),
            Encoding.UTF8.GetBytes(storedHash));
    }
}
