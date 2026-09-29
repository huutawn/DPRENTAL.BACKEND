using Dprental.MediaService.Api.Configuration;
using Dprental.MediaService.Api.Options;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dprental.MediaService.Api.Tests;

public class ConfigurationValidationTests
{
    [Fact]
    public void ValidateRequiredConfiguration_WhenAllConfigurationsPresent_ShouldSucceed()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=media;Username=user;Password=pass",
                ["ObjectStorage:Endpoint"] = "http://127.0.0.1:9000",
                ["ObjectStorage:Bucket"] = "dprental-media",
                ["ObjectStorage:AccessKey"] = "access",
                ["ObjectStorage:SecretKey"] = "secret",
                ["ApiKey"] = "valid-api-key"
            })
            .Build();

        var exception = Record.Exception(() => config.ValidateRequiredConfiguration());

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateRequiredConfiguration_WhenPostgresMissingOrEmpty_ShouldThrowInvalidOperationException(string? postgresValue)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = postgresValue,
                ["ObjectStorage:Endpoint"] = "http://127.0.0.1:9000",
                ["ObjectStorage:Bucket"] = "dprental-media",
                ["ObjectStorage:AccessKey"] = "access",
                ["ObjectStorage:SecretKey"] = "secret"
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => config.ValidateRequiredConfiguration());
        Assert.Contains("ConnectionStrings:Postgres", ex.Message, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateRequiredConfiguration_WhenApiKeyMissingOrEmpty_ShouldThrowInvalidOperationException(string? apiKeyValue)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=media;Username=user;Password=pass",
                ["ObjectStorage:Endpoint"] = "http://127.0.0.1:9000",
                ["ObjectStorage:Bucket"] = "dprental-media",
                ["ObjectStorage:AccessKey"] = "access",
                ["ObjectStorage:SecretKey"] = "secret",
                ["ApiKey"] = apiKeyValue
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => config.ValidateRequiredConfiguration());
        Assert.Contains("ApiKey", ex.Message, StringComparison.Ordinal);
    }


    [Theory]
    [InlineData("ObjectStorage:Endpoint")]
    [InlineData("ObjectStorage:Bucket")]
    [InlineData("ObjectStorage:AccessKey")]
    [InlineData("ObjectStorage:SecretKey")]
    public void ValidateRequiredConfiguration_WhenSingleObjectStorageKeyMissing_ShouldThrowInvalidOperationException(string keyToOmit)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=media;Username=user;Password=pass",
            ["ObjectStorage:Endpoint"] = "http://127.0.0.1:9000",
            ["ObjectStorage:Bucket"] = "dprental-media",
            ["ObjectStorage:AccessKey"] = "access",
            ["ObjectStorage:SecretKey"] = "secret",
            ["ApiKey"] = "valid-api-key"
        };
        values.Remove(keyToOmit);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => config.ValidateRequiredConfiguration());
        Assert.Contains(keyToOmit, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectStorageOptionsValidator_WhenFieldsMissing_ShouldFail()
    {
        var validator = new ObjectStorageOptionsValidator();
        var emptyOptions = new ObjectStorageOptions();

        var result = validator.Validate(null, emptyOptions);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("Endpoint", StringComparison.Ordinal));
        Assert.Contains(result.Failures, f => f.Contains("Bucket", StringComparison.Ordinal));
        Assert.Contains(result.Failures, f => f.Contains("AccessKey", StringComparison.Ordinal));
        Assert.Contains(result.Failures, f => f.Contains("SecretKey", StringComparison.Ordinal));
    }

    [Fact]
    public void ObjectStorageOptionsValidator_WhenValid_ShouldSucceed()
    {
        var validator = new ObjectStorageOptionsValidator();
        var validOptions = new ObjectStorageOptions
        {
            Endpoint = "http://localhost:9000",
            Bucket = "media",
            AccessKey = "key",
            SecretKey = "secret"
        };

        var result = validator.Validate(null, validOptions);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(901)]
    public void ObjectStorageOptionsValidator_WhenReadUrlExpiryOutOfRange_ShouldFail(int seconds)
    {
        var options = new ObjectStorageOptions
        {
            Endpoint = "http://localhost:9000",
            Bucket = "media",
            AccessKey = "key",
            SecretKey = "secret",
            ReadUrlExpirySeconds = seconds
        };

        var result = new ObjectStorageOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("ReadUrlExpirySeconds", StringComparison.Ordinal));
    }
}
