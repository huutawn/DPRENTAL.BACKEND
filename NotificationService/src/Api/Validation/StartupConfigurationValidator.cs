using Microsoft.Extensions.Configuration;

namespace Dprental.NotificationService.Api.Validation;

public static class StartupConfigurationValidator
{
    public static void Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var postgres = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(postgres))
        {
            throw new InvalidOperationException("Thiếu ConnectionStrings:Postgres.");
        }

        var bootstrapServers = configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            throw new InvalidOperationException("Thiếu cấu hình Kafka:BootstrapServers.");
        }

        var topicsSection = configuration.GetSection("Kafka:Topics");
        var hasTopicsList = topicsSection.GetChildren().Any(c => !string.IsNullOrWhiteSpace(c.Value));
        var topicsStr = configuration["Kafka:Topics"];
        if (!hasTopicsList && string.IsNullOrWhiteSpace(topicsStr))
        {
            throw new InvalidOperationException("Thiếu cấu hình Kafka:Topics.");
        }

        var authUrl = configuration["Services:AuthService:BaseUrl"] ?? configuration["AuthService:BaseUrl"];
        if (string.IsNullOrWhiteSpace(authUrl))
        {
            throw new InvalidOperationException("Thiếu cấu hình Services:AuthService:BaseUrl.");
        }

        var apiKey = configuration["InternalAuthentication:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Thiếu cấu hình InternalAuthentication:ApiKey.");
        }

        var redis = configuration.GetConnectionString("Redis") ?? configuration["Redis:ConnectionString"];
        if (string.IsNullOrWhiteSpace(redis))
        {
            throw new InvalidOperationException("Thiếu cấu hình ConnectionStrings:Redis hoặc Redis:ConnectionString.");
        }

        try
        {
            var options = StackExchange.Redis.ConfigurationOptions.Parse(redis);
            if (options.EndPoints.Count == 0)
            {
                throw new InvalidOperationException("Cấu hình ConnectionStrings:Redis không chứa endpoint hợp lệ.");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Cấu hình ConnectionStrings:Redis không hợp lệ: {ex.Message}", ex);
        }
    }
}
