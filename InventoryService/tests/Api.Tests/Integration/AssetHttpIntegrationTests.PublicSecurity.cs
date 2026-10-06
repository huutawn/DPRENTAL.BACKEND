using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.Security;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class AssetHttpIntegrationTests
{
    private static readonly string[] ForbiddenFieldNames =
    [
        "ownerUserId", "owner_user_id", "ownerId", "owner_id",
        "organizationId", "organization_id",
        "serialNumber", "serial_number", "serial", "serialNo", "serial_no",
        "displayName", "display_name",
        "suspensionReason", "suspension_reason",
        "externalReference", "external_reference",
        "sourceSystem", "source_system",
        "holdExpiresAt", "hold_expires_at",
        "calendarBlocks", "calendar_blocks",
        "allocations", "allocationId", "allocation_id",
        "renterId", "renter_id",
        "renterUserId", "renter_user_id",
        "orderId", "order_id",
        "orderReference", "order_reference",
        "rentalOrderId", "rental_order_id",
        "rentalOrderReference", "rental_order_reference",
        "reservationId", "reservation_id",
        "reservationReference", "reservation_reference",
        "version", "xmin",
        "createdAt", "created_at",
        "updatedAt", "updated_at",
        "createdBy", "created_by",
        "updatedBy", "updated_by",
        "permissions", "permission",
        "inventoryAssetManage", "inventory_asset_manage"
    ];

    [Fact]
    public async Task GetPublicAssets_ResponseJsonContainsZeroPrivateProperties()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(
            owner,
            model: "Toyota 8FD25",
            serial: "SECRET-SN-12345",
            specValues: "{\"capacityKg\": 2500, \"fuelType\": \"diesel\", \"ownerUserId\": \"leak-attempt\"}");

        // 1. List
        var resList = await client.GetAsync("api/v1/inventory/public/assets");
        Assert.Equal(HttpStatusCode.OK, resList.StatusCode);
        var listJson = await resList.Content.ReadAsStringAsync();
        using var docList = JsonDocument.Parse(listJson);
        AssertZeroForbiddenProperties(docList.RootElement);

        // 2. Detail
        var resDetail = await client.GetAsync($"api/v1/inventory/public/assets/{asset.Id:D}");
        Assert.Equal(HttpStatusCode.OK, resDetail.StatusCode);
        var detailJson = await resDetail.Content.ReadAsStringAsync();
        using var docDetail = JsonDocument.Parse(detailJson);
        AssertZeroForbiddenProperties(docDetail.RootElement);
    }

    [Fact]
    public async Task GetPublicAssets_SerialSearchSideChannel_Prevented()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var secretSerial = "TOP-SECRET-SERIAL-XYZ";
        var asset = await SeedAssetAsync(owner, model: "ModelPublic77", serial: secretSerial);

        // Public discovery search by serial number returns NO matches
        var resPublic = await client.GetAsync($"api/v1/inventory/public/assets?q={secretSerial}");
        Assert.Equal(HttpStatusCode.OK, resPublic.StatusCode);
        var pagedPublic = await resPublic.Content.ReadFromJsonAsync<PagedResponse<PublicInventoryAssetResponse>>();
        Assert.NotNull(pagedPublic);
        Assert.Empty(pagedPublic.Items);
        Assert.Equal(0, pagedPublic.TotalCount);

        // Authenticated owner search by same serial number DOES discover the asset
        var token = secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resOwner = await client.GetAsync($"api/v1/inventory/assets?q={secretSerial}");
        Assert.Equal(HttpStatusCode.OK, resOwner.StatusCode);
        var pagedOwner = await resOwner.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(pagedOwner);
        Assert.Single(pagedOwner.Items);
        Assert.Equal(asset.Id, pagedOwner.Items[0].Id);
    }

    [Theory]
    [InlineData("ownerUserId")]
    [InlineData("serialNumber")]
    [InlineData("suspensionReason")]
    [InlineData("holdExpiresAt")]
    [InlineData("renterUserId")]
    [InlineData("rentalOrderId")]
    [InlineData("rentalOrderReference")]
    [InlineData("orderReference")]
    [InlineData("reservationReference")]
    [InlineData("organizationId")]
    public async Task GetPublicAssets_ReservedSpecKeyDynamicFilter_RejectedWith400(string reservedKey)
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var filterJson = Uri.EscapeDataString($"[{{\"field\":\"{reservedKey}\",\"operator\":\"eq\",\"value\":\"secret\"}}]");
        var res = await client.GetAsync($"api/v1/inventory/public/assets?equipmentTypeId={Guid.NewGuid():D}&filters={filterJson}");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("INVALID_QUERY_PARAMETER", problem.Title);
    }

    private static void AssertZeroForbiddenProperties(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    Assert.DoesNotContain(prop.Name, ForbiddenFieldNames, StringComparer.OrdinalIgnoreCase);
                    AssertZeroForbiddenProperties(prop.Value);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AssertZeroForbiddenProperties(item);
                }
                break;
        }
    }
}
