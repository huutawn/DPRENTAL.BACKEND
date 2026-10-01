using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Security.Jwt;
using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Application.Options;
using Dprental.AuthService.Application.Validators;
using Dprental.AuthService.Domain.Entities;
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

        services.AddSingleton<IValidateOptions<MediaServiceOptions>, MediaServiceOptionsValidator>();
        services.AddOptions<MediaServiceOptions>()
            .Bind(configuration.GetSection(MediaServiceOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<OrganizationReviewOptions>()
            .Bind(configuration.GetSection(OrganizationReviewOptions.SectionName))
            .Validate(
                options => options.SlaHours > 0 && options.ReminderCooldownHours > 0,
                "OrganizationReview:SlaHours và OrganizationReview:ReminderCooldownHours phải lớn hơn 0.")
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
        services.AddScoped<IFirebaseLoginService, FirebaseLoginService>();
        services.AddScoped<IOrganizationService, OrganizationService>();
        services.AddScoped<IOrganizationRegistrationService, OrganizationRegistrationService>();
        services.AddScoped<IOrganizationReviewService, OrganizationReviewService>();
        services.AddScoped<IUserAccountService, UserAccountService>();
        services.AddScoped<IMediaUrlResolver, MediaUrlResolver>();
        services.AddScoped<IMediaOutboxService, MediaOutboxService>();

        return services;
    }
}
