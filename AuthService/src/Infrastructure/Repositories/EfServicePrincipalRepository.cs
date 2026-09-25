using Dprental.AuthService.Application.Repositories;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Infrastructure.Repositories;

public sealed class EfServicePrincipalRepository(AuthDbContext dbContext) : IServicePrincipalRepository
{
    public Task<ServicePrincipal?> GetByClientIdAsync(string clientId, CancellationToken cancellationToken = default) =>
        dbContext.ServicePrincipals
            .AsNoTracking()
            .SingleOrDefaultAsync(principal => principal.ClientId == clientId, cancellationToken);

    public Task<bool> HasScopeAsync(Guid servicePrincipalId, string scope, string audience, CancellationToken cancellationToken = default) =>
        dbContext.ServicePrincipalScopes
            .AsNoTracking()
            .AnyAsync(
                servicePrincipalScope => servicePrincipalScope.ServicePrincipalId == servicePrincipalId
                    && servicePrincipalScope.Scope == scope
                    && servicePrincipalScope.Audience == audience,
                cancellationToken);
}
