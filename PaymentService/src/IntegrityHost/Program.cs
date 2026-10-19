using Dprental.PaymentService.Infrastructure.DependencyInjection;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.IntegrityHost.Options;
using Dprental.PaymentService.IntegrityHost.Persistence;
using Dprental.PaymentService.IntegrityHost.Services;
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
        string integrityConn = builder.Configuration.GetConnectionString("IntegrityPostgres")
            ?? builder.Configuration["IntegrityHost:IntegrityConnectionString"]
            ?? "Host=127.0.0.1;Port=26280;Database=integrity_evidence;Username=paymentservice;Password=postgres";

        builder.Services.AddDbContext<IntegrityDbContext>(options =>
            options.UseNpgsql(integrityConn));

        // 2. Payment DB context for reading unconfirmed journal and writing confirmation
        string paymentConn = builder.Configuration.GetConnectionString("Postgres")
            ?? builder.Configuration["IntegrityHost:PaymentConnectionString"]
            ?? "Host=127.0.0.1;Port=26280;Database=paymentservice;Username=paymentservice;Password=postgres";

        builder.Services.AddPaymentInfrastructure(paymentConn);

        // 3. Cryptographic key manager & workers
        builder.Services.AddSingleton<IntegrityKeyManager>();
        builder.Services.AddScoped<IntegrityVerificationEngine>();
        builder.Services.AddHostedService<IntegrityConfirmationWorker>();
        builder.Services.AddHostedService<IntegrityPeriodicScannerWorker>();

        var app = builder.Build();

        // Internal API Endpoints
        app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "PaymentService.IntegrityHost" }));

        app.MapGet("/internal/v1/integrity/public-key", (IntegrityKeyManager keyManager) =>
            Results.Ok(new { keyId = keyManager.KeyId, publicKey = keyManager.PublicKeyBase64 }));

        app.MapGet("/internal/v1/integrity/manifests/{streamId}", async (string streamId, IntegrityDbContext db, CancellationToken ct) =>
        {
            var manifest = await db.Manifests.AsNoTracking().FirstOrDefaultAsync(m => m.StreamId == streamId, ct);
            return manifest is not null ? Results.Ok(manifest) : Results.NotFound();
        });

        app.MapGet("/internal/v1/integrity/incidents", async (IntegrityDbContext db, CancellationToken ct) =>
        {
            var incidents = await db.Incidents.AsNoTracking().OrderByDescending(i => i.DetectedAt).Take(50).ToListAsync(ct);
            return Results.Ok(incidents);
        });

        app.MapPost("/internal/v1/integrity/verify", async (TransactionVerificationRequest req, IntegrityVerificationEngine engine, CancellationToken ct) =>
        {
            var result = await engine.VerifyAsync(req, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.BadRequest(result.Error);
        });

        return app;
    }
}
