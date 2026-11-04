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
    }
}
