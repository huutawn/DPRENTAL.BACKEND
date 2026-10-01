using System.Net;
using System.Text.Json;
using Dprental.InventoryService.Api.Filters;
using Dprental.InventoryService.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.InventoryService.Api.Tests;

public sealed class CommonBoundaryTests
{
    [Fact]
    public void Result_EnforcesSuccessAndFailureInvariants()
    {
        Assert.True(Result.Success().IsSuccess);
        Assert.Equal(Error.None, Result.Success(42).Error);
        Assert.Equal(42, Result.Success(42).Value);
        Assert.Throws<ArgumentException>(() => Result.Failure(Error.None));
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
        var failure = Result.Failure<int>(Error.Conflict("CONFLICT", "Conflict"));
        Assert.True(failure.IsFailure);
        Assert.Throws<InvalidOperationException>(() => failure.Value);
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
    public void ExpectedError_MapsStatusAndPreservesCode(ErrorType type, int status)
    {
        var response = new BoundaryProbeController().ToProblem(new Error("CODE", "Message", type));
        Assert.Equal(status, response.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(response.Value);
        Assert.Equal("CODE", problem.Title);
        Assert.Equal("Message", problem.Detail);
    }

    [Fact]
    public void ExpectedError_RejectsNoError()
    {
        Assert.Throws<ArgumentException>(() => new BoundaryProbeController().ToProblem(Error.None));
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    public async Task UnexpectedException_ReturnsSafeProblemDetails(string accept)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
            builder.ConfigureTestServices(services => services.AddControllers()
                .AddApplicationPart(typeof(BoundaryProbeController).Assembly));
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        using var response = await client.GetAsync("/__tests/exception");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-token", body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("Inventory.Unexpected", json.RootElement.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
    }
}

[ApiController]
public sealed class BoundaryProbeController : ControllerBase
{
    [HttpGet("/__tests/exception")]
    public IActionResult ThrowUnexpected() => throw new InvalidOperationException($"private-token {HttpContext.TraceIdentifier}");
}
