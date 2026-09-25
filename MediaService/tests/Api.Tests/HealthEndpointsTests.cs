using System.Net;
using Dprental.MediaService.Api.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Dprental.MediaService.Api.Tests;

public class HealthEndpointsTests
{
    [Fact]
    public async Task GetLiveness_ShouldReturnOk_WithHealthyStatus()
    {
        await using var factory = new MediaServiceTestFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", content.Trim());
    }

    [Fact]
    public async Task GetReadiness_WhenPostgresAndMinioHealthy_ShouldReturnOk()
    {
        var postgresChecker = Substitute.For<IPostgreSqlHealthChecker>();
        postgresChecker.CheckHealthAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(HealthCheckResult.Healthy("Postgres OK")));

        var minioClient = Substitute.For<IMinioClient>();
        minioClient.BucketExistsAsync(Arg.Any<BucketExistsArgs>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        await using var factory = new MediaServiceTestFactory
        {
            PostgreSqlHealthCheckerOverride = postgresChecker,
            MinioClientOverride = minioClient
        };
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", content.Trim());
    }

    [Fact]
    public async Task GetReadiness_WhenPostgresFails_ShouldReturn503ServiceUnavailable()
    {
        var postgresChecker = Substitute.For<IPostgreSqlHealthChecker>();
        postgresChecker.CheckHealthAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(HealthCheckResult.Unhealthy("Không thể kết nối đến PostgreSQL.")));

        var minioClient = Substitute.For<IMinioClient>();
        minioClient.BucketExistsAsync(Arg.Any<BucketExistsArgs>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        await using var factory = new MediaServiceTestFactory
        {
            PostgreSqlHealthCheckerOverride = postgresChecker,
            MinioClientOverride = minioClient
        };
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Unhealthy", content.Trim());
    }

    [Fact]
    public async Task GetReadiness_WhenMinioConnectionFails_ShouldReturn503ServiceUnavailable()
    {
        var postgresChecker = Substitute.For<IPostgreSqlHealthChecker>();
        postgresChecker.CheckHealthAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(HealthCheckResult.Healthy("Postgres OK")));

        var minioClient = Substitute.For<IMinioClient>();
        minioClient.BucketExistsAsync(Arg.Any<BucketExistsArgs>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused to MinIO host:port"));

        await using var factory = new MediaServiceTestFactory
        {
            PostgreSqlHealthCheckerOverride = postgresChecker,
            MinioClientOverride = minioClient
        };
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Unhealthy", content.Trim());
    }

    [Fact]
    public async Task GetReadiness_WhenMinioCredentialsInvalid_ShouldReturn503ServiceUnavailable()
    {
        var postgresChecker = Substitute.For<IPostgreSqlHealthChecker>();
        postgresChecker.CheckHealthAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(HealthCheckResult.Healthy("Postgres OK")));

        var minioClient = Substitute.For<IMinioClient>();
        minioClient.BucketExistsAsync(Arg.Any<BucketExistsArgs>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AccessDeniedException("The Access Key Id you provided does not exist in our records."));

        await using var factory = new MediaServiceTestFactory
        {
            PostgreSqlHealthCheckerOverride = postgresChecker,
            MinioClientOverride = minioClient
        };
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Unhealthy", content.Trim());
    }

    [Fact]
    public async Task GetReadiness_WhenMinioBucketDoesNotExist_ShouldReturn503ServiceUnavailable()
    {
        var postgresChecker = Substitute.For<IPostgreSqlHealthChecker>();
        postgresChecker.CheckHealthAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(HealthCheckResult.Healthy("Postgres OK")));

        var minioClient = Substitute.For<IMinioClient>();
        minioClient.BucketExistsAsync(Arg.Any<BucketExistsArgs>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        await using var factory = new MediaServiceTestFactory
        {
            PostgreSqlHealthCheckerOverride = postgresChecker,
            MinioClientOverride = minioClient
        };
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Unhealthy", content.Trim());
    }
}
