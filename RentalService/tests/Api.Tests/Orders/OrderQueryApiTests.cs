using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Domain.IRepositories;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dprental.RentalService.Application.Security.Jwt;
using Dprental.RentalService.Domain.Repositories;
using Dprental.RentalService.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Orders;

/// <summary>Gọi các API xem đơn qua HTTP với toàn bộ DI thật, chỉ thay database và cách xác thực.</summary>
public sealed class OrderQueryApiTests
{

    [Fact]
    public async Task GetOrders_WithoutToken_Returns401()
    {
        using OrderApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/rental/orders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Renter_GetsOwnOrderWithSubOrders_AndAnotherRenterGets404()
    {
        using OrderApiFactory factory = new();
        RentalOrder order = factory.SeedPaidMixedOrder();

        using HttpClient renter = factory.CreateClientFor(factory.RenterId);
        using HttpResponseMessage own = await renter.GetAsync($"/api/v1/rental/orders/{order.Id}");
        using HttpClient stranger = factory.CreateClientFor(Guid.NewGuid());
        using HttpResponseMessage other = await stranger.GetAsync($"/api/v1/rental/orders/{order.Id}");

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        JsonElement body = await ReadAsync(own);
        Assert.Equal(order.OrderCode, body.GetProperty("orderCode").GetString());
        Assert.Equal("PAID", body.GetProperty("status").GetString());
        Assert.Equal(2, body.GetProperty("subOrders").GetArrayLength());
        JsonElement firstItem = body.GetProperty("subOrders")[0].GetProperty("items")[0];
        Assert.Equal("2026-10-14", firstItem.GetProperty("endDate").GetString());
        Assert.Equal("PENDING_SUPPLIER_CONFIRMATION", firstItem.GetProperty("status").GetString());
        Assert.Equal(3_000_000m, firstItem.GetProperty("dailyRate").GetDecimal());
        Assert.Equal("NONE", firstItem.GetProperty("refund").GetProperty("state").GetString());
        Assert.Equal(55, firstItem.GetProperty("listing").GetProperty("specValues").GetProperty("cong_suat_kw").GetInt32());
        Assert.Equal("PENDING_SUPPLIER_CONFIRMATION", body.GetProperty("subOrders")[0].GetProperty("status").GetString());
        Assert.Equal("PAID", body.GetProperty("paymentState").GetString());
        Assert.Equal("0123456789", body.GetProperty("refundAccount").GetProperty("accountNumber").GetString());
        Assert.DoesNotContain("assetId", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("allocation", body.GetRawText(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal("Order.NotFound", (await ReadAsync(other)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Renter_FiltersOrdersByStatus_AndUnknownStatusIs400()
    {
        using OrderApiFactory factory = new();
        RentalOrder order = factory.SeedPaidMixedOrder();
        using HttpClient renter = factory.CreateClientFor(factory.RenterId);

        using HttpResponseMessage paid = await renter.GetAsync("/api/v1/rental/orders?status=PAID");
        using HttpResponseMessage pending = await renter.GetAsync("/api/v1/rental/orders?status=PENDING_PAYMENT");
        using HttpResponseMessage unknown = await renter.GetAsync("/api/v1/rental/orders?status=SHIPPED");

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        JsonElement paidPage = await ReadAsync(paid);
        Assert.Equal(1, paidPage.GetProperty("totalCount").GetInt32());
        Assert.Equal(order.OrderCode, paidPage.GetProperty("items")[0].GetProperty("orderCode").GetString());
        Assert.Equal(0, (await ReadAsync(pending)).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("Order.InvalidStatus", (await ReadAsync(unknown)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Admin_SearchesAndOpensAnyOrder_OthersAreRefused()
    {
        using OrderApiFactory factory = new();
        RentalOrder order = factory.SeedPaidMixedOrder();
        RentalItem mixers = order.Items.Single(i => i.SupplierId == factory.SupplierCuong);
        using HttpClient admin = factory.CreateClientFor(Guid.NewGuid(), roles: "admin");
        using HttpClient renter = factory.CreateClientFor(factory.RenterId);

        using HttpResponseMessage byAsset =
            await admin.GetAsync($"/api/v1/rental/admin/orders?assetId={mixers.UnitAssignments.First().AssetId}&itemStatus=PENDING_SUPPLIER_CONFIRMATION");
        using HttpResponseMessage noMatch = await admin.GetAsync($"/api/v1/rental/admin/orders?renterId={Guid.NewGuid()}");
        using HttpResponseMessage badGuid = await admin.GetAsync("/api/v1/rental/admin/orders?listingId=abc");
        using HttpResponseMessage badDate = await admin.GetAsync("/api/v1/rental/admin/orders?createdFrom=2026-10-01");
        using HttpResponseMessage detail = await admin.GetAsync($"/api/v1/rental/admin/orders/{order.Id}");
        using HttpResponseMessage renterSearch = await renter.GetAsync("/api/v1/rental/admin/orders");
        using HttpResponseMessage renterDetail = await renter.GetAsync($"/api/v1/rental/admin/orders/{order.Id}");
        using HttpResponseMessage anonymous = await factory.CreateClient().GetAsync("/api/v1/rental/admin/orders");

        Assert.Equal(HttpStatusCode.OK, byAsset.StatusCode);
        JsonElement found = await ReadAsync(byAsset);
        Assert.Equal(1, found.GetProperty("totalCount").GetInt32());
        Assert.Equal(order.RenterId, found.GetProperty("items")[0].GetProperty("renterId").GetGuid());
        Assert.Equal(0, (await ReadAsync(noMatch)).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, badGuid.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badDate.StatusCode);
        Assert.Equal("Order.InvalidDateRange", (await ReadAsync(badDate)).GetProperty("title").GetString());

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        JsonElement body = await ReadAsync(detail);
        Assert.Equal($"INV-{order.OrderCode}", body.GetProperty("invoiceRef").GetString());
        Assert.Equal("PAID", body.GetProperty("paymentState").GetString());
        Assert.Equal("******6789", body.GetProperty("refundAccount").GetProperty("maskedAccountNumber").GetString());
        Assert.False(body.GetProperty("refundAccount").TryGetProperty("accountNumber", out _));
        JsonElement mixerLine = body.GetProperty("subOrders").EnumerateArray()
            .SelectMany(s => s.GetProperty("items").EnumerateArray())
            .Single(i => i.GetProperty("rentalItemId").GetGuid() == mixers.Id);
        Assert.Equal(mixers.AllocationGroupId, mixerLine.GetProperty("allocationGroupId").GetString());
        Assert.Equal(2, mixerLine.GetProperty("units").GetArrayLength());
        Assert.True(body.GetProperty("history").GetArrayLength() > 0);

        Assert.Equal(HttpStatusCode.Forbidden, renterSearch.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, renterDetail.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Supplier_FiltersWorkQueueByItemStatus_AndSeesOwnUnits()
    {
        using OrderApiFactory factory = new();
        RentalOrder order = factory.SeedPaidMixedOrder();
        RentalSubOrder cuongsSubOrder = order.SubOrders.Single(s => s.SupplierId == factory.SupplierCuong);
        RentalItem mixers = cuongsSubOrder.Items.Single();
        using HttpClient cuong = factory.CreateClientFor(factory.SupplierCuong);

        using HttpResponseMessage waiting =
            await cuong.GetAsync("/api/v1/rental/supplier/sub-orders?status=PENDING_SUPPLIER_CONFIRMATION");
        using HttpResponseMessage inRental = await cuong.GetAsync("/api/v1/rental/supplier/sub-orders?status=IN_RENTAL");
        using HttpResponseMessage unknown = await cuong.GetAsync("/api/v1/rental/supplier/sub-orders?status=SHIPPED");
        using HttpResponseMessage detail = await cuong.GetAsync($"/api/v1/rental/supplier/sub-orders/{cuongsSubOrder.Id}");

        Assert.Equal(cuongsSubOrder.SubOrderCode,
            (await ReadAsync(waiting)).GetProperty("items")[0].GetProperty("subOrderCode").GetString());
        Assert.Equal(0, (await ReadAsync(inRental)).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("Order.InvalidStatus", (await ReadAsync(unknown)).GetProperty("title").GetString());

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        JsonElement line = (await ReadAsync(detail)).GetProperty("items")[0];
        Assert.Equal(mixers.Id, line.GetProperty("rentalItemId").GetGuid());
        Assert.Equal("PENDING_SUPPLIER_CONFIRMATION", line.GetProperty("status").GetString());
        Assert.Equal(400_000m, line.GetProperty("dailyRate").GetDecimal());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("handover").ValueKind);
        Assert.Equal(
            mixers.UnitAssignments.Select(u => u.AssetId).Order(),
            line.GetProperty("units").EnumerateArray().Select(u => u.GetProperty("assetId").GetGuid()).Order());
        Assert.All(line.GetProperty("units").EnumerateArray(),
            u => Assert.Equal("ASSIGNED", u.GetProperty("status").GetString()));
        Assert.False(line.TryGetProperty("refund", out _));
    }

    [Fact]
    public async Task Supplier_ListsOnlyOwnSubOrder_AndCannotOpenAnotherSuppliersSubOrder()
    {
        using OrderApiFactory factory = new();
        RentalOrder order = factory.SeedPaidMixedOrder();
        RentalSubOrder cuongsSubOrder = order.SubOrders.Single(s => s.SupplierId == factory.SupplierCuong);

        using HttpClient binh = factory.CreateClientFor(factory.SupplierBinh);
        using HttpResponseMessage list = await binh.GetAsync("/api/v1/rental/supplier/sub-orders");
        using HttpResponseMessage foreign = await binh.GetAsync($"/api/v1/rental/supplier/sub-orders/{cuongsSubOrder.Id}");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        JsonElement page = await ReadAsync(list);
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
        JsonElement subOrder = page.GetProperty("items")[0];
        Assert.Equal($"{order.OrderCode}-01", subOrder.GetProperty("subOrderCode").GetString());
        Assert.Equal(1, subOrder.GetProperty("itemCount").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("SubOrder.NotFound", (await ReadAsync(foreign)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Supplier_RejectsWholeSubOrderOverHttp_AndThePerItemRouteNoLongerExists()
    {
        using OrderApiFactory factory = new();
        RentalOrder order = factory.SeedPaidMixedOrder();
        RentalSubOrder binhsSubOrder = order.SubOrders.Single(s => s.SupplierId == factory.SupplierBinh);
        Guid binhsItemId = binhsSubOrder.Items.Single().Id;

        using HttpClient binh = factory.CreateClientFor(factory.SupplierBinh);
        binh.DefaultRequestHeaders.Add("Idempotency-Key", "reject-1");
        using HttpResponseMessage perItem = await binh.PostAsync($"/api/v1/rental/items/{binhsItemId}/reject",
            JsonContent.Create(new { rejectReason = "Không giao kịp" }));
        using HttpResponseMessage reject = await binh.PostAsync($"/api/v1/rental/supplier/sub-orders/{binhsSubOrder.Id}/reject",
            JsonContent.Create(new { rejectReason = "Máy xúc đang bảo trì, không giao kịp" }));

        Assert.Equal(HttpStatusCode.NotFound, perItem.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        JsonElement body = await ReadAsync(reject);
        Assert.Equal("Rejected", body.GetProperty("status").GetString());
        Assert.Equal($"{order.OrderCode}-01", body.GetProperty("subOrderCode").GetString());

        // Người thuê thấy phần của Bình bị từ chối, phần của Cường vẫn chờ xác nhận.
        using HttpClient renter = factory.CreateClientFor(factory.RenterId);
        JsonElement detail = await ReadAsync(await renter.GetAsync($"/api/v1/rental/orders/{order.Id}"));
        Assert.Equal("REJECTED", detail.GetProperty("subOrders")[0].GetProperty("status").GetString());
        Assert.Equal("PENDING_SUPPLIER_CONFIRMATION", detail.GetProperty("subOrders")[1].GetProperty("status").GetString());
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

}
