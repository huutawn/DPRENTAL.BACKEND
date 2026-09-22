using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Security.Jwt;
using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Models;
using Dprental.AuthService.Domain.Utils;
using Dprental.AuthService.Domain.Validators;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dprental.AuthService.Application.DependencyInjection;

/// <summary>Registers application services and Auth security dependencies.</summary>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>Registers the single Auth application flow and its JWT token issuer.</summary>
    public static IServiceCollection AddAuthApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetRequiredSection(JwtOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<AdminBootstrapOptions>, AdminBootstrapOptionsValidator>();
        services.AddOptions<AdminBootstrapOptions>()
            .Bind(configuration.GetSection(AdminBootstrapOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RsaJwtKeyRing>();
        services.AddSingleton<IAccessTokenIssuer, RsaAccessTokenIssuer>();
        services.AddScoped<IAuthSessionService, AuthSessionService>();
        services.AddTransient<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IRoleAssignmentService, RoleAssignmentService>();
        services.AddScoped<IServiceIdentityService, ServiceIdentityService>();
        services.AddScoped<IExternalLoginService, ExternalLoginService>();
        services.AddScoped<IOrganizationService, OrganizationService>();
        services.AddScoped<INavigationService, NavigationService>();

        return services;
    }
}
