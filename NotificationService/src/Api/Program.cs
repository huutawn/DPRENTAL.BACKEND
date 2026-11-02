using System.IO;
using Dprental.NotificationService.Api.Exceptions;
using Dprental.NotificationService.Api.Hubs;
using Dprental.NotificationService.Api.Security;
using Dprental.NotificationService.Api.Services;
using Dprental.NotificationService.Application.Clients;
using Dprental.NotificationService.Application.DependencyInjection;
using Dprental.NotificationService.Application.Email;
using Dprental.NotificationService.Application.Email.Options;
using Dprental.NotificationService.Application.Email.Workers;
using Dprental.NotificationService.Application.IServices;
using Dprental.NotificationService.Domain.Repositories;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNotificationSecurityAndAuthentication(builder.Configuration);
builder.Services.AddNotificationServiceDomainRepositories(builder.Configuration.GetConnectionString("Postgres"));
builder.Services.AddNotificationServiceApplication();

// Cấu hình SMTP & Email Delivery
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddHostedService<EmailDeliveryWorker>();
builder.Services.AddHostedService<EmailLeaseReconciliationWorker>();

// Cấu hình AuthService HttpClient
builder.Services.AddHttpClient<IAuthContactClient, AuthContactClient>(client =>
{
    var authBaseUrl = builder.Configuration["Services:AuthService:BaseUrl"] ?? "http://auth-service:8080";
    client.BaseAddress = new Uri(authBaseUrl);
});

// Cấu hình SignalR Realtime Notifier
builder.Services.AddSignalR();
builder.Services.AddSingleton<INotificationRealtimeNotifier, SignalRNotificationRealtimeNotifier>();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<NotificationServiceDbContext>(
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
app.MapHub<NotificationHub>("/api/v1/notifications/hubs/inbox");
app.Run();

/// <summary>
/// Điểm vào ứng dụng NotificationService, được công khai để chạy kiểm thử tích hợp.
/// </summary>
public partial class Program
{
}
