using System.Security.Cryptography;
using System.Text;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.Execution;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Utils;
using Dprental.AuthService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Dprental.AuthService.Application.Services;

/// <summary>Phát service token qua OAuth2 client_credentials — chủ thể là service, không phải user.</summary>
public sealed class ServiceIdentityService(
    IServicePrincipalRepository servicePrincipalRepository,
    IAccessTokenIssuer accessTokenIssuer,
    ICorrelationIdProvider correlationIdProvider,
    ILogger<ServiceIdentityService> logger) : IServiceIdentityService
{
    private const string ServiceTokenUse = JwtClaimCatalog.TokenUses.Service;

    public async Task<Result<ServiceTokenResponse>> IssueTokenAsync(ServiceTokenRequest request, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure<ServiceTokenResponse>(validationError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "IssueServiceToken",
            async () =>
            {
                ServicePrincipal? principal = await servicePrincipalRepository.GetByClientIdAsync(request.ClientId, cancellationToken);
                if (principal is null || !principal.IsActive || !SecretMatches(request.ClientSecret, principal.SecretHash))
                {
                    return Result.Failure<ServiceTokenResponse>(AuthErrors.ServicePrincipal.InvalidClient);
                }

                if (!await servicePrincipalRepository.HasScopeAsync(principal.Id, request.Scope, request.Audience, cancellationToken))
                {
                    return Result.Failure<ServiceTokenResponse>(AuthErrors.ServicePrincipal.InvalidScope);
                }

                IssuedAccessToken token = accessTokenIssuer.Issue(new AccessTokenRequest(
                    principal.Id,
                    Audience: request.Audience,
                    TokenUse: ServiceTokenUse,
                    Scopes: [request.Scope]));

                return Result.Success(new ServiceTokenResponse(token.Value, token.ExpiresAtUtc));
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    private static bool SecretMatches(string candidateSecret, string storedHash)
    {
        string candidateHash = RefreshTokenUtility.ComputeHash(candidateSecret);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidateHash),
            Encoding.UTF8.GetBytes(storedHash));
    }
}
