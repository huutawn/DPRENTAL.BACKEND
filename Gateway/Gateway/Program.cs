using Dprental.Gateway.Models.Authentication;
using Dprental.Gateway.Extensions.Authentication;
using Dprental.Gateway.Models.Errors;
using Dprental.Gateway.Middlewares.Errors;
using Dprental.Gateway.Extensions.Errors;
using Dprental.Gateway.Extensions.Health;
using Dprental.Gateway.Extensions.Proxy;
using Dprental.Gateway.Models.Security;
using Dprental.Gateway.Middlewares.Security;
using Dprental.Gateway.Extensions.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Ocelot.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Nạp cấu hình Ocelot theo thư mục Ocelot/{EnvironmentName} trước khi đăng ký authentication, security và Ocelot
builder.ConfigureGatewayOcelot();

// Đăng ký các dịch vụ hệ thống và bảo mật
builder.Services.AddGatewayJwtAuthentication(builder.Configuration);
builder.Services.AddSingleton<ApiKeyRoutePolicy>();
builder.Services.AddGatewaySecurity(builder.Configuration);
builder.Services.AddGatewayOcelot(builder.Configuration);
builder.Services.AddGatewayHealthChecks();

var app = builder.Build();
// Khởi tạo policy ngay để phát hiện cấu hình route API key sai trước khi nhận traffic.
_ = app.Services.GetRequiredService<ApiKeyRoutePolicy>();
OcelotServiceCollectionExtensions.ValidateOcelotConfiguration(app.Configuration);

// Thứ tự middleware là thứ tự xử lý request; phản hồi đi ngược lại chuỗi này.
// Gắn mã truy vết trước để cả lỗi bảo mật và lỗi nội bộ đều có cùng TraceId.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GatewayExceptionMiddleware>();
app.UseGatewaySecurity();
// Xác thực trước khi kiểm tra quyền truy cập hub và route dành riêng cho service.
app.UseAuthentication();
app.UseMiddleware<ServiceOnlyRouteBlockingMiddleware>();
if (app.Environment.IsDevelopment())
{
    // Giao diện tập hợp tài liệu từ các service, chỉ bật trong môi trường Development.
    app.UseSwaggerUI(options =>
    {
        options.DocumentTitle = "DPRental API";
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "AuthService");
        options.SwaggerEndpoint("/media/swagger/v1/swagger.json", "MediaService");
        options.SwaggerEndpoint("/catalog/swagger/v1/swagger.json", "CatalogService");
        options.SwaggerEndpoint("/inventory/swagger/v1/swagger.json", "InventoryService");
        options.SwaggerEndpoint("/rental/swagger/v1/swagger.json", "RentalService");
        options.SwaggerEndpoint("/api/v1/payment/swagger/v1/swagger.json", "PaymentService");
        options.SwaggerEndpoint("/analytics/swagger/v1/swagger.json", "AnalyticsService");
        options.SwaggerEndpoint("/notifications/swagger/v1/swagger.json", "NotificationService");
        options.SwaggerEndpoint("/review/swagger/v1/swagger.json", "ReviewService");
    });
}

// Liveness chỉ xác nhận tiến trình còn phản hồi, không phụ thuộc AuthService hoặc route.
HealthCheckOptions livenessOptions = new()
{
    Predicate = _ => false,
    AllowCachingResponses = false
};
app.UseHealthChecks("/health/live", livenessOptions);
app.UseHealthChecks("/_health/live", livenessOptions);

// Readiness chạy các kiểm tra mang nhãn ready; không cache kết quả để probe thấy trạng thái hiện tại.
HealthCheckOptions readinessOptions = new()
{
    Predicate = check => check.Tags.Contains(GatewayHealthCheckTags.Readiness, StringComparer.Ordinal),
    AllowCachingResponses = false
};
app.UseHealthChecks("/health/ready", readinessOptions);
app.UseHealthChecks("/_health/ready", readinessOptions);

// Đặt proxy cuối chuỗi: request đã qua bảo mật và không phải health/Swagger mới đến Ocelot.
await app.UseGatewayOcelot();

app.Run();

// Cho phép bộ kiểm thử tích hợp truy cập điểm khởi động qua WebApplicationFactory<Program>.
public partial class Program;
