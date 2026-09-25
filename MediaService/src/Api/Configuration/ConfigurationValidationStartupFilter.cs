using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Dprental.MediaService.Api.Configuration;

/// <summary>
/// Startup filter xác thực cấu hình bắt buộc khi WebApplication bắt đầu khởi động.
/// </summary>
public sealed class ConfigurationValidationStartupFilter : IStartupFilter
{
    private readonly IConfiguration _configuration;

    public ConfigurationValidationStartupFilter(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        _configuration.ValidateRequiredConfiguration();
        return next;
    }
}
