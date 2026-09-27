using Xunit;

namespace Dprental.MediaService.Api.Tests;

public class StartupFailureTests
{
    [Fact]
    public void Startup_WhenPostgresConnectionStringMissing_ShouldFailStartup()
    {
        using var factory = new MediaServiceTestFactory();
        factory.ConfigurationOverrides["ConnectionStrings:Postgres"] = "";

        var exception = Assert.ThrowsAny<Exception>(() =>
        {
            _ = factory.CreateClient();
        });

        Assert.Contains("ConnectionStrings:Postgres", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ObjectStorage:Endpoint")]
    [InlineData("ObjectStorage:Bucket")]
    [InlineData("ObjectStorage:AccessKey")]
    [InlineData("ObjectStorage:SecretKey")]
    public void Startup_WhenObjectStorageKeyMissing_ShouldFailStartup(string missingKey)
    {
        using var factory = new MediaServiceTestFactory();
        factory.ConfigurationOverrides[missingKey] = "";

        var exception = Assert.ThrowsAny<Exception>(() =>
        {
            _ = factory.CreateClient();
        });

        Assert.Contains(missingKey, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Jwt:Issuer")]
    [InlineData("Jwt:Audience")]
    [InlineData("Jwt:JwksUri")]
    public void Startup_WhenJwtTrustSettingMissing_ShouldFailStartup(string missingKey)
    {
        using var factory = new MediaServiceTestFactory();
        factory.ConfigurationOverrides[missingKey] = "";

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Jwt:", exception.ToString(), StringComparison.Ordinal);
    }
}
