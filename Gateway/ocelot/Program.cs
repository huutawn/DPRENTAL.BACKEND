using Dprental.Gateway.Authentication;
using Dprental.Gateway.Errors;
using Dprental.Gateway.Health;
using Dprental.Gateway.Proxy;
using Dprental.Gateway.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Ocelot.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Đăng ký các dịch vụ hệ thống và bảo mật
builder.Services.AddGatewayJwtAuthentication(builder.Configuration);
builder.Services.AddSingleton<ApiKeyRoutePolicy>();
builder.Services.AddGatewaySecurity(builder.Configuration);
builder.Services.AddGatewayOcelot(builder.Configuration);
builder.Services.AddGatewayHealthChecks();

var app = builder.Build();
_ = app.Services.GetRequiredService<ApiKeyRoutePolicy>();
OcelotServiceCollectionExtensions.ValidateOcelotConfiguration(app.Configuration);

// Cấu hình chuỗi Middleware Pipeline cho Gateway
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GatewayExceptionMiddleware>();
app.UseGatewaySecurity();
app.UseAuthentication();

if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(options =>
    {
        options.DocumentTitle = "DPRental API";
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "AuthService");
        options.SwaggerEndpoint("/media/swagger/v1/swagger.json", "MediaService");
        options.SwaggerEndpoint("/catalog/swagger/v1/swagger.json", "CatalogService");
        options.SwaggerEndpoint("/inventory/swagger/v1/swagger.json", "InventoryService");
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

await app.UseGatewayOcelot();

app.Run();

public partial class Program;
