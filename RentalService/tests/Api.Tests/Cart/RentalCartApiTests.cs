using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Common;
using Dprental.RentalService.Application.DTOs.Cart;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Dprental.RentalService.Api.Controllers;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Cart;

public class RentalCartApiTests : IClassFixture<RentalApiFixture>
{
    private readonly HttpClient _unauthorizedClient;
    private readonly HttpClient _renter1Client;
    private readonly HttpClient _renter2Client;
    private readonly Guid _renter1Id = Guid.NewGuid();
    private readonly RentalApiFixture _fixture;

    public RentalCartApiTests(RentalApiFixture fixture)
    {
        _fixture = fixture;
        _unauthorizedClient = fixture.CreateClient();
        _renter1Client = fixture.CreateAuthorizedClient(_renter1Id);
        _renter2Client = fixture.CreateAuthorizedClient(Guid.NewGuid());
    }

    [Fact]
    public async Task Cart_WithoutAuthentication_ReturnsUnauthorized()
    {
        using var response = await _unauthorizedClient.GetAsync("rental/cart");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CartMutationEndpoints_WithoutAuthentication_ReturnUnauthorized()
    {
        using var add = await _unauthorizedClient.PostAsJsonAsync("rental/cart/items", NewItemRequest());
        using var patch = await _unauthorizedClient.PatchAsJsonAsync(
            $"rental/cart/items/{Guid.NewGuid()}", new UpdateCartItemRequest(2, null, null));
        using var remove = await _unauthorizedClient.DeleteAsync($"rental/cart/items/{Guid.NewGuid()}");
        using var clear = await _unauthorizedClient.DeleteAsync("rental/cart");

        Assert.Equal(HttpStatusCode.Unauthorized, add.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, patch.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, remove.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, clear.StatusCode);
    }

    // Ngày tính từ hôm nay để test không hỏng khi thời gian trôi qua
    private static AddCartItemRequest NewItemRequest()
    {
        DateOnly start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7);
        return new AddCartItemRequest(Guid.NewGuid(), 1, start, start.AddDays(4));
    }

