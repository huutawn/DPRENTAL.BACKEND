using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Orders;

/// <summary>
/// IDOR: lấy mã của người khác để đọc hoặc thao tác phải nhận đúng phản hồi như khi mã không tồn tại,
/// để không dò được mã nào có thật. Quyền do RentalAuthorizationPolicy quyết định, không phải từng controller.
/// </summary>
public sealed class OwnershipIdorTests
{
    private const string Root = "/api/v1/rental";

    [Fact]
    public async Task Renter_ReadingAnotherRentersOrder_GetsTheSameResponseAsAnUnknownOrder()
    {
        using OrderApiFactory factory = new();
        RentalOrder order = factory.SeedPaidMixedOrder();
        using HttpClient stranger = factory.CreateClientFor(Guid.NewGuid());
        using HttpClient owner = factory.CreateClientFor(factory.RenterId);

        using HttpResponseMessage foreign = await stranger.GetAsync($"{Root}/orders/{order.Id}");
        using HttpResponseMessage unknown = await stranger.GetAsync($"{Root}/orders/{Guid.NewGuid()}");
        using HttpResponseMessage own = await owner.GetAsync($"{Root}/orders/{order.Id}");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(await Describe(unknown), await Describe(foreign));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task Supplier_ReadingAnotherSuppliersSubOrder_GetsTheSameResponseAsAnUnknownSubOrder()
    {
        using OrderApiFactory factory = new();
        RentalOrder order = factory.SeedPaidMixedOrder();
        RentalSubOrder binhs = order.SubOrders.Single(s => s.SupplierId == factory.SupplierBinh);
        using HttpClient cuong = factory.CreateClientFor(factory.SupplierCuong);
        using HttpClient binh = factory.CreateClientFor(factory.SupplierBinh);

        using HttpResponseMessage foreign = await cuong.GetAsync($"{Root}/supplier/sub-orders/{binhs.Id}");
        using HttpResponseMessage unknown = await cuong.GetAsync($"{Root}/supplier/sub-orders/{Guid.NewGuid()}");
        using HttpResponseMessage own = await binh.GetAsync($"{Root}/supplier/sub-orders/{binhs.Id}");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(await Describe(unknown), await Describe(foreign));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Theory]
    [InlineData("accept-sub-order")]
    [InlineData("reject-sub-order")]
    [InlineData("handover-item")]
    [InlineData("return-item")]
    [InlineData("deduct-item")]
    public async Task Supplier_ActingOnAnotherSuppliersResource_GetsTheSameResponseAsAnUnknownOne_AndChangesNothing(string action)
    {
        using OrderApiFactory factory = new();
        RentalOrder order = factory.SeedPaidMixedOrder();
        RentalSubOrder binhs = order.SubOrders.Single(s => s.SupplierId == factory.SupplierBinh);
        RentalItem binhsItem = binhs.Items.Single();
        using HttpClient cuong = factory.CreateClientFor(factory.SupplierCuong);
        using HttpClient binh = factory.CreateClientFor(factory.SupplierBinh);
        cuong.DefaultRequestHeaders.Add("Idempotency-Key", "idor-cuong");
        binh.DefaultRequestHeaders.Add("Idempotency-Key", "idor-binh-control");

        (string foreignPath, string unknownPath, object body) = Build(action, binhs.Id, binhsItem);

        using HttpResponseMessage foreign = await cuong.PostAsJsonAsync(foreignPath, body);
        using HttpResponseMessage unknown = await cuong.PostAsJsonAsync(unknownPath, body);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(await Describe(unknown), await Describe(foreign));

        // Đối chứng: chính chủ không bị chặn ở bước quyền (có thể bị chặn vì trạng thái, nhưng không phải NotFound).
        if (action is not ("accept-sub-order" or "reject-sub-order"))
        {
            using HttpResponseMessage own = await binh.PostAsJsonAsync(foreignPath, body);
            Assert.NotEqual((await Describe(foreign)).Title, (await Describe(own)).Title);
        }

        // Thao tác của người lạ không được đổi dữ liệu.
        using IServiceScope scope = factory.Services.CreateScope();
        RentalServiceDbContext context = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
        RentalItem stored = context.RentalItems.Single(i => i.Id == binhsItem.Id);
        Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, stored.Status);
        Assert.Empty(context.RentalOperationReceipts.Where(r => r.OperationId.Contains("idor-cuong")));
    }

    [Fact]
    public async Task Supplier_CanAcceptOwnSubOrder_WhileTheOtherSuppliersPartStaysUntouched()
    {
        using OrderApiFactory factory = new();
        RentalOrder order = factory.SeedPaidMixedOrder();
        RentalSubOrder binhs = order.SubOrders.Single(s => s.SupplierId == factory.SupplierBinh);
        using HttpClient binh = factory.CreateClientFor(factory.SupplierBinh);
        binh.DefaultRequestHeaders.Add("Idempotency-Key", "idor-control");

        using HttpResponseMessage reject = await binh.PostAsJsonAsync(
            $"{Root}/supplier/sub-orders/{binhs.Id}/reject", new { rejectReason = "Máy đang bảo trì" });

        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        using IServiceScope scope = factory.Services.CreateScope();
        RentalServiceDbContext context = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
        Assert.Equal(
            RentalItemStatus.PendingSupplierConfirmation,
            context.RentalItems.Single(i => i.SupplierId == factory.SupplierCuong).Status);
    }

