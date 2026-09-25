using Dprental.AuthService.Application.Repositories;
using Dprental.AuthService.Infrastructure.Data;
using Dprental.AuthService.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.AuthService.Infrastructure.DependencyInjection;

/// <summary>Registers Infrastructure EF persistence and repositories.</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Registers PostgreSQL persistence for the Auth domain.</summary>
    public static IServiceCollection AddAuthInfrastructurePersistence(this IServiceCollection services, string? connectionString)
    {
        services.AddDbContext<AuthDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IAuthSessionRepository, EfAuthSessionRepository>();
        services.AddScoped<IUserRepository, EfUserRepository>();
        services.AddScoped<IRoleRepository, EfRoleRepository>();
        services.AddScoped<IServicePrincipalRepository, EfServicePrincipalRepository>();
        services.AddScoped<IExternalLoginRepository, EfExternalLoginRepository>();
        services.AddScoped<IOrganizationRepository, EfOrganizationRepository>();
        services.AddScoped<IOrganizationMembershipRepository, EfOrganizationMembershipRepository>();
        services.AddScoped<IAuditLogRepository, EfAuditLogRepository>();
        services.AddScoped<IRecoveryTokenRepository, EfRecoveryTokenRepository>();
        return services;
    }
}
