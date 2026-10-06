using System.ComponentModel.DataAnnotations;
using Dprental.InventoryService.Domain.Clients.Catalog;
using Dprental.InventoryService.Domain.Clients.Media;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Media;

/// <summary>
/// Kiểm tra quy tắc validation và cấu hình MediaServiceOptions khi khởi động ứng dụng.
/// </summary>
public sealed class MediaServiceOptionsTests
{
    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class FakeServiceProvider(IHostEnvironment env) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IHostEnvironment) ? env : null;
    }

    [Fact]
    public void ValidOptions_PassesValidation()
    {
        var options = new MediaServiceOptions
        {
            InternalBaseUrl = "http://media-service:8080",
            PublicGatewayUrl = "http://localhost:5050",
            ApiKey = "secret-key",
            TimeoutSeconds = 10
        };

        var context = new ValidationContext(options);
        var results = options.Validate(context);

        Assert.Empty(results);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-valid-url")]
    [InlineData("ftp://media-service:8080")]
    public void InvalidInternalBaseUrl_FailsValidation(string invalidUrl)
    {
        var options = new MediaServiceOptions
        {
            InternalBaseUrl = invalidUrl,
            PublicGatewayUrl = "http://localhost:5050",
            ApiKey = "secret-key",
            TimeoutSeconds = 10
        };

        var context = new ValidationContext(options);
        var results = options.Validate(context);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(MediaServiceOptions.InternalBaseUrl)));
    }

    [Theory]
    [InlineData("http://user:pass@media-service:8080", "credentials")]
    [InlineData("http://media-service:8080?query=1", "query")]
    [InlineData("http://media-service:8080#fragment", "fragment")]
    public void InternalBaseUrl_WithCredentialsOrQueryOrFragment_FailsValidation(string url, string reason)
    {
        var options = new MediaServiceOptions
        {
            InternalBaseUrl = url,
            PublicGatewayUrl = "http://localhost:5050",
            ApiKey = "secret-key",
            TimeoutSeconds = 10
        };

        var context = new ValidationContext(options);
        var results = options.Validate(context);

        Assert.True(results.Any(r => r.MemberNames.Contains(nameof(MediaServiceOptions.InternalBaseUrl))), $"Expected failure for {reason}");
    }

    [Theory]
    [InlineData("http://user:pass@gateway.example:5050", "credentials")]
    [InlineData("http://gateway.example:5050?foo=bar", "query")]
    [InlineData("http://gateway.example:5050#frag", "fragment")]
    public void PublicGatewayUrl_WithCredentialsOrQueryOrFragment_FailsValidation(string url, string reason)
    {
        var options = new MediaServiceOptions
        {
            InternalBaseUrl = "http://media-service:8080",
            PublicGatewayUrl = url,
            ApiKey = "secret-key",
            TimeoutSeconds = 10
        };

        var context = new ValidationContext(options);
        var results = options.Validate(context);

        Assert.True(results.Any(r => r.MemberNames.Contains(nameof(MediaServiceOptions.PublicGatewayUrl))), $"Expected failure for {reason}");
    }

    [Fact]
    public void PublicGatewayUrl_InProduction_RequiresHttps()
    {
        var options = new MediaServiceOptions
        {
            InternalBaseUrl = "http://media-service:8080",
            PublicGatewayUrl = "http://gateway.example",
            ApiKey = "secret-key",
            TimeoutSeconds = 10
        };

        var env = new FakeHostEnvironment(Environments.Production);
        var sp = new FakeServiceProvider(env);

        var context = new ValidationContext(options, sp, null);
        var results = options.Validate(context);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(MediaServiceOptions.PublicGatewayUrl)) &&
                                      r.ErrorMessage?.Contains("HTTPS", StringComparison.OrdinalIgnoreCase) == true);

        // Đổi sang HTTPS -> Thành công
        options.PublicGatewayUrl = "https://gateway.example";
        var validResults = options.Validate(context);
        Assert.Empty(validResults);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyApiKey_FailsValidation(string emptyKey)
    {
        var options = new MediaServiceOptions
        {
            InternalBaseUrl = "http://media-service:8080",
            PublicGatewayUrl = "http://localhost:5050",
            ApiKey = emptyKey,
            TimeoutSeconds = 10
        };

        var context = new ValidationContext(options);
        var results = options.Validate(context);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(MediaServiceOptions.ApiKey)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TimeoutSeconds_ZeroOrNegative_FailsValidation(int invalidTimeout)
    {
        var options = new MediaServiceOptions
        {
            InternalBaseUrl = "http://media-service:8080",
            PublicGatewayUrl = "http://localhost:5050",
            ApiKey = "secret-key",
            TimeoutSeconds = invalidTimeout
        };

        var context = new ValidationContext(options);
        var results = options.Validate(context);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(MediaServiceOptions.TimeoutSeconds)));
    }
}
