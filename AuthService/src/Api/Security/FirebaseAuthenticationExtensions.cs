namespace Dprental.AuthService.Api.Security;

using Dprental.AuthService.Application.IServices;

internal static class FirebaseAuthenticationExtensions
{
    internal static IServiceCollection AddFirebaseAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<FirebaseAuthOptions>()
            .Bind(configuration.GetSection(FirebaseAuthOptions.SectionName))
            .Validate(options =>
            {
                if (!options.Enabled)
                {
                    return true;
                }

                return !string.IsNullOrWhiteSpace(options.ProjectId)
                    && (!string.IsNullOrWhiteSpace(options.CredentialsJson)
                        || !string.IsNullOrWhiteSpace(options.CredentialsPath));
            }, "Auth:Firebase requires ProjectId and CredentialsJson or CredentialsPath when Enabled=true.")
            .ValidateOnStart();

        services.AddSingleton<IFirebaseIdentityService, FirebaseIdentityService>();

        return services;
    }
}
