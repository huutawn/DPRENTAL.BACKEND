using Dprental.AuthService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.AuthService.Domain.Repositories;

/// <summary>Registers Domain EF repositories and DbContext.</summary>
public static class DomainRepositoriesServiceCollectionExtensions
{
    /// <summary>Registers PostgreSQL persistence for the Auth domain.</summary>
    public static IServiceCollection AddAuthDomainRepositories(this IServiceCollection services, string? connectionString)
    {
        services.AddDbContext<AuthDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IAuthSessionRepository, EfAuthSessionRepository>();
        services.AddScoped<IUserRepository, EfUserRepository>();
        services.AddScoped<IRoleRepository, EfRoleRepository>();
        services.AddScoped<IExternalLoginRepository, EfExternalLoginRepository>();
        services.AddScoped<IOrganizationRepository, EfOrganizationRepository>();
        services.AddScoped<IOrganizationMembershipRepository, EfOrganizationMembershipRepository>();
        services.AddScoped<IAuditLogRepository, EfAuditLogRepository>();
        services.AddScoped<IRecoveryTokenRepository, EfRecoveryTokenRepository>();
        services.AddScoped<IMediaOutboxRepository, EfMediaOutboxRepository>();
        return services;
    }

    /// <summary>Alias tương thích cho AddAuthDomainRepositories.</summary>
    public static IServiceCollection AddAuthDomainPersistence(this IServiceCollection services, string? connectionString) =>
        services.AddAuthDomainRepositories(connectionString);
}
