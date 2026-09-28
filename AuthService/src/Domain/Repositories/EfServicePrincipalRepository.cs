using Dprental.AuthService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Domain.Repositories;

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
