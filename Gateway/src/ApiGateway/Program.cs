using Dprental.Gateway.Authentication;
using Dprental.Gateway.Errors;
using Dprental.Gateway.Health;
using Dprental.Gateway.Observability;
using Dprental.Gateway.Proxy;
using Dprental.Gateway.Security;
using Dprental.Gateway.Traffic;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Đăng ký các dịch vụ hệ thống và bảo mật
builder.Services.AddGatewayDistributedTracing(builder.Configuration);
builder.Services.AddGatewayObservability(builder.Configuration);
builder.Services.AddGatewayJwtAuthentication(builder.Configuration);
builder.Services.AddGatewaySecurity(builder.Configuration);
builder.Services.AddGatewayTrafficControl(builder.Configuration);
builder.Services.AddGatewayReverseProxy(builder.Configuration);
builder.Services.AddGatewayHealthChecks();

var app = builder.Build();

// Cấu hình chuỗi Middleware Pipeline cho Gateway
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseGatewayRequestLogging();
app.UseGatewayMetrics();
app.UseMiddleware<GatewayExceptionMiddleware>();
app.UseGatewaySecurity();
app.UseGatewayProxyErrorResponses();
app.UseAuthentication();
app.UseGatewayTrafficControl();
app.UseAuthorization();

HealthCheckOptions livenessOptions = new()
{
    Predicate = _ => false,
    AllowCachingResponses = false
};
app.MapHealthChecks("/health/live", livenessOptions).AllowAnonymous();
app.MapHealthChecks("/_health/live", livenessOptions).AllowAnonymous();

HealthCheckOptions readinessOptions = new()
{
    Predicate = check => check.Tags.Contains(GatewayHealthCheckTags.Readiness, StringComparer.Ordinal),
    AllowCachingResponses = false
};
app.MapHealthChecks("/health/ready", readinessOptions).AllowAnonymous();
app.MapHealthChecks("/_health/ready", readinessOptions).AllowAnonymous();
app.MapReverseProxy();

app.Run();

public partial class Program;
