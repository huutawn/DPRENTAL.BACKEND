using System.Text.Json;
using Dprental.InventoryService.Api.Controllers;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class PublicInventoryAssetsControllerTests
{
    private sealed class StubPublicService : IPublicInventoryAssetService
    {
        public Func<ListPublicAssetsRequest, Task<Result<PagedResponse<PublicInventoryAssetResponse>>>>? ListHandler { get; set; }
        public Func<Guid, Task<Result<PublicInventoryAssetResponse>>>? GetHandler { get; set; }

        public Task<Result<PagedResponse<PublicInventoryAssetResponse>>> ListPublicAssetsAsync(
            ListPublicAssetsRequest request,
            CancellationToken cancellationToken = default) =>
            ListHandler != null
                ? ListHandler(request)
                : Task.FromResult(Result.Success(new PagedResponse<PublicInventoryAssetResponse>([], 1, 20, 0, 0)));

        public Task<Result<PublicInventoryAssetResponse>> GetPublicAssetByIdAsync(
            Guid assetId,
            CancellationToken cancellationToken = default) =>
            GetHandler != null
                ? GetHandler(assetId)
                : Task.FromResult(Result.Failure<PublicInventoryAssetResponse>(Error.NotFound("ASSET_NOT_FOUND", "Tài sản không tồn tại.")));
    }

    private readonly StubPublicService _stubService = new();
    private readonly PublicInventoryAssetsController _controller;

    public PublicInventoryAssetsControllerTests()
    {
        _controller = new PublicInventoryAssetsController(_stubService);
        var httpContext = new DefaultHttpContext();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    [Fact]
    public async Task List_DuplicateQueryParameter_Returns400()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["q"] = new StringValues(["a", "b"])
        });
        _controller.Request.Query = query;

        var result = await _controller.List(new ListPublicAssetsRequest());

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, obj.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal("INVALID_QUERY_PARAMETER", problem.Title);
    }

    [Theory]
    [InlineData("ownerUserId")]
    [InlineData("serialNumber")]
    [InlineData("suspensionReason")]
    [InlineData("acceptingBookings")]
    [InlineData("unknown_param")]
    public async Task List_DisallowedQueryParameter_Returns400(string disallowedKey)
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            [disallowedKey] = new StringValues("value")
        });
        _controller.Request.Query = query;

        var result = await _controller.List(new ListPublicAssetsRequest());

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, obj.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal("INVALID_QUERY_PARAMETER", problem.Title);
    }

    [Fact]
    public async Task List_FromWithoutTo_Returns400()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["from"] = new StringValues("2026-10-06")
        });
        _controller.Request.Query = query;

        var result = await _controller.List(new ListPublicAssetsRequest(From: new DateOnly(2026, 10, 6)));

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, obj.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal("INVALID_QUERY_PARAMETER", problem.Title);
        Assert.Contains("đồng thời", problem.Detail);
    }

    [Fact]
    public async Task List_EmptyFromDateString_Returns400()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["from"] = new StringValues(""),
            ["to"] = new StringValues("2026-10-06")
        });
        _controller.Request.Query = query;

        var result = await _controller.List(new ListPublicAssetsRequest());

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, obj.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal("INVALID_QUERY_PARAMETER", problem.Title);
        Assert.Contains("không được để trống", problem.Detail);
    }

    [Fact]
    public async Task List_ValidQuery_ReturnsOkWithPagedResult()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["q"] = new StringValues("forklift")
        });
        _controller.Request.Query = query;

        _stubService.ListHandler = _ => Task.FromResult(Result.Success(new PagedResponse<PublicInventoryAssetResponse>([], 1, 20, 0, 0)));

        var result = await _controller.List(new ListPublicAssetsRequest(Q: "forklift"));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(StatusCodes.Status200OK, ok.StatusCode);
    }

    [Fact]
    public async Task GetById_AssetFound_ReturnsOk()
    {
        var assetId = Guid.NewGuid();
        using var doc = JsonDocument.Parse("{}");
        var response = new PublicInventoryAssetResponse(
            assetId, Guid.NewGuid(), null, "M", 2024, 1, doc.RootElement.Clone(),
            new PublicAssetRentalRateResponse(100_000m, null, null, "VND"), null, true);

        _stubService.GetHandler = id => Task.FromResult(Result.Success(response));

        var result = await _controller.GetById(assetId);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(StatusCodes.Status200OK, ok.StatusCode);
    }

    [Fact]
    public async Task GetById_AssetNotFound_Returns404ProblemDetails()
    {
        var result = await _controller.GetById(Guid.NewGuid());

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, obj.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal("ASSET_NOT_FOUND", problem.Title);
    }
}
