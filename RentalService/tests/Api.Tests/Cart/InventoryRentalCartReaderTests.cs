using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.Services.Cart;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Cart;

/// <summary>
/// Giỏ hàng đọc thông tin máy qua API batch của Inventory (POST /equipment-listings/batch) với một mã mỗi lần.
/// </summary>
public sealed class InventoryRentalCartReaderTests
{
    private readonly Guid _listingId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly FakeInventoryListingClient _inventory = new();
    private readonly InventoryRentalCartReader _reader;

    public InventoryRentalCartReaderTests()
    {
        _inventory.Listings[_listingId] = TestListings.Create(
            _listingId, _ownerId, "Máy xúc Komatsu PC200", 3_000_000m, depositPerUnit: 20_000_000m);
        _reader = new InventoryRentalCartReader(_inventory);
    }

    [Fact]
    public async Task GetListing_AsksTheBatchApiForExactlyThatListing()
    {
        Result<RentalListingSnapshot> listing = await _reader.GetListingAsync(_listingId);

        Assert.True(listing.IsSuccess);
        Assert.Equal(_ownerId, listing.Value.OwnerId);
        Assert.Equal(1, _inventory.ListingReadCount);
        Assert.Equal([_listingId], _inventory.RequestedListingIds);
    }

    [Fact]
    public async Task GetListing_MapsPriceStatusAndDeposit()
    {
        RentalListingSnapshot listing = (await _reader.GetListingAsync(_listingId)).Value;

        Assert.Equal(_listingId, listing.ListingId);
        Assert.Equal(TestListings.EquipmentTypeId, listing.EquipmentTypeId);
        Assert.True(listing.IsPublished);
        Assert.True(listing.AcceptsBookings);
        Assert.Equal(3_000_000m, listing.DailyRate);
        Assert.Equal(20_000_000m, listing.Deposit);
        // Phiên bản giá gồm giá thuê và tiền cọc: một trong hai đổi thì phiên bản đổi.
        Assert.Equal("3000000:20000000", listing.PriceVersion);
    }

    [Fact]
    public async Task ListingInMissingIds_IsNotFound()
    {
        Result<RentalListingSnapshot> listing = await _reader.GetListingAsync(Guid.NewGuid());

        Assert.True(listing.IsFailure);
        Assert.Equal(ErrorType.NotFound, listing.Error.Type);
    }

    [Fact]
    public async Task InventoryDown_ReturnsServiceUnavailable()
    {
        _inventory.FailListingRead = true;

        Result<RentalListingSnapshot> listing = await _reader.GetListingAsync(_listingId);

        Assert.True(listing.IsFailure);
        Assert.Equal(ErrorType.ServiceUnavailable, listing.Error.Type);
    }

