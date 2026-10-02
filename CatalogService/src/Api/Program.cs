using Dprental.CatalogService.Api.Exceptions;
using Dprental.CatalogService.Api.Filters;
using Dprental.CatalogService.Api.Observability;
using Dprental.CatalogService.Api.Routing;
using Dprental.CatalogService.Api.Security;
using Dprental.CatalogService.Api.Clients;
using Dprental.CatalogService.Application.Clients;
using Dprental.CatalogService.Application.Common;
using Dprental.CatalogService.Application.Options;
using Microsoft.Extensions.Options;
using Dprental.CatalogService.Application.DependencyInjection;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.Repositories;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCatalogServiceDistributedTracing(builder.Configuration);
builder.Services.AddCatalogServiceApplication(builder.Configuration);
builder.Services.AddCatalogServiceDomainRepositories(builder.Configuration.GetConnectionString("Postgres"));
builder.Services.AddJwtBearerSecurity(builder.Configuration);
builder.Services.AddAuthForwardedHeaders(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICorrelationIdProvider, CorrelationIdProvider>();

var mediaSection = builder.Configuration.GetSection(MediaServiceOptions.SectionName);
var mediaOptions = mediaSection.Get<MediaServiceOptions>() ?? new MediaServiceOptions();
if (!string.IsNullOrWhiteSpace(builder.Configuration["PublicGatewayUrl"]))
{
    mediaOptions.PublicGatewayUrl = builder.Configuration["PublicGatewayUrl"]!;
}

var mediaValidator = new MediaServiceOptionsValidator(builder.Environment.IsProduction());
var mediaValidationResult = mediaValidator.Validate(null, mediaOptions);
if (mediaValidationResult.Failed)
{
    throw new OptionsValidationException(MediaServiceOptions.SectionName, typeof(MediaServiceOptions), mediaValidationResult.Failures);
}

MediaUrlResolver.Configure(mediaOptions.PublicGatewayUrl);
builder.Services.AddSingleton<IValidateOptions<MediaServiceOptions>>(mediaValidator);
builder.Services.AddOptions<MediaServiceOptions>()
    .Bind(mediaSection)
    .PostConfigure(opts =>
    {
        if (!string.IsNullOrWhiteSpace(builder.Configuration["PublicGatewayUrl"]))
        {
            opts.PublicGatewayUrl = builder.Configuration["PublicGatewayUrl"]!;
        }
    })
    .ValidateOnStart();

builder.Services.AddHttpClient<IMediaServiceClient, MediaServiceClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<MediaServiceOptions>>().Value;
    client.BaseAddress = new Uri(opts.InternalBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
    client.DefaultRequestHeaders.Add("X-Api-Key", opts.ApiKey);
});

string[] configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? [];

if (configuredOrigins.Length == 0 && builder.Environment.IsDevelopment())
{
    configuredOrigins = ["http://localhost:3000"];
}

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (configuredOrigins.Length > 0)
        {
            policy.WithOrigins(configuredOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        }
    });
});

builder.Services.AddControllers(options => options.Conventions.Add(new ApiRoutePrefixConvention(ApiRoutes.Prefix)))
    .ConfigureApiBehaviorOptions(options =>
    {
        // Lỗi model binding (JSON hỏng, sai kiểu dữ liệu) cũng trả mã ổn định thay cho title mặc định của framework.
        options.InvalidModelStateResponseFactory = context =>
        {
            ProblemDetailsFactory factory = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
            ValidationProblemDetails problem = factory.CreateValidationProblemDetails(
                context.HttpContext,
                context.ModelState,
                statusCode: StatusCodes.Status400BadRequest,
                title: CatalogErrors.Catalog.InvalidRequest.Code,
                detail: CatalogErrors.Catalog.InvalidRequest.Message);

            return new BadRequestObjectResult(problem)
            {
                ContentTypes = { "application/problem+json" }
            };
        };
    });
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions[CorrelationIdMiddleware.ProblemDetailsKey] =
            CorrelationIdMiddleware.GetCorrelationId(context.HttpContext));
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.InferSecuritySchemes();
    var apiXml = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
    if (File.Exists(apiXml))
    {
        options.IncludeXmlComments(apiXml, includeControllerXmlComments: true);
    }
    options.SchemaFilter<UpdateEquipmentTypeRequestSchemaFilter>();
    options.OperationFilter<BearerSecurityRequirementOperationFilter>();
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<CatalogServiceDbContext>(
        "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

var app = builder.Build();

app.UseForwardedHeaders();

app.UseMiddleware<CorrelationIdMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

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

app.Run();

/// <summary>
/// Điểm vào ứng dụng CatalogService, được công khai để chạy kiểm thử tích hợp.
/// </summary>
public partial class Program
{
}
