using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Dprental.InventoryService.Api.Controllers;
using System.IO;
using Dprental.InventoryService.Api.Exceptions;
using Dprental.InventoryService.Api.Routing;
using Dprental.InventoryService.Api.Security;
using Dprental.InventoryService.Application.DependencyInjection;
using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.Kafka.DependencyInjection;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInventoryServiceDomainRepositories(builder.Configuration.GetConnectionString("Postgres"));
builder.Services.AddInventoryKafkaInfrastructure(builder.Configuration);

// DPR-147: JWT Authentication & Authorization
builder.Services.AddInventoryJwtAuthentication(builder.Configuration);
builder.Services.AddInventoryAuthorization();
// Clean Architecture: Application services (IInventoryAssetService, ICalendarService, IInventoryMediaService, IInventoryAvailabilityService)
builder.Services.AddInventoryApplication();
// DPR-150: Valkey Read Cache
builder.Services.AddInventoryValkeyCache(builder.Configuration);
// Domain clients (MediaService & CatalogService): Options validation and typed HTTP clients
builder.Services.AddInventoryClients(builder.Configuration);

builder.Services.AddHealthChecks()
    .AddDbContextCheck<InventoryServiceDbContext>(
        "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

builder.Services.AddControllers(options =>
    options.Conventions.Add(new ApiRoutePrefixConvention(ApiRoutes.Prefix)));

builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    var defaultFactory = options.InvalidModelStateResponseFactory;
    options.InvalidModelStateResponseFactory = context =>
    {
        var actionDesc = context.ActionDescriptor as ControllerActionDescriptor;
        if (actionDesc != null &&
            (string.Equals(actionDesc.ControllerName, "InventoryAssets", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(actionDesc.ControllerName, "PublicInventoryAssets", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(actionDesc.ActionName, nameof(InventoryAssetsController.List), StringComparison.OrdinalIgnoreCase) &&
            HttpMethods.IsGet(context.HttpContext.Request.Method))
        {
            var firstError = context.ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .FirstOrDefault(msg => !string.IsNullOrWhiteSpace(msg)) ?? "Giá trị query parameter không hợp lệ.";

            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "INVALID_QUERY_PARAMETER",
                Detail = firstError,
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1"
            };
            problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;

            return new BadRequestObjectResult(problem)
            {
                ContentTypes = { "application/problem+json" }
            };
        }

        return defaultFactory(context);
    };
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.InferSecuritySchemes();
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

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

/// <summary>
/// Điểm vào ứng dụng InventoryService, được công khai để chạy kiểm thử tích hợp.
/// </summary>
public partial class Program
{
}
