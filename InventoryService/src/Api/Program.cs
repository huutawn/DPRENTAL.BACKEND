using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Dprental.InventoryService.Api.Controllers;
using System.IO;
using Dprental.InventoryService.Api.Exceptions;
using Dprental.InventoryService.Api.Routing;
using Dprental.InventoryService.Api.Caching;
using Dprental.InventoryService.Api.Recommendation;
using Dprental.InventoryService.Api.Security;
using Dprental.InventoryService.Application.DependencyInjection;
using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.Kafka.DependencyInjection;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Application.Search.DependencyInjection;
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
builder.Services.AddInventoryValkeyCache(builder.Configuration, builder.Environment);
builder.Services.AddRecommendationRanking(builder.Configuration);
builder.Services.AddInventoryClients(builder.Configuration);

// Meilisearch: Search client and index services
builder.Services.AddInventoryMeilisearch(builder.Configuration);
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
        var hasDepositError = context.ModelState.Keys.Any(k => string.Equals(k, "depositAmount", StringComparison.OrdinalIgnoreCase) || k.EndsWith(".depositAmount", StringComparison.OrdinalIgnoreCase)) ||
                              context.ModelState.Values.SelectMany(v => v.Errors).Any(e => (e.ErrorMessage != null && e.ErrorMessage.Contains("INVALID_DEPOSIT_AMOUNT", StringComparison.OrdinalIgnoreCase)) || (e.Exception?.Message != null && e.Exception.Message.Contains("INVALID_DEPOSIT_AMOUNT", StringComparison.OrdinalIgnoreCase)));
        if (hasDepositError)
        {
            var depositMsg = context.ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => !string.IsNullOrWhiteSpace(e.ErrorMessage) ? e.ErrorMessage : e.Exception?.Message)
                .FirstOrDefault(msg => !string.IsNullOrWhiteSpace(msg) && msg.Contains("INVALID_DEPOSIT_AMOUNT", StringComparison.OrdinalIgnoreCase));

            if (depositMsg != null && depositMsg.StartsWith("INVALID_DEPOSIT_AMOUNT: ", StringComparison.OrdinalIgnoreCase))
            {
                depositMsg = depositMsg["INVALID_DEPOSIT_AMOUNT: ".Length..];
            }

            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "INVALID_DEPOSIT_AMOUNT",
                Detail = depositMsg ?? "Giá trị tiền cọc không hợp lệ.",
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1"
            };
            problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;

            return new BadRequestObjectResult(problem)
            {
                ContentTypes = { "application/problem+json" }
            };
        }

        var actionDesc = context.ActionDescriptor as ControllerActionDescriptor;
        if (actionDesc != null &&
            (string.Equals(actionDesc.ControllerName, "InventoryAssets", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(actionDesc.ControllerName, "PublicEquipmentListings", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(actionDesc.ControllerName, "EquipmentListings", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(actionDesc.ActionName, "List", StringComparison.OrdinalIgnoreCase) &&
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

app.UseMiddleware<InternalApiKeyMiddleware>();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.Request.Headers.ContainsKey("Authorization") &&
        context.User?.Identity?.IsAuthenticated != true)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
            Title = "Unauthorized",
            Status = StatusCodes.Status401Unauthorized,
            Detail = "Invalid or expired JWT token.",
            Instance = context.Request.Path
        }, options: null, contentType: "application/problem+json");
        return;
    }
    await next();
});
app.UseAuthorization();

app.MapControllers();

app.Run();

/// <summary>
/// Điểm vào ứng dụng InventoryService, được công khai để chạy kiểm thử tích hợp.
/// </summary>
public partial class Program
{
}
