using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Dprental.Gateway.Models.Errors;
using Dprental.Gateway.Middlewares.Errors;
using Dprental.Gateway.Extensions.Errors;
using Dprental.Gateway.Models.Security;
using Dprental.Gateway.Middlewares.Security;
using Dprental.Gateway.Extensions.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Ocelot.DependencyInjection;
using Ocelot.Middleware;
namespace Dprental.Gateway.Extensions.Proxy;

/// <summary>
/// Đăng ký và cấu hình proxy Ocelot 25.0 cho API Gateway.
/// </summary>
public static class OcelotServiceCollectionExtensions
{
    /// <summary>
    /// Nạp cấu hình Ocelot cho đúng môi trường hiện tại từ thư mục Ocelot/{EnvironmentName}.
    /// Sử dụng cơ chế merge in-memory của Ocelot 25 mà không sinh file cấu hình tổng hợp trên đĩa.
    /// Đảm bảo biến môi trường và command line vẫn có độ ưu tiên cao hơn JSON.
    /// Dừng khởi động nếu thiếu thư mục môi trường, không tự fallback sang môi trường khác.
    /// </summary>
    /// <param name="builder">Đối tượng WebApplicationBuilder của ứng dụng.</param>
    public static void ConfigureGatewayOcelot(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        string envName = builder.Environment.EnvironmentName;
        string ocelotFolder = Path.Combine(builder.Environment.ContentRootPath, "Ocelot", envName);

        if (!Directory.Exists(ocelotFolder))
        {
            string baseDirFolder = Path.Combine(AppContext.BaseDirectory, "Ocelot", envName);
            if (Directory.Exists(baseDirFolder))
            {
                ocelotFolder = baseDirFolder;
            }
            else
            {
                throw new DirectoryNotFoundException(
                    $"Missing required Ocelot environment directory for environment '{envName}'. Path '{ocelotFolder}' does not exist.");
            }
        }

        // Tạm gỡ các source Memory, EnvironmentVariables và CommandLine để chèn Ocelot JSON phía trước,
        // bảo đảm biến môi trường, command line và in-memory test overrides luôn có độ ưu tiên cao hơn JSON.
        List<IConfigurationSource> higherPrioritySources = builder.Configuration.Sources
            .Where(s => s is Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource
                     || s is Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationSource
                     || s is Microsoft.Extensions.Configuration.CommandLine.CommandLineConfigurationSource)
            .ToList();

        foreach (IConfigurationSource source in higherPrioritySources)
        {
            builder.Configuration.Sources.Remove(source);
        }

        // Merge các file ocelot.*.json trong thư mục môi trường hoàn toàn trong bộ nhớ
        builder.Configuration.AddGatewayOcelotConfiguration(ocelotFolder, builder.Environment);

        // Đưa lại các nguồn có độ ưu tiên cao hơn về cuối danh sách source
        foreach (IConfigurationSource source in higherPrioritySources)
        {
            builder.Configuration.Sources.Add(source);
        }
    }

    // Ocelot merges the service files; order the result by filename and local route order
    // so overrides are portable while preserving precedence within each service.
    internal static IConfigurationBuilder AddGatewayOcelotConfiguration(
        this IConfigurationBuilder configuration, string folder, IWebHostEnvironment environment)
    {
        string mergedJson = configuration.GetMergedOcelotJson(folder, environment);
        JsonObject merged = JsonNode.Parse(mergedJson)!.AsObject();
        Dictionary<string, int> routeOrder = new(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(folder, "ocelot.*.json").Order(StringComparer.Ordinal))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("Routes", out JsonElement fileRoutes))
            {
                continue;
            }

