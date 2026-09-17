using System.Text.Json;
using Dprental.Gateway.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.Gateway.Tests;

public sealed class GatewayExceptionMiddlewareTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task UnhandledExceptionReturnsCanonicalErrorWithoutLeakingExceptionDetails()
    {
        DefaultHttpContext context = new()
        {
            TraceIdentifier = "trace-123"
        };
        context.Response.Body = new MemoryStream();
        GatewayExceptionMiddleware middleware = new(
            static _ => Task.FromException(new InvalidOperationException("sensitive implementation detail")),
            NullLogger<GatewayExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        GatewayErrorResponse? response = await JsonSerializer.DeserializeAsync<GatewayErrorResponse>(
            context.Response.Body,
            JsonOptions);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.NotNull(response);
        Assert.Equal("Gateway.UnexpectedFailure", response.Code);
        Assert.Equal("trace-123", response.TraceId);
        Assert.DoesNotContain("sensitive", response.Message, StringComparison.OrdinalIgnoreCase);
    }
}
