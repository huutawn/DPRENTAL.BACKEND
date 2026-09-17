using AuthService.Application.Authentication.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Infrastructure.Authentication;

/// <summary>Đăng ký các dependency phục vụ session persistence và refresh token.</summary>
public static class AuthenticationSessionServiceCollectionExtensions
{
    /// <summary>Đăng ký một implementation duy nhất cho các thao tác session của Auth Service.</summary>
    public static IServiceCollection AddAuthenticationSessions(this IServiceCollection services)
    {
        services.AddScoped<IAuthenticationSessionStore, SessionStore>();
        services.AddScoped<IAuthenticationSessionService, AuthenticationSessionService>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
