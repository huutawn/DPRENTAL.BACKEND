using System.IO;
using Dprental.PaymentService.Api.Exceptions;
using Dprental.PaymentService.Api.Security;
using Dprental.PaymentService.Application.DependencyInjection;
using Dprental.PaymentService.Domain.Repository;
using Dprental.PaymentService.Domain.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPaymentDomainRepositories(builder.Configuration.GetConnectionString("Postgres"));
builder.Services.AddPaymentApplication(builder.Configuration);

builder.Services.AddPaymentJwtAuthentication(builder.Configuration);
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("InvoiceServicePolicy", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
            ctx.User.IsServiceToken() &&
            ctx.User.HasServiceAudience(builder.Configuration.GetValue<string>("Authentication:Jwt:ServiceAudience") ?? "payment-api") &&
            ctx.User.HasScope("payment.invoice.write"));
    });

    options.AddPolicy("InvoiceRenterPolicy", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
            ctx.User.HasClientAudience(builder.Configuration.GetValue<string>("Authentication:Jwt:ClientAudience") ?? "dprental-clients") &&
            ctx.User.TryGetSubjectUserId(out _));
    });
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<PaymentServiceDbContext>(
        "postgres",
        failureStatus: HealthStatus.Unhealthy,
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
/// Điểm vào ứng dụng PaymentService, được công khai để chạy kiểm thử tích hợp.
/// </summary>
public partial class Program
{
}
