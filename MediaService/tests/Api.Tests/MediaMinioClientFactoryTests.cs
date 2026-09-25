using Dprental.MediaService.Api.Health;
using Dprental.MediaService.Api.Options;
using Xunit;

namespace Dprental.MediaService.Api.Tests;

public class MediaMinioClientFactoryTests
{
    [Theory]
    [InlineData("127.0.0.1:9000")]
    [InlineData("http://127.0.0.1:9000")]
    [InlineData("http://localhost:9000")]
    [InlineData("https://s3.example.com")]
    public void Create_WithValidEndpoint_ShouldReturnMinioClientInstance(string endpoint)
    {
        var options = new ObjectStorageOptions
        {
            Endpoint = endpoint,
            Bucket = "dprental-media",
            AccessKey = "access",
            SecretKey = "secret"
        };

        var client = MediaMinioClientFactory.Create(options);

        Assert.NotNull(client);
    }

    [Fact]
    public void Create_WhenOptionsNull_ShouldThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => MediaMinioClientFactory.Create(null!));
    }
}