    [Fact]
    public async Task GetAvailableQuantity_Success_ReturnsAvailableQuantityAndIsAvailable()
    {
        _inventory.AvailableQuantities[_listingId] = 5;
        DateOnly start = new(2026, 11, 1);
        DateOnly end = new(2026, 11, 5);

        Result<RentalAvailabilityResult> result = await _reader.GetAvailableQuantityAsync(_listingId, start, end, 3);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Value.AvailableQuantity);
        Assert.True(result.Value.IsAvailable);
        Assert.Equal(1, _inventory.AvailabilityCheckCount);
    }

    [Fact]
    public async Task GetAvailableQuantity_StartDateAfterEndDate_ReturnsValidation()
    {
        DateOnly start = new(2026, 11, 5);
        DateOnly end = new(2026, 11, 1);

        Result<RentalAvailabilityResult> result = await _reader.GetAvailableQuantityAsync(_listingId, start, end, 1);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_DATES", result.Error.Code);
    }

    [Fact]
    public async Task GetAvailableQuantity_QuantityZeroOrNegative_ReturnsValidation()
    {
        DateOnly start = new(2026, 11, 1);
        DateOnly end = new(2026, 11, 5);

        Result<RentalAvailabilityResult> result0 = await _reader.GetAvailableQuantityAsync(_listingId, start, end, 0);
        Result<RentalAvailabilityResult> resultNeg = await _reader.GetAvailableQuantityAsync(_listingId, start, end, -2);

        Assert.True(result0.IsFailure);
        Assert.Equal("INVALID_QUANTITY", result0.Error.Code);
        Assert.True(resultNeg.IsFailure);
        Assert.Equal("INVALID_QUANTITY", resultNeg.Error.Code);
    }

    [Fact]
    public async Task GetAvailableQuantity_InventoryDown_ReturnsServiceUnavailable()
    {
        _inventory.FailAvailabilityCheck = true;
        DateOnly start = new(2026, 11, 1);
        DateOnly end = new(2026, 11, 5);

        Result<RentalAvailabilityResult> result = await _reader.GetAvailableQuantityAsync(_listingId, start, end, 1);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.ServiceUnavailable, result.Error.Type);
        Assert.Equal("INVENTORY_UNAVAILABLE", result.Error.Code);
        Assert.Equal("Không thể kiểm tra thiết bị lúc này. Vui lòng thử lại.", result.Error.Message);
    }

    [Theory]
    [InlineData("UPSTREAM_FAIL", ErrorType.Failure)]
    [InlineData("UPSTREAM_400", ErrorType.Validation)]
    [InlineData("UPSTREAM_409", ErrorType.Conflict)]
    [InlineData("AUTH_FAILED", ErrorType.Unauthorized)]
    [InlineData("FORBIDDEN", ErrorType.Forbidden)]
    [InlineData("INTERNAL_500", ErrorType.Failure)]
    [InlineData("DOWN_503", ErrorType.ServiceUnavailable)]
    public async Task GetListing_UpstreamErrors_NormalizedToServiceUnavailable_WithoutLeakingMessage(string code, ErrorType errorType)
    {
        const string secretMessage = "INTERNAL_SECRET_LEAK_POSTGRES_DB_PASSWORD_123";
        var stub = new DelegatingInventoryClient
        {
            SnapshotsHandler = _ => Task.FromResult(Result.Failure<ListingRentalSnapshotsResponse>(new Error(code, secretMessage, errorType)))
        };
        var reader = new InventoryRentalCartReader(stub);

        Result<RentalListingSnapshot> result = await reader.GetListingAsync(_listingId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.ServiceUnavailable, result.Error.Type);
        Assert.Equal("INVENTORY_UNAVAILABLE", result.Error.Code);
        Assert.Equal("Không thể kiểm tra thiết bị lúc này. Vui lòng thử lại.", result.Error.Message);
        Assert.DoesNotContain(secretMessage, result.Error.Message);
    }

    [Theory]
    [InlineData("UPSTREAM_FAIL", ErrorType.Failure)]
    [InlineData("UPSTREAM_400", ErrorType.Validation)]
    [InlineData("UPSTREAM_409", ErrorType.Conflict)]
    [InlineData("AUTH_FAILED", ErrorType.Unauthorized)]
    [InlineData("FORBIDDEN", ErrorType.Forbidden)]
    [InlineData("INTERNAL_500", ErrorType.Failure)]
    [InlineData("DOWN_503", ErrorType.ServiceUnavailable)]
    public async Task GetAvailableQuantity_UpstreamErrors_NormalizedToServiceUnavailable_WithoutLeakingMessage(string code, ErrorType errorType)
    {
        const string secretMessage = "INTERNAL_SECRET_LEAK_NETWORK_TRACE_URL_REDIS";
        var stub = new DelegatingInventoryClient
        {
            AvailabilityHandler = _ => Task.FromResult(Result.Failure<ListingAvailabilityCheckResponse>(new Error(code, secretMessage, errorType)))
        };
        var reader = new InventoryRentalCartReader(stub);

        Result<RentalAvailabilityResult> result = await reader.GetAvailableQuantityAsync(
            _listingId, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5), 1);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.ServiceUnavailable, result.Error.Type);
        Assert.Equal("INVENTORY_UNAVAILABLE", result.Error.Code);
        Assert.Equal("Không thể kiểm tra thiết bị lúc này. Vui lòng thử lại.", result.Error.Message);
        Assert.DoesNotContain(secretMessage, result.Error.Message);
    }

    [Fact]
    public async Task GetAvailableQuantity_NegativeAvailableQuantity_ReturnsContractInvalid()
    {
        DateOnly start = new(2026, 11, 1);
        DateOnly end = new(2026, 11, 5);
        var stub = new DelegatingInventoryClient
        {
            AvailabilityHandler = req => Task.FromResult(Result.Success(
                new ListingAvailabilityCheckResponse(req.ListingId, req.StartDate, req.EndDate, req.RequestedQuantity, -1, true)))
        };
        var reader = new InventoryRentalCartReader(stub);

        Result<RentalAvailabilityResult> result = await reader.GetAvailableQuantityAsync(_listingId, start, end, 1);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.ServiceUnavailable, result.Error.Type);
        Assert.Equal("INVENTORY_CONTRACT_INVALID", result.Error.Code);
        Assert.Equal("Không thể xác minh dữ liệu thiết bị lúc này.", result.Error.Message);
    }

    [Fact]
    public async Task GetAvailableQuantity_MismatchedParameters_ReturnsContractInvalid()
    {
        DateOnly start = new(2026, 11, 1);
        DateOnly end = new(2026, 11, 5);
        var stub = new DelegatingInventoryClient
        {
            AvailabilityHandler = req => Task.FromResult(Result.Success(
                new ListingAvailabilityCheckResponse(Guid.NewGuid(), req.StartDate, req.EndDate, req.RequestedQuantity, 5, true)))
        };
        var reader = new InventoryRentalCartReader(stub);

        Result<RentalAvailabilityResult> result = await reader.GetAvailableQuantityAsync(_listingId, start, end, 1);

        Assert.True(result.IsFailure);
        Assert.Equal("INVENTORY_CONTRACT_INVALID", result.Error.Code);
    }

    [Fact]
    public async Task GetListing_NullResponse_ReturnsContractInvalidWithoutException()
    {
        var stub = new DelegatingInventoryClient
        {
            SnapshotsHandler = _ => Task.FromResult(Result.Success<ListingRentalSnapshotsResponse>(null!))
        };
        var reader = new InventoryRentalCartReader(stub);

        Result<RentalListingSnapshot> result = await reader.GetListingAsync(_listingId);

        Assert.True(result.IsFailure);
        Assert.Equal("INVENTORY_CONTRACT_INVALID", result.Error.Code);
    }

    [Fact]
    public async Task EndToEnd_HttpClient500_ThroughAdapter_ReturnsServiceUnavailable()
    {
        var fakeHandler = new FakeHttpMessageHandler(System.Net.HttpStatusCode.InternalServerError,
            """{"title":"Internal Server Error","detail":"Raw secret db fail on host 10.0.0.5"}""");
        using var httpClient = new System.Net.Http.HttpClient(fakeHandler);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var inventoryClient = new HttpInventoryRentalClient(httpClient, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpInventoryRentalClient>.Instance);
        var reader = new InventoryRentalCartReader(inventoryClient);

        Result<RentalListingSnapshot> result = await reader.GetListingAsync(_listingId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.ServiceUnavailable, result.Error.Type);
        Assert.Equal("INVENTORY_UNAVAILABLE", result.Error.Code);
        Assert.Equal("Không thể kiểm tra thiết bị lúc này. Vui lòng thử lại.", result.Error.Message);
        Assert.DoesNotContain("10.0.0.5", result.Error.Message);
    }

    private sealed class DelegatingInventoryClient : IInventoryRentalClient
    {
        public Func<ListingRentalSnapshotsRequest, Task<Result<ListingRentalSnapshotsResponse>>>? SnapshotsHandler { get; set; }
        public Func<ListingAvailabilityCheckRequest, Task<Result<ListingAvailabilityCheckResponse>>>? AvailabilityHandler { get; set; }

        public Task<Result<ListingRentalSnapshotsResponse>> GetListingRentalSnapshotsAsync(ListingRentalSnapshotsRequest request, CancellationToken ct = default)
            => SnapshotsHandler?.Invoke(request) ?? throw new NotSupportedException();

        public Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(ListingAvailabilityCheckRequest request, CancellationToken ct = default)
            => AvailabilityHandler?.Invoke(request) ?? throw new NotSupportedException();

        public Task<Result<CreateHoldResponse>> CreateHoldAsync(CreateHoldRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeHttpMessageHandler(System.Net.HttpStatusCode statusCode, string content) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new System.Net.Http.HttpResponseMessage(statusCode)
            {
                Content = new System.Net.Http.StringContent(content, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