    // Báo lỗi kèm nội dung phản hồi thay vì chỉ "500 Internal Server Error"
    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"{(int)response.StatusCode} {response.StatusCode}: {body}");
        }
    }

    [Fact]
    public async Task SameRenter_TwoClients_CanReadAndModifySameCart()
    {
        using var addResponse = await _renter1Client.PostAsJsonAsync("rental/cart/items", NewItemRequest());
        await EnsureSuccessAsync(addResponse);
        using var addDoc = await addResponse.Content.ReadFromJsonAsync<JsonDocument>();
        Guid itemId = addDoc!.RootElement.GetProperty("cartItemId").GetGuid();

        // Giả lập thiết bị thứ 2 nhưng CÙNG renter1
        using var sameRenterDevice2 = _fixture.CreateAuthorizedClient(_renter1Id);
        using var patchResponse = await sameRenterDevice2.PatchAsJsonAsync(
            $"rental/cart/items/{itemId}", new UpdateCartItemRequest(3, null, null));
        Assert.Equal(HttpStatusCode.NoContent, patchResponse.StatusCode);

        using var getResponse = await _renter1Client.GetAsync("rental/cart");
        await EnsureSuccessAsync(getResponse);
        using var cartDoc = await getResponse.Content.ReadFromJsonAsync<JsonDocument>();

        Assert.NotNull(cartDoc);
        var item = Assert.Single(cartDoc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(3, item.GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task RenterIsolation_CannotReadOrModifyOtherRentersCart()
    {
        // 1. Renter 1 thêm item
        using var addResponse = await _renter1Client.PostAsJsonAsync("rental/cart/items", NewItemRequest());
        await EnsureSuccessAsync(addResponse);

        // 2. Lấy giỏ của Renter 1 để trích xuất Real ID
        using var getResponseRenter1 = await _renter1Client.GetAsync("rental/cart");
        await EnsureSuccessAsync(getResponseRenter1);
        using var cartDoc1 = await getResponseRenter1.Content.ReadFromJsonAsync<JsonDocument>();
        var itemsArray = cartDoc1!.RootElement.GetProperty("items");
        var realCartItemId = itemsArray[0].GetProperty("cartItemId").GetGuid();

        // 3. Renter 2 đọc giỏ -> Phải là mảng rỗng
        using var getResponseRenter2 = await _renter2Client.GetAsync("rental/cart");
        await EnsureSuccessAsync(getResponseRenter2);
        using var cartDoc2 = await getResponseRenter2.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Empty(cartDoc2!.RootElement.GetProperty("items").EnumerateArray());

        // 4. Renter 2 cố xóa item CÓ THẬT của Renter 1 -> Phải báo NotFound (tính cô lập)
        using var unauthorizedPatch = await _renter2Client.PatchAsJsonAsync(
            $"rental/cart/items/{realCartItemId}", new UpdateCartItemRequest(9, null, null));
        using var deleteResponse = await _renter2Client.DeleteAsync($"rental/cart/items/{realCartItemId}");
        using var clearResponse = await _renter2Client.DeleteAsync("rental/cart");
        Assert.Equal(HttpStatusCode.NotFound, unauthorizedPatch.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleteResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, clearResponse.StatusCode);

        using var finalResponse = await _renter1Client.GetAsync("rental/cart");
        await EnsureSuccessAsync(finalResponse);
        using var finalCart = await finalResponse.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Single(finalCart!.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Patch_InvalidOrPartialPayload_ReturnsBadRequestWithoutChangingCart()
    {
        using var addResponse = await _renter1Client.PostAsJsonAsync("rental/cart/items", NewItemRequest());
        await EnsureSuccessAsync(addResponse);
        using var addDoc = await addResponse.Content.ReadFromJsonAsync<JsonDocument>();
        Guid itemId = addDoc!.RootElement.GetProperty("cartItemId").GetGuid();

        using var partialDatePatch = await _renter1Client.PatchAsJsonAsync(
            $"rental/cart/items/{itemId}", new UpdateCartItemRequest(null, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(9), null));
        using var combinedPatch = await _renter1Client.PatchAsJsonAsync(
            $"rental/cart/items/{itemId}", new UpdateCartItemRequest(5, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(12)));
        using var invalidQuantityPatch = await _renter1Client.PatchAsJsonAsync(
            $"rental/cart/items/{itemId}", new UpdateCartItemRequest(0, null, null));
        using var emptyPatch = await _renter1Client.PatchAsJsonAsync(
            $"rental/cart/items/{itemId}", new UpdateCartItemRequest(null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, partialDatePatch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, combinedPatch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidQuantityPatch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, emptyPatch.StatusCode);

        using var getResponse = await _renter1Client.GetAsync("rental/cart");
        await EnsureSuccessAsync(getResponse);
        using var cart = await getResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var item = Assert.Single(cart!.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(1, item.GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task CartItem_CanUpdateDatesAndThenBeRemoved()
    {
        using var addResponse = await _renter1Client.PostAsJsonAsync("rental/cart/items", NewItemRequest());
        await EnsureSuccessAsync(addResponse);
        using var addDoc = await addResponse.Content.ReadFromJsonAsync<JsonDocument>();
        Guid itemId = addDoc!.RootElement.GetProperty("cartItemId").GetGuid();

        DateOnly updatedStart = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(12);
        DateOnly updatedEnd = updatedStart.AddDays(2);
        using var updateResponse = await _renter1Client.PatchAsJsonAsync(
            $"rental/cart/items/{itemId}", new UpdateCartItemRequest(null, updatedStart, updatedEnd));
        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

        using var getResponse = await _renter1Client.GetAsync("rental/cart");
        await EnsureSuccessAsync(getResponse);
        using var cartDoc = await getResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var item = Assert.Single(cartDoc!.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(updatedStart.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), item.GetProperty("expectedStartDate").GetString());
        Assert.Equal(updatedEnd.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), item.GetProperty("expectedEndDate").GetString());

        using var removeResponse = await _renter1Client.DeleteAsync($"rental/cart/items/{itemId}");
        Assert.Equal(HttpStatusCode.NoContent, removeResponse.StatusCode);
    }

    [Fact]
    public async Task GetCart_UsesCurrentPriceWithoutQueryParameters()
    {
        using var addResponse = await _renter1Client.PostAsJsonAsync("rental/cart/items", NewItemRequest());
        await EnsureSuccessAsync(addResponse);

        using var getResponse = await _renter1Client.GetAsync("rental/cart");
        await EnsureSuccessAsync(getResponse);
        using var cartDoc = await getResponse.Content.ReadFromJsonAsync<JsonDocument>();
        var item = Assert.Single(cartDoc!.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("AVAILABLE", item.GetProperty("status").GetString());
        Assert.Equal("ENABLED", item.GetProperty("interactionStatus").GetString());
        Assert.Equal("v1", item.GetProperty("currentPriceVersion").GetString());
    }

    [Fact]
    public async Task AddItem_InvalidQuantityOrDateRange_ReturnsBadRequest()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        using var invalidQuantity = await _renter1Client.PostAsJsonAsync(
            "rental/cart/items", new AddCartItemRequest(Guid.NewGuid(), 0, today.AddDays(2), today.AddDays(2)));
        using var invalidDates = await _renter1Client.PostAsJsonAsync(
            "rental/cart/items", new AddCartItemRequest(Guid.NewGuid(), 1, today.AddDays(3), today.AddDays(2)));

        Assert.Equal(HttpStatusCode.BadRequest, invalidQuantity.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidDates.StatusCode);
    }

    [Fact]
    public async Task ClearCart_RemovesAllItemsForCurrentRenter()
    {
        using var add1 = await _renter1Client.PostAsJsonAsync("rental/cart/items", NewItemRequest());
        using var add2 = await _renter1Client.PostAsJsonAsync("rental/cart/items", NewItemRequest());
        await EnsureSuccessAsync(add1);
        await EnsureSuccessAsync(add2);

        using var clear = await _renter1Client.DeleteAsync("rental/cart");
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);

        using var get = await _renter1Client.GetAsync("rental/cart");
        await EnsureSuccessAsync(get);
        using var cart = await get.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Empty(cart!.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Cart_RouteAliases_ReturnIdenticalCart()
    {
        using var addResponse = await _renter1Client.PostAsJsonAsync("rental/cart/items", NewItemRequest());
        await EnsureSuccessAsync(addResponse);

        using var get1 = await _renter1Client.GetAsync("rental/cart");
        using var get2 = await _renter1Client.GetAsync("api/v1/rental/cart");

        await EnsureSuccessAsync(get1);
        await EnsureSuccessAsync(get2);

        string body1 = await get1.Content.ReadAsStringAsync();
        string body2 = await get2.Content.ReadAsStringAsync();
        Assert.Equal(body1, body2);

        using var clear = await _renter1Client.DeleteAsync("rental/cart");
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
    }

    [Fact]
    public async Task AddItem_InsufficientStock_Returns409ConflictWithProblemDetails()
    {
        _fixture.Dependencies.AvailabilityHandler = (id, s, e, qty) =>
            Task.FromResult(Result.Success(new RentalAvailabilityResult(1, false)));

        try
        {
            DateOnly start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7);
            using var response = await _renter1Client.PostAsJsonAsync(
                "rental/cart/items", new AddCartItemRequest(Guid.NewGuid(), 5, start, start.AddDays(2)));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var problem = await response.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.Equal("Cart.InsufficientAvailability", problem!.RootElement.GetProperty("title").GetString());
            Assert.Contains("1", problem.RootElement.GetProperty("detail").GetString());
        }
        finally
        {
            _fixture.Dependencies.AvailabilityHandler = null;
        }
    }

    [Fact]
    public async Task AddItem_ListingDisabled_Returns409Conflict()
    {
        _fixture.Dependencies.ListingHandler = id =>
            Task.FromResult(Result.Success(new RentalListingSnapshot(id, Guid.NewGuid(), Guid.NewGuid(), false, false, 100m, 50m, "v1")));

        try
        {
            using var response = await _renter1Client.PostAsJsonAsync("rental/cart/items", NewItemRequest());
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var problem = await response.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.Equal("Cart.ListingUnavailable", problem!.RootElement.GetProperty("title").GetString());
        }
        finally
        {
            _fixture.Dependencies.ListingHandler = null;
        }
    }

    [Fact]
    public async Task AddItem_OwnListing_Returns403Forbidden()
    {
        _fixture.Dependencies.ListingHandler = id =>
            Task.FromResult(Result.Success(new RentalListingSnapshot(id, _renter1Id, Guid.NewGuid(), true, true, 100m, 50m, "v1")));

        try
        {
            using var response = await _renter1Client.PostAsJsonAsync("rental/cart/items", NewItemRequest());
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var problem = await response.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.Equal("Cart.CannotRentOwnListing", problem!.RootElement.GetProperty("title").GetString());
        }
        finally
        {
            _fixture.Dependencies.ListingHandler = null;
        }
    }

    [Fact]
    public async Task AddItem_WhenInventoryFailsWith503_Returns503WithoutLeakingMessage()
    {
        _fixture.Dependencies.ListingHandler = _ =>
            Task.FromResult(Result.Failure<RentalListingSnapshot>(Error.ServiceUnavailable("INVENTORY_UNAVAILABLE", "Không thể kiểm tra thiết bị lúc này. Vui lòng thử lại.")));

        try
        {
            using var response = await _renter1Client.PostAsJsonAsync("rental/cart/items", NewItemRequest());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var problem = await response.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.Equal("INVENTORY_UNAVAILABLE", problem!.RootElement.GetProperty("title").GetString());
            Assert.Equal("Không thể kiểm tra thiết bị lúc này. Vui lòng thử lại.", problem.RootElement.GetProperty("detail").GetString());
        }
        finally
        {
            _fixture.Dependencies.ListingHandler = null;
        }
    }

    [Fact]
    public async Task RaceCondition_ApiReturns200AtGetAnd409AtPatch()
    {
        // Setup normal add
        using var addResponse = await _renter1Client.PostAsJsonAsync("rental/cart/items", NewItemRequest());
        await EnsureSuccessAsync(addResponse);
        using var addDoc = await addResponse.Content.ReadFromJsonAsync<JsonDocument>();
        Guid itemId = addDoc!.RootElement.GetProperty("cartItemId").GetGuid();

        // Simulate race condition: listing snapshot succeeds, but availability returns NotFound (listing deleted in between)
        _fixture.Dependencies.AvailabilityHandler = (id, s, e, qty) =>
            Task.FromResult(Result.Failure<RentalAvailabilityResult>(Error.NotFound("LISTING_NOT_FOUND", "Sản phẩm không tồn tại hoặc đã bị xóa.")));

        try
        {
            // GET: returns 200, item marked UNAVAILABLE and DISABLED_READ_ONLY
            using var getResponse = await _renter1Client.GetAsync("rental/cart");
            await EnsureSuccessAsync(getResponse);
            using var cartDoc = await getResponse.Content.ReadFromJsonAsync<JsonDocument>();
            var item = Assert.Single(cartDoc!.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("UNAVAILABLE", item.GetProperty("status").GetString());
            Assert.Equal("DISABLED_READ_ONLY", item.GetProperty("interactionStatus").GetString());

            // PATCH quantity: returns 409 Cart.ListingUnavailable
            using var patchResponse = await _renter1Client.PatchAsJsonAsync(
                $"rental/cart/items/{itemId}", new UpdateCartItemRequest(5, null, null));
            Assert.Equal(HttpStatusCode.Conflict, patchResponse.StatusCode);
            using var patchProblem = await patchResponse.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.Equal("Cart.ListingUnavailable", patchProblem!.RootElement.GetProperty("title").GetString());
        }
        finally
        {
            _fixture.Dependencies.AvailabilityHandler = null;
            await _renter1Client.DeleteAsync("rental/cart");
        }
    }

}
