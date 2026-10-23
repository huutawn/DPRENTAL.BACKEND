using Dprental.PaymentService.Application.Security.Jwt;
using Dprental.PaymentService.Application.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.PaymentService.Api.Security;

/// <summary>
/// Mở rộng IServiceCollection thiết lập JWT Bearer Authentication cho PaymentService.
/// </summary>
public static class PaymentJwtAuthenticationExtensions
{
    public static IServiceCollection AddPaymentJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(PaymentJwtOptions.SectionName);
        services.AddOptions<PaymentJwtOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RentalServiceApiKeyOptions>()
            .Bind(configuration.GetSection(RentalServiceApiKeyOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient("payment-auth-jwks", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<PaymentJwtOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        services.AddSingleton<PaymentJwksConfigurationManager>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PaymentJwtOptions>>().Value;
            var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("payment-auth-jwks");
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return new PaymentJwksConfigurationManager(client, options, timeProvider);
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer()
            .AddScheme<AuthenticationSchemeOptions, PaymentServiceApiKeyAuthenticationHandler>(
                PaymentServiceApiKeyDefaults.AuthenticationScheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, SePayAuthenticationHandler>(
                SePayAuthenticationDefaults.AuthenticationScheme, _ => { });

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<PaymentJwksConfigurationManager, IOptions<PaymentJwtOptions>>((bearerOptions, configManager, jwtOptionsAccessor) =>
            {
                var options = jwtOptionsAccessor.Value;

                bearerOptions.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (context.Request.Path.StartsWithSegments("/api/v1/payment/webhooks", StringComparison.OrdinalIgnoreCase))
                        {
                            context.NoResult();
                            return Task.CompletedTask;
                        }

                        if (context.Request.Path.StartsWithSegments("/hubs/payments", StringComparison.OrdinalIgnoreCase) ||
                            IsInvoiceReadRequest(context.Request))
                        {
                            if (string.IsNullOrWhiteSpace(context.Token) &&
                                context.Request.Cookies.TryGetValue(Dprental.PaymentService.Application.Hub.Security.PaymentHubSecurityConstants.AccessTokenCookieName, out string? cookieToken) &&
                                !string.IsNullOrWhiteSpace(cookieToken))
                            {
                                context.Token = cookieToken;
                            }
                        }

                        return Task.CompletedTask;
                    }
                };
                bearerOptions.MapInboundClaims = false;
                bearerOptions.ConfigurationManager = configManager;
                bearerOptions.RefreshOnIssuerKeyNotFound = true;
                bearerOptions.SaveToken = true;
                bearerOptions.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,
                    ValidateAudience = true,
                    ValidAudiences = [options.ServiceAudience, options.ClientAudience],
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(options.ClockSkewSeconds),
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
                };
            });

        return services;
    }

    private static bool IsInvoiceReadRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method)) return false;

        string[] segments = request.Path.Value?
            .Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];

        if (segments.Length is not (4 or 5) ||
            !string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[1], "v1", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[2], "invoices", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(segments[3]))
        {
            return false;
        }

        return segments.Length == 4 ||
               string.Equals(segments[4], "status", StringComparison.OrdinalIgnoreCase);
    }
}
