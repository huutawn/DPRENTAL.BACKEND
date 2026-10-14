using System.Text.Json;
using Dprental.InventoryService.Api.Controllers;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Search;
using Dprental.InventoryService.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class PublicEquipmentListingsControllerTests
{
    private sealed class StubPublicListingService : IPublicEquipmentListingService
    {
        public Func<ListPublicEquipmentListingsRequest, Task<Result<SearchExecution<PagedResponse<PublicEquipmentListingResponse>>>>>? ListHandler { get; set; }
        public Func<Guid, Task<Result<PublicEquipmentListingResponse>>>? GetHandler { get; set; }

        public Task<Result<SearchExecution<PagedResponse<PublicEquipmentListingResponse>>>> ListPublicListingsAsync(
            ListPublicEquipmentListingsRequest request,
            CancellationToken cancellationToken = default) =>
            ListHandler != null
                ? ListHandler(request)
                : Task.FromResult(Result.Success(new SearchExecution<PagedResponse<PublicEquipmentListingResponse>>(new PagedResponse<PublicEquipmentListingResponse>([], 1, 20, 0, 0), "postgres")));

        public Task<Result<PublicEquipmentListingResponse>> GetPublicListingByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            GetHandler != null
                ? GetHandler(id)
                : Task.FromResult(Result.Failure<PublicEquipmentListingResponse>(Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.")));

        public Func<Task<Result<EquipmentListingStatisticsResponse>>>? StatisticsHandler { get; set; }

        public Task<Result<EquipmentListingStatisticsResponse>> GetStatisticsAsync(
            CancellationToken cancellationToken = default) =>
            StatisticsHandler != null
                ? StatisticsHandler()
                : Task.FromResult(Result.Success(new EquipmentListingStatisticsResponse(
                    new Dictionary<Guid, int>(),
                    new Dictionary<Guid, int>())));
    }

    private readonly StubPublicListingService _stubService = new();
    private readonly PublicEquipmentListingsController _controller;
    private static readonly string[] DuplicateQValues = ["FX3", "FX6"];

    public PublicEquipmentListingsControllerTests()
    {
        _controller = new PublicEquipmentListingsController(_stubService);
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
            ["q"] = new StringValues(DuplicateQValues)
        });
        _controller.HttpContext.Request.Query = query;

        var result = await _controller.List(new ListPublicEquipmentListingsRequest());
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
    }

    [Fact]
    public async Task List_InvalidQueryParameter_ManufactureYear_Returns400()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["manufactureYearFrom"] = "2024"
        });
        _controller.HttpContext.Request.Query = query;

        var result = await _controller.List(new ListPublicEquipmentListingsRequest());
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
    }

    [Fact]
    public async Task List_FromWithoutTo_Returns400()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["from"] = "2026-10-10"
        });
        _controller.HttpContext.Request.Query = query;

        var result = await _controller.List(new ListPublicEquipmentListingsRequest { From = new DateOnly(2026, 10, 10) });
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
    }

    [Fact]
    public async Task List_RequestedQuantityWithoutDates_Returns400()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["requestedQuantity"] = "2"
        });
        _controller.HttpContext.Request.Query = query;

        var result = await _controller.List(new ListPublicEquipmentListingsRequest { RequestedQuantity = 2 });
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetById_NotFound_Returns404()
    {
        var id = Guid.NewGuid();
        var result = await _controller.GetById(id, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetById_Found_Returns200()
    {
        var id = Guid.NewGuid();
        using var doc = JsonDocument.Parse("{}");
        var response = new PublicEquipmentListingResponse(
            id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Name X",
            "Desc X",
            "Model X",
            2024,
            1,
            doc.RootElement.Clone(),
            new RentalRateDto(100_000m, null, null, "VND"),
            [],
            null,
            5,
            3);

        _stubService.GetHandler = _ => Task.FromResult(Result.Success(response));

        var result = await _controller.GetById(id, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(response, okResult.Value);
    }

    [Fact]
    public async Task GetStatistics_NoQueryParameters_Returns200()
    {
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var typeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var stats = new EquipmentListingStatisticsResponse(
            new Dictionary<Guid, int> { [typeId] = 5 },
            new Dictionary<Guid, int> { [brandId] = 5 });

        _stubService.StatisticsHandler = () => Task.FromResult(Result.Success(stats));

        var result = await _controller.GetStatistics(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);
        var response = Assert.IsType<EquipmentListingStatisticsResponse>(okResult.Value);
        Assert.Equal(5, response.ByEquipmentType[typeId]);
        Assert.Equal(5, response.ByBrand[brandId]);
    }
}
