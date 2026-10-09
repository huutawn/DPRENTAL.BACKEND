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
        var addResponse = await _renter1Client.PostAsJsonAsync("rental/cart/items", NewItemRequest());
        await EnsureSuccessAsync(addResponse);

        // Giả lập thiết bị thứ 2 nhưng CÙNG renter1
        var sameRenterDevice2 = _fixture.CreateAuthorizedClient(_renter1Id);

        var getResponse = await sameRenterDevice2.GetAsync("rental/cart");
        await EnsureSuccessAsync(getResponse);
        using var cartDoc = await getResponse.Content.ReadFromJsonAsync<JsonDocument>();

        Assert.NotNull(cartDoc);
        Assert.Single(cartDoc.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task RenterIsolation_CannotReadOrModifyOtherRentersCart()
    {
        // 1. Renter 1 thêm item
        var addResponse = await _renter1Client.PostAsJsonAsync("rental/cart/items", NewItemRequest());
        await EnsureSuccessAsync(addResponse);

        // 2. Lấy giỏ của Renter 1 để trích xuất Real ID
        var getResponseRenter1 = await _renter1Client.GetAsync("rental/cart");
        await EnsureSuccessAsync(getResponseRenter1);
        using var cartDoc1 = await getResponseRenter1.Content.ReadFromJsonAsync<JsonDocument>();
        var itemsArray = cartDoc1!.RootElement.GetProperty("items");
        var realCartItemId = itemsArray[0].GetProperty("cartItemId").GetGuid();

        // 3. Renter 2 đọc giỏ -> Phải là mảng rỗng
        var getResponseRenter2 = await _renter2Client.GetAsync("rental/cart");
        await EnsureSuccessAsync(getResponseRenter2);
        using var cartDoc2 = await getResponseRenter2.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Empty(cartDoc2!.RootElement.GetProperty("items").EnumerateArray());

        // 4. Renter 2 cố xóa item CÓ THẬT của Renter 1 -> Phải báo NotFound (tính cô lập)
        var deleteResponse = await _renter2Client.DeleteAsync($"rental/cart/items/{realCartItemId}");
        Assert.Equal(HttpStatusCode.NotFound, deleteResponse.StatusCode);
    }

}