    [Fact]
    public async Task OperationReceipts_AreVisibleOnlyToParticipantsAndAdmins_StrangersGetTheUnknownOrderResponse()
    {
        using OrderApiFactory factory = new();
        RentalOrder order = factory.SeedPaidMixedOrder();
        string route = $"{Root}/internal/orders/{order.Id}/operations/op-1";
        using HttpClient stranger = factory.CreateClientFor(Guid.NewGuid());

        using HttpResponseMessage foreign = await stranger.GetAsync(route);
        using HttpResponseMessage unknownOrder = await stranger.GetAsync($"{Root}/internal/orders/{Guid.NewGuid()}/operations/op-1");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(await Describe(unknownOrder), await Describe(foreign));
        Assert.Equal("Order.NotFound", (await Describe(foreign)).Title);

        // Người thuê, supplier có dòng trong đơn và Admin vượt qua bước quyền; chỉ tác vụ không có.
        foreach (HttpClient participant in new[]
                 {
                     factory.CreateClientFor(factory.RenterId),
                     factory.CreateClientFor(factory.SupplierBinh),
                     factory.CreateClientFor(Guid.NewGuid(), roles: "admin")
                 })
        {
            using (participant)
            {
                using HttpResponseMessage allowed = await participant.GetAsync(route);
                Assert.Equal(HttpStatusCode.NotFound, allowed.StatusCode);
                Assert.Equal("OPERATION_NOT_FOUND", (await Describe(allowed)).Title);
            }
        }
    }

    [Fact]
    public async Task AdminEndpoints_RefuseRentersSuppliersAndServiceTokens_AndOnlyAdminsReadAnyOrder()
    {
        using OrderApiFactory factory = new();
        RentalOrder order = factory.SeedPaidMixedOrder();
        using HttpClient anonymous = factory.CreateClient();
        using HttpClient renter = factory.CreateClientFor(factory.RenterId);
        using HttpClient supplier = factory.CreateClientFor(factory.SupplierBinh);
        using HttpClient service = factory.CreateClientFor(Guid.NewGuid(), roles: "admin", service: true);
        using HttpClient admin = factory.CreateClientFor(Guid.NewGuid(), roles: "admin");

        foreach (string route in new[] { $"{Root}/admin/orders", $"{Root}/admin/orders/{order.Id}", $"{Root}/admin/orders/{Guid.NewGuid()}" })
        {
            using HttpResponseMessage noToken = await anonymous.GetAsync(route);
            Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
            foreach (HttpClient refused in new[] { renter, supplier, service })
            {
                using HttpResponseMessage response = await refused.GetAsync(route);
                // Sai vai trò là 403 dù mã có tồn tại hay không, nên không lộ thông tin.
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }

        using HttpResponseMessage allowed = await admin.GetAsync($"{Root}/admin/orders/{order.Id}");
        using HttpResponseMessage missing = await admin.GetAsync($"{Root}/admin/orders/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ServiceTokens_AreRefusedByPersonalOrderEndpoints()
    {
        using OrderApiFactory factory = new();
        RentalOrder order = factory.SeedPaidMixedOrder();
        using HttpClient service = factory.CreateClientFor(factory.RenterId, service: true);

        foreach (string route in new[] { $"{Root}/orders", $"{Root}/orders/{order.Id}", $"{Root}/supplier/sub-orders" })
        {
            using HttpResponseMessage response = await service.GetAsync(route);
            Assert.True(
                response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
                $"{route} trả {(int)response.StatusCode}");
        }
    }

    private static (string ForeignPath, string UnknownPath, object Body) Build(string action, Guid subOrderId, RentalItem item)
    {
        Guid unknown = Guid.NewGuid();
        Guid assignmentId = item.UnitAssignments.First().Id;
        return action switch
        {
            "accept-sub-order" => (
                $"{Root}/supplier/sub-orders/{subOrderId}/accept",
                $"{Root}/supplier/sub-orders/{unknown}/accept",
                new { }),
            "reject-sub-order" => (
                $"{Root}/supplier/sub-orders/{subOrderId}/reject",
                $"{Root}/supplier/sub-orders/{unknown}/reject",
                new { rejectReason = "Không giao kịp" }),
            "handover-item" => (
                $"{Root}/items/{item.Id}/handover",
                $"{Root}/items/{unknown}/handover",
                new
                {
                    actualHandoverAt = DateTimeOffset.UnixEpoch,
                    units = new[] { new { assignmentId, condition = 0 } }
                }),
            "return-item" => (
                $"{Root}/items/{item.Id}/returns",
                $"{Root}/items/{unknown}/returns",
                new
                {
                    actualReturnAt = DateTimeOffset.UnixEpoch,
                    units = new[] { new { rentalUnitAssignmentId = assignmentId, condition = 0, mediaIds = Array.Empty<Guid>() } }
                }),
            "deduct-item" => (
                $"{Root}/items/{item.Id}/deductions",
                $"{Root}/items/{unknown}/deductions",
                new
                {
                    deductionAmount = 1,
                    damageDescription = "Vỡ thùng trộn",
                    evidenceMediaIds = new[] { Guid.NewGuid() },
                    affectedAssetIds = new[] { Guid.NewGuid() }
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
    }

    /// <summary>Phần của phản hồi lỗi mà người gọi nhìn thấy; hai phản hồi giống nhau thì không phân biệt được.</summary>
    private static async Task<(int Status, string? Title, string? Detail)> Describe(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        return (
            (int)response.StatusCode,
            root.TryGetProperty("title", out JsonElement title) ? title.GetString() : null,
            root.TryGetProperty("detail", out JsonElement detail) ? detail.GetString() : null);
    }
}