            foreach (JsonElement route in fileRoutes.EnumerateArray())
            {
                string key = route.GetProperty("Key").GetString()!;
                if (!routeOrder.TryAdd(key, routeOrder.Count))
                {
                    throw new InvalidOperationException($"Duplicate route key '{key}' detected.");
                }
            }
        }

        JsonArray routes = merged["Routes"]!.AsArray();
        merged["Routes"] = new JsonArray(routes
            .OrderBy(route => routeOrder[route!["Key"]!.GetValue<string>()])
            .Select(route => route!.DeepClone()).ToArray());
        return configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(merged.ToJsonString())));
    }

    /// <summary>
    /// Đăng ký Ocelot và các delegating handlers cho bảo mật danh tính, correlation, origin và CORS.
    /// </summary>
    /// <param name="services">Tập hợp dịch vụ DI container.</param>
    /// <param name="configuration">Cấu hình hệ thống (IConfiguration).</param>
    /// <returns>Tập hợp dịch vụ IServiceCollection để xâu chuỗi.</returns>
    public static IServiceCollection AddGatewayOcelot(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHttpContextAccessor();
        services.AddOcelot(configuration)
            .AddDelegatingHandler<TrustedIdentityDelegatingHandler>(global: true)
            .AddDelegatingHandler<GatewayCorsDelegatingHandler>(global: true);

        return services;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Kích hoạt Ocelot middleware pipeline cho Gateway, tích hợp hook kiểm soát rate limit tại PreAuthorizationMiddleware.
    /// </summary>
    /// <param name="app">Đối tượng IApplicationBuilder xây dựng pipeline.</param>
    /// <returns>Đối tượng IApplicationBuilder để xâu chuỗi.</returns>
    public static async Task<IApplicationBuilder> UseGatewayOcelot(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        OcelotPipelineConfiguration pipelineConfiguration = new()
        {
            // Hook chạy sau xác thực của Ocelot để quota có thể dựa trên danh tính của route.
            PreAuthorizationMiddleware = async (context, next) =>
            {
                IGatewayRateLimiter rateLimiter = context.RequestServices.GetRequiredService<IGatewayRateLimiter>();
                GatewayRateLimitResult result = rateLimiter.CheckRateLimit(context);

                if (result.IsAllowed)
                {
                    await next();
                    return;
                }

                ApplyRateLimitExceededResponse(context, result);
            }
        };

        return await app.UseOcelot(pipelineConfiguration);
    }

    /// <summary>
    /// Tạo và gắn response từ chối do vượt rate limit vào luồng phản hồi của Ocelot để tránh bị responder ghi đè.
    /// Trả về HTTP 429 Too Many Requests, tiêu đề Retry-After (giây, tối thiểu 1) và JSON lỗi chuẩn Gateway.RateLimitExceeded.
    /// </summary>
    /// <param name="context">Ngữ cảnh HTTP của request.</param>
    /// <param name="result">Kết quả rate limit bị từ chối.</param>
    private static void ApplyRateLimitExceededResponse(HttpContext context, GatewayRateLimitResult result)
    {
        string retryAfter = result.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        GatewayErrorResponse errorPayload = new(
            GatewayError.RateLimitExceeded.Code,
            GatewayError.RateLimitExceeded.Message,
            context.TraceIdentifier);

        string jsonBody = JsonSerializer.Serialize(errorPayload, JsonOptions);
        StringContent content = new(jsonBody, Encoding.UTF8, "application/json");

        List<Header> headers =
        [
            new("Retry-After", [retryAfter])
        ];

        DownstreamResponse downstreamResponse = new(
            content,
            HttpStatusCode.TooManyRequests,
            headers,
            "Too Many Requests");

        // Gắn vào Items của Ocelot để ResponderMiddleware dùng làm response chính thức, tránh bị responder ghi đè
        context.Items.UpsertDownstreamResponse(downstreamResponse);

        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers[HeaderNames.RetryAfter] = retryAfter;
        }
    }

    /// <summary>
    /// Kiểm tra cấu hình Ocelot sau khi các nguồn cấu hình đã được nạp.
    /// Đảm bảo timeout dương và mọi route có đường dẫn, scheme, downstream host/port.
    /// </summary>
    /// <param name="configuration">Cấu hình hệ thống.</param>
    internal static void ValidateOcelotConfiguration(IConfiguration configuration)
    {
        string? timeoutValue = configuration["GlobalConfiguration:Timeout"];
        if (!int.TryParse(timeoutValue, out int timeoutSeconds) || timeoutSeconds <= 0)
        {
            throw new InvalidOperationException("GlobalConfiguration:Timeout must be configured and greater than 0.");
        }

        IConfigurationSection routesSection = configuration.GetSection("Routes");
        List<IConfigurationSection> routes = routesSection.GetChildren().ToList();

        if (routes.Count == 0)
        {
            throw new InvalidOperationException("Routes must contain at least one configured upstream route.");
        }
        HashSet<string> seenKeys = new(StringComparer.OrdinalIgnoreCase);

        foreach (IConfigurationSection route in routes)
        {
            string? key = route["Key"];
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException($"Route '{route["UpstreamPathTemplate"]}' must define a Key.");
            }

            if (!seenKeys.Add(key))
            {
                throw new InvalidOperationException($"Duplicate route key '{key}' detected.");
            }
            string? upstreamPathTemplate = route["UpstreamPathTemplate"];
            string? downstreamPathTemplate = route["DownstreamPathTemplate"];
            string? downstreamScheme = route["DownstreamScheme"];

            if (string.IsNullOrWhiteSpace(upstreamPathTemplate))
            {
                throw new InvalidOperationException("Each route must define an UpstreamPathTemplate.");
            }

            if (string.IsNullOrWhiteSpace(downstreamPathTemplate))
            {
                throw new InvalidOperationException($"Route '{upstreamPathTemplate}' must define a DownstreamPathTemplate.");
            }

            if (string.IsNullOrWhiteSpace(downstreamScheme))
            {
                throw new InvalidOperationException($"Route '{upstreamPathTemplate}' must define a DownstreamScheme.");
            }

            IConfigurationSection hostAndPortsSection = route.GetSection("DownstreamHostAndPorts");
            List<IConfigurationSection> hostAndPorts = hostAndPortsSection.GetChildren().ToList();

            if (hostAndPorts.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Route '{upstreamPathTemplate}' must configure at least one DownstreamHostAndPorts entry.");
            }

            foreach (IConfigurationSection hostAndPort in hostAndPorts)
            {
                string? host = hostAndPort["Host"];
                string? portValue = hostAndPort["Port"];

                if (string.IsNullOrWhiteSpace(host))
                {
                    throw new InvalidOperationException(
                        $"Route '{upstreamPathTemplate}' contains a DownstreamHostAndPorts entry without a Host.");
                }

                if (!int.TryParse(portValue, out int port) || port <= 0 || port > 65535)
                {
                    throw new InvalidOperationException(
                        $"Route '{upstreamPathTemplate}' contains an invalid Port '{portValue}'.");
                }
            }
        }
    }
}
