using System.Net;
using System.Diagnostics.CodeAnalysis;
using Dprental.__SERVICE_NAME__.Api.Filters;
using Dprental.__SERVICE_NAME__.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.__SERVICE_NAME__.Api.Tests;

public sealed class CommonBoundaryTests
{
    [Fact]
    public void Result_EnforcesSuccessAndFailureInvariants()
    {
        Assert.True(Result.Success().IsSuccess);
        Assert.Equal(42, Result.Success(42).Value);
        Error error = Error.Validation("Invalid", "Invalid input");
        Result<int> failure = Result.Failure<int>(error);
        Assert.True(failure.IsFailure);
        Assert.Equal(error, failure.Error);
        Assert.Throws<InvalidOperationException>(() => failure.Value);
        Assert.Throws<ArgumentException>(() => Result.Failure(Error.None));
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
    }

    [Theory]
    [InlineData(ErrorType.Validation, 400)]
    [InlineData(ErrorType.Unauthorized, 401)]
    [InlineData(ErrorType.Forbidden, 403)]
    [InlineData(ErrorType.NotFound, 404)]
    [InlineData(ErrorType.Conflict, 409)]
    [InlineData(ErrorType.Expired, 410)]
    [InlineData(ErrorType.Unprocessable, 422)]
    [InlineData(ErrorType.Failure, 500)]
    public void ExpectedError_MapsToHttpStatus(ErrorType type, int status)
    {
        BoundaryTestController controller = new();
        ObjectResult response = controller.ToProblem(new Error("Test.Error", "Expected failure", type));
        Assert.Equal(status, response.StatusCode);
        ProblemDetails problem = Assert.IsType<ProblemDetails>(response.Value);
        Assert.Equal("Test.Error", problem.Title);
        Assert.Equal("Expected failure", problem.Detail);
        Assert.Throws<ArgumentException>(() => controller.ToProblem(Error.None));
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    public async Task UnexpectedException_ReturnsSafeProblemDetails(string accept)
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("environment", "Production");
                builder.UseSetting("ConnectionStrings:Postgres",
                    "Host=localhost;Database=test;Username=test;Password=test");
                builder.ConfigureTestServices(services => services.AddControllers()
                    .AddApplicationPart(typeof(BoundaryTestController).Assembly));
            });
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        using HttpResponseMessage response = await client.GetAsync("/test-boundary/throw");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("__SERVICE_NAME__.Unexpected", body, StringComparison.Ordinal);
        Assert.Contains("traceId", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret", body, StringComparison.Ordinal);
    }
}

[ApiController]
[Route("test-boundary")]
public sealed class BoundaryTestController : ControllerBase
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "MVC requires instance controller actions.")]
    [HttpGet("throw")]
    public IActionResult Throw() => throw new InvalidOperationException("private-secret");
}
