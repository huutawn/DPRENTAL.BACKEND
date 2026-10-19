using System.IO;
using Dprental.RecommendationService.Api.Exceptions;
using Dprental.RecommendationService.Application.Background;
using Dprental.RecommendationService.Application.DependencyInjection;
using Dprental.RecommendationService.Application.Kafka.Options;
using Dprental.RecommendationService.Domain.DependencyInjection;
using Dprental.RecommendationService.Domain.IRepositories;
using Dprental.RecommendationService.Domain.Repositories;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

// CLI Flag: --migrate
if (args.Contains("--migrate", StringComparer.OrdinalIgnoreCase))
{
    var migrateBuilder = WebApplication.CreateBuilder(args);
    migrateBuilder.Services.AddRecommendationServiceDomainRepositories(migrateBuilder.Configuration.GetConnectionString("Postgres"));
    var migrateApp = migrateBuilder.Build();
    using var scope = migrateApp.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<RecommendationServiceDbContext>();
    try
    {
        Console.WriteLine("Applying migrations for RecommendationService...");
        await dbContext.Database.MigrateAsync();
        Console.WriteLine("Migrations applied successfully.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Migration failed: {ex.Message}");
        return 1;
    }
}

// CLI Flag: --rebuild-projections
if (args.Contains("--rebuild-projections", StringComparer.OrdinalIgnoreCase))
{
    var rebuildBuilder = WebApplication.CreateBuilder(args);
    rebuildBuilder.Services.AddRecommendationServiceDomainRepositories(rebuildBuilder.Configuration.GetConnectionString("Postgres"));
    var rebuildApp = rebuildBuilder.Build();
    using var scope = rebuildApp.Services.CreateScope();
    var uow = scope.ServiceProvider.GetRequiredService<IInteractionUnitOfWork>();
    try
    {
        Console.WriteLine("Rebuilding recommendation projections from interaction events...");
        await uow.RebuildProjectionsAsync(CancellationToken.None);
        Console.WriteLine("Projections rebuilt successfully.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Rebuild failed: {ex.Message}");
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRecommendationServiceDomainRepositories(builder.Configuration.GetConnectionString("Postgres"));

var kafkaOptions = builder.Configuration.GetSection(KafkaOptions.SectionName).Get<KafkaOptions>() ?? new KafkaOptions();
var valkeyOptions = builder.Configuration.GetSection(ValkeyOptions.SectionName).Get<ValkeyOptions>() ?? new ValkeyOptions();

if (kafkaOptions.Enabled)
{
    if (string.IsNullOrWhiteSpace(kafkaOptions.BootstrapServers))
    {
        throw new InvalidOperationException("Kafka is enabled but Kafka:BootstrapServers is missing or empty.");
    }
    if (kafkaOptions.MaxRetryAttemptsPerRound <= 0 || kafkaOptions.InitialRetryDelayMs <= 0 || kafkaOptions.MaxRetryDelayMs <= 0)
    {
        throw new InvalidOperationException("Kafka retry options must be positive numbers.");
    }
}

if (valkeyOptions.Enabled)
{
    string? redisConn = builder.Configuration.GetConnectionString("Redis");
    if (string.IsNullOrWhiteSpace(redisConn))
    {
        throw new InvalidOperationException("Valkey is enabled but ConnectionStrings:Redis is missing or empty.");
    }
    if (valkeyOptions.IntervalSeconds <= 0)
    {
        throw new InvalidOperationException("Valkey:IntervalSeconds must be positive.");
    }

    builder.Services.AddRecommendationServiceValkey(redisConn);
}
else
{
    builder.Services.AddSingleton<IValkeyConnectionProvider, NullValkeyConnectionProvider>();
}

builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.SectionName));
builder.Services.Configure<ValkeyOptions>(builder.Configuration.GetSection(ValkeyOptions.SectionName));

builder.Services.AddRecommendationServiceApplication(kafkaOptions.Enabled, valkeyOptions.Enabled);

builder.Services.AddHealthChecks()
    .AddDbContextCheck<RecommendationServiceDbContext>(
        "postgres",
        failureStatus: HealthStatus.Unhealthy,
        customTestQuery: async (dbContext, cancellationToken) =>
        {
            if (!await dbContext.Database.CanConnectAsync(cancellationToken))
            {
                return false;
            }

            if (dbContext.Database.IsRelational())
            {
                var pending = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
                if (pending.Any())
                {
                    return false;
                }
            }

            return true;
        },
        tags: ["ready"]);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var apiXml = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
    if (File.Exists(apiXml))
    {
        options.IncludeXmlComments(apiXml, includeControllerXmlComments: true);
    }
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

HealthCheckOptions livenessOptions = new()
{
    Predicate = _ => false,
    AllowCachingResponses = false
};
app.MapHealthChecks("/health/live", livenessOptions).AllowAnonymous();

HealthCheckOptions readinessOptions = new()
{
    Predicate = check => check.Tags.Contains("ready", StringComparer.Ordinal),
    AllowCachingResponses = false
};
app.MapHealthChecks("/health/ready", readinessOptions).AllowAnonymous();

app.MapControllers();

await app.RunAsync();
return 0;

/// <summary>
/// Điểm vào ứng dụng RecommendationService, được công khai để chạy kiểm thử tích hợp.
/// </summary>
public partial class Program
{
}
