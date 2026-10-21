using System.Security.Cryptography;
using Dprental.PaymentService.Domain.DependencyInjection;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.IntegrityHost.Options;
using Dprental.PaymentService.Domain.Repositories;
using Dprental.PaymentService.IntegrityHost.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dprental.PaymentService.IntegrityHost;

public static class IntegrityHostProgram
{
    public static void Main(string[] args)
    {
        CreateWebApplication(args).Run();
    }

    public static WebApplication CreateWebApplication(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.Configure<IntegrityHostOptions>(builder.Configuration.GetSection(IntegrityHostOptions.SectionName));

        // 1. Separate PostgreSQL database for Integrity evidence store
        string? integrityConn = builder.Configuration.GetConnectionString("IntegrityPostgres")
            ?? builder.Configuration["IntegrityHost:IntegrityConnectionString"];
        if (string.IsNullOrWhiteSpace(integrityConn))
        {
            throw new InvalidOperationException("Missing required connection string: 'ConnectionStrings:IntegrityPostgres' or 'IntegrityHost:IntegrityConnectionString'.");
        }

        builder.Services.AddIntegrityEvidencePersistence(integrityConn);

        // 2. Payment DB context for reading unconfirmed journal and writing confirmation
        string? paymentConn = builder.Configuration.GetConnectionString("Postgres")
            ?? builder.Configuration["IntegrityHost:PaymentConnectionString"];
        if (string.IsNullOrWhiteSpace(paymentConn))
        {
            throw new InvalidOperationException("Missing required connection string: 'ConnectionStrings:Postgres' or 'IntegrityHost:PaymentConnectionString'.");
        }

        string configuredKey = builder.Configuration["IntegrityHost:InternalApiKey"] ?? string.Empty;
        if (System.Text.Encoding.UTF8.GetByteCount(configuredKey) < 32)
        {
            throw new InvalidOperationException("IntegrityHost:InternalApiKey must be configured with at least 32 bytes.");
        }

        builder.Services.AddPaymentDomainPersistence(paymentConn);
        // 3. Cryptographic key manager & workers
        builder.Services.AddOptions<Dprental.PaymentService.Application.Options.PaymentEncryptionOptions>()
            .BindConfiguration(Dprental.PaymentService.Application.Options.PaymentEncryptionOptions.SectionName);
        builder.Services.AddOptions<Dprental.PaymentService.Application.Options.PaymentKafkaOptions>()
            .BindConfiguration(Dprental.PaymentService.Application.Options.PaymentKafkaOptions.SectionName);
        builder.Services.AddSingleton<Dprental.PaymentService.Common.Cryptography.IPaymentDataEncryptor>(sp =>
        {
            var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Dprental.PaymentService.Application.Options.PaymentEncryptionOptions>>().Value;
            return new Dprental.PaymentService.Common.Cryptography.PaymentDataEncryptor(opt.GetKeyBytes(), opt.ActiveKeyId, opt.GetOldKeyBytes());
        });
        builder.Services.AddSingleton<IntegrityKeyManager>();
        builder.Services.AddSingleton<Dprental.PaymentService.Application.IServices.IIntegritySigningAdapter>(sp => sp.GetRequiredService<IntegrityKeyManager>());
        builder.Services.AddScoped<Dprental.PaymentService.Application.Services.Integrity.IntegrityVerificationEngine>();
        builder.Services.AddScoped<Dprental.PaymentService.Application.IServices.IIntegrityVerificationService>(sp => sp.GetRequiredService<Dprental.PaymentService.Application.Services.Integrity.IntegrityVerificationEngine>());
        builder.Services.AddScoped<Dprental.PaymentService.Application.IServices.IIntegrityConfirmationService, Dprental.PaymentService.Application.Services.Integrity.IntegrityConfirmationService>();
        builder.Services.AddScoped<Dprental.PaymentService.Application.IServices.IIntegrityScannerService, Dprental.PaymentService.Application.Services.Integrity.IntegrityScannerService>();
        builder.Services.AddHostedService<IntegrityConfirmationWorker>();
        builder.Services.AddHostedService<IntegrityPeriodicScannerWorker>();

        var app = builder.Build();
        // Eager cryptographic key validation at startup
        using (var scope = app.Services.CreateScope())
        {
            _ = scope.ServiceProvider.GetRequiredService<IntegrityKeyManager>();
            _ = scope.ServiceProvider.GetRequiredService<Dprental.PaymentService.Common.Cryptography.IPaymentDataEncryptor>();
        }


        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            if (!context.Request.Headers.TryGetValue("X-Api-Key", out var headerValues) ||
                headerValues.Count != 1 ||
                string.IsNullOrWhiteSpace(headerValues[0]))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                    Title = "Unauthorized",
                    Status = StatusCodes.Status401Unauthorized,
                    Detail = "Header 'X-Api-Key' is missing or has multiple values.",
                    Instance = context.Request.Path
                }, options: null, contentType: "application/problem+json");
                return;
            }
            byte[] providedDigest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(headerValues[0]!));
            byte[] configuredDigest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(configuredKey));

            if (!CryptographicOperations.FixedTimeEquals(providedDigest, configuredDigest))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                    Title = "Unauthorized",
                    Status = StatusCodes.Status401Unauthorized,
                    Detail = "Invalid 'X-Api-Key' header.",
                    Instance = context.Request.Path
                }, options: null, contentType: "application/problem+json");
                return;
            }

            await next();
        });
        // Internal API Endpoints
        app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "PaymentService.IntegrityHost" }));

        app.MapGet("/internal/v1/integrity/public-key", (IntegrityKeyManager keyManager) =>
            Results.Ok(new { keyId = keyManager.KeyId, publicKey = keyManager.PublicKeyBase64 }));

        app.MapGet("/internal/v1/integrity/manifests/{streamId}", async (string streamId, Dprental.PaymentService.Domain.IRepositories.IIntegrityEvidenceRepository repo, CancellationToken ct) =>
        {
            var manifest = await repo.GetManifestAsync(streamId, ct);
            return manifest is not null ? Results.Ok(manifest) : Results.NotFound();
        });

        app.MapGet("/internal/v1/integrity/incidents", async (Dprental.PaymentService.Domain.IRepositories.IIntegrityEvidenceRepository repo, CancellationToken ct) =>
        {
            var incidents = await repo.GetRecentIncidentsAsync(50, ct);
            return Results.Ok(incidents);
        });

        app.MapPost("/internal/v1/integrity/verify", async (TransactionVerificationRequest req, Dprental.PaymentService.Application.IServices.IIntegrityVerificationService engine, CancellationToken ct) =>
        {
            var result = await engine.VerifyAsync(req, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(result.Error);
        });

        return app;
    }
}
