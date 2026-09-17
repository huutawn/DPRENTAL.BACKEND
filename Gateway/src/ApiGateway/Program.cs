using Dprental.Gateway.Authentication;
using Dprental.Gateway.Errors;
using Dprental.Gateway.Health;
using Dprental.Gateway.Observability;
using Dprental.Gateway.Proxy;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGatewayJwtAuthentication(builder.Configuration);
builder.Services.AddGatewayReverseProxy(builder.Configuration);
builder.Services.AddGatewayHealthChecks();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GatewayExceptionMiddleware>();
app.UseGatewayProxyErrorResponses();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/_health/live", new HealthCheckOptions
    {
        Predicate = _ => false,
        AllowCachingResponses = false
    })
    .AllowAnonymous();
app.MapHealthChecks("/_health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains(GatewayHealthCheckTags.Readiness, StringComparer.Ordinal),
        AllowCachingResponses = false
    })
    .AllowAnonymous();
app.MapReverseProxy();

app.Run();

public partial class Program;
