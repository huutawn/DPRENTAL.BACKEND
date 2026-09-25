using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Application.Repositories;

public interface IServicePrincipalRepository
{
    Task<ServicePrincipal?> GetByClientIdAsync(string clientId, CancellationToken cancellationToken = default);

    Task<bool> HasScopeAsync(Guid servicePrincipalId, string scope, string audience, CancellationToken cancellationToken = default);
}
