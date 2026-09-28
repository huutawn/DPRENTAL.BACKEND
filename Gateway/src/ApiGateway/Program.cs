using Dprental.Gateway.Authentication;
using Dprental.Gateway.Errors;
using Dprental.Gateway.Health;
using Dprental.Gateway.Observability;
using Dprental.Gateway.Proxy;
using Dprental.Gateway.Security;
using Dprental.Gateway.Traffic;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Ocelot.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Đăng ký các dịch vụ hệ thống và bảo mật
builder.Services.AddGatewayDistributedTracing(builder.Configuration);
builder.Services.AddGatewayObservability(builder.Configuration);
builder.Services.AddGatewayJwtAuthentication(builder.Configuration);
builder.Services.AddGatewaySecurity(builder.Configuration);
builder.Services.AddGatewayTrafficControl(builder.Configuration);
builder.Services.AddGatewayOcelot(builder.Configuration);
builder.Services.AddGatewayHealthChecks();

var app = builder.Build();
OcelotServiceCollectionExtensions.ValidateOcelotConfiguration(app.Configuration);

// Cấu hình chuỗi Middleware Pipeline cho Gateway
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseGatewayRequestLogging();
app.UseGatewayMetrics();
app.UseMiddleware<GatewayExceptionMiddleware>();
app.UseGatewaySecurity();
app.UseAuthentication();
app.UseGatewayTrafficControl();

if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(options =>
    {
        options.DocumentTitle = "DPRental API";
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "AuthService");
        options.SwaggerEndpoint("/media/swagger/v1/swagger.json", "MediaService");
    });
}

HealthCheckOptions livenessOptions = new()
{
    Predicate = _ => false,
    AllowCachingResponses = false
};
app.UseHealthChecks("/health/live", livenessOptions);
app.UseHealthChecks("/_health/live", livenessOptions);

HealthCheckOptions readinessOptions = new()
{
    Predicate = check => check.Tags.Contains(GatewayHealthCheckTags.Readiness, StringComparer.Ordinal),
    AllowCachingResponses = false
};
app.UseHealthChecks("/health/ready", readinessOptions);
app.UseHealthChecks("/_health/ready", readinessOptions);

await app.UseOcelot();

app.Run();

public partial class Program;
