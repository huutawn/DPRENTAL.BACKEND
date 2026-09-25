using AuthService.Domain.DTOs.User;
using Dprental.AuthService.Api.Endpoints;
using Dprental.AuthService.Api.Observability;
using Dprental.AuthService.Api.Routing;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.DependencyInjection;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Repositories;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthServiceDistributedTracing(builder.Configuration);
builder.Services.AddAuthApplication(builder.Configuration);
builder.Services.AddAuthDomainRepositories(builder.Configuration.GetConnectionString("Postgres"));
builder.Services.AddJwtBearerSecurity();
builder.Services.AddAuthForwardedHeaders(builder.Configuration);
builder.Services.AddGoogleLogin(builder.Configuration);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:3000")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});
builder.Services.AddControllers(options => options.Conventions.Add(new ApiRoutePrefixConvention(ApiRoutes.Prefix)));
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<GetUsersRequestValidator>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICorrelationIdProvider, CorrelationIdProvider>();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.InferSecuritySchemes();
    options.IncludeXmlComments(
        Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml"),
        includeControllerXmlComments: true);
});
builder.Services.AddHealthChecks()

    .AddDbContextCheck<AuthDbContext>(
        "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

var app = builder.Build();

app.UseForwardedHeaders();

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
app.MapControllers();
app.MapJwksEndpoint();

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

app.Run();

/// <summary>Điểm vào ứng dụng AuthService, được công khai để chạy kiểm thử tích hợp.</summary>
public partial class Program { }
