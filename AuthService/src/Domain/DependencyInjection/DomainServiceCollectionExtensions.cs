using Dprental.AuthService.Domain.Data;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.AuthService.Domain.DependencyInjection;

/// <summary>Registers the Domain-owned EF persistence and repositories.</summary>
public static class DomainServiceCollectionExtensions
{
    /// <summary>Registers PostgreSQL persistence for the Auth domain.</summary>
    public static IServiceCollection AddAuthDomainPersistence(this IServiceCollection services, string? connectionString)
    {
        services.AddDbContext<AuthDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IAuthSessionRepository, EfAuthSessionRepository>();
        services.AddScoped<IUserRepository, EfUserRepository>();
        services.AddScoped<IRoleRepository, EfRoleRepository>();
        services.AddScoped<IServicePrincipalRepository, EfServicePrincipalRepository>();
        services.AddScoped<IExternalLoginRepository, EfExternalLoginRepository>();
        services.AddScoped<IOrganizationRepository, EfOrganizationRepository>();
        services.AddScoped<IOrganizationMembershipRepository, EfOrganizationMembershipRepository>();
        services.AddScoped<INavigationCatalogRepository, EfNavigationCatalogRepository>();
        return services;
    }
}
