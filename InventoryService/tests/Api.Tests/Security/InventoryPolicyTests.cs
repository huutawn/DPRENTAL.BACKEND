using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Dprental.InventoryService.Domain.Entities;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Security;

public sealed class InventoryPolicyTests
{
    private readonly Guid _ownerUserId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();
    private readonly Guid _orgId = Guid.NewGuid();

    [Theory]
    [InlineData("manage", "POST")]
    [InlineData("calendar", "GET")]
    [InlineData("manual-allocate", "POST")]
    public async Task MissingToken_ResourcePoliciesReturn401(string capability, string method)
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();
        var asset = CreateTestAsset(factory, _ownerUserId);
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/test/assets/{asset.Id}/{capability}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void AdminCannotAuthorizeMissingOwnershipResource()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", Guid.NewGuid().ToString()), new Claim("r", "adm")], "Bearer"));
        var caller = new Dprental.InventoryService.Api.Security.InventoryCallerContext(principal);
        foreach (Guid? owner in new Guid?[] { null, Guid.Empty })
        {
            Assert.False(Dprental.InventoryService.Application.Security.InventoryAccessEvaluator.CanManageAsset(caller, owner));
            Assert.False(Dprental.InventoryService.Application.Security.InventoryAccessEvaluator.CanAccessCalendar(caller, owner));
            Assert.False(Dprental.InventoryService.Application.Security.InventoryAccessEvaluator.CanManualAllocate(caller, owner));
        }
    }

    [Fact]
    public async Task BareOwnerGuidIsNotATrustedAuthorizationResource()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", _ownerUserId.ToString()), new Claim("r", "adm")], "Bearer"));
        var caller = new Dprental.InventoryService.Api.Security.InventoryCallerContext(principal);
        var requirement = new Dprental.InventoryService.Api.Security.AssetManageRequirement();
        var context = new Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext([requirement], principal, _ownerUserId);
        await new Dprental.InventoryService.Api.Security.InventoryAuthorizationHandler(caller).HandleAsync(context);
        Assert.False(context.HasSucceeded);
    }

    private static InventoryAsset CreateTestAsset(InventorySecurityTestFactory factory, Guid ownerId, Guid? assetId = null)
    {
        var id = assetId ?? Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var listing = EquipmentListing.Create(
            Guid.NewGuid(),
            ownerId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Sony FX3",
            null,
            "Sony FX3",
            1,
            "{\"serial\":\"TEST\"}",
            100_000m,
            null,
            null,
            "VND",
            now,
            2024).Value;

        var asset = InventoryAsset.Create(id, listing.Id, $"SN-{id:N}", now).Value;
        typeof(InventoryAsset).GetProperty(nameof(InventoryAsset.Listing))?.SetValue(asset, listing);
        factory.TestAssets[id] = asset;
        return asset;
    }

    [Fact]
    public async Task ManageAsset_UserHasPermissionAndOwnsAsset_Succeeds200()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        var asset = CreateTestAsset(factory, _ownerUserId);
        string token = factory.CreateHumanToken(_ownerUserId, permissions: ["inventory.asset.manage"]);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/test/assets/{asset.Id}/manage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ManageAsset_UserHasPermissionButBelongsToOtherUser_Returns403Forbidden()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        var asset = CreateTestAsset(factory, _ownerUserId);
        // _otherUserId có permission nhưng không phải chủ sở hữu tài sản
        string token = factory.CreateHumanToken(_otherUserId, permissions: ["inventory.asset.manage"]);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/test/assets/{asset.Id}/manage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ManageAsset_SameOrganizationDifferentOwner_IsIsolatedAndReturns403Forbidden()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        // 2 assets cùng orgId nhưng khác ownerUserId
        var assetOfUserA = CreateTestAsset(factory, _ownerUserId);
        string tokenUserB = factory.CreateHumanToken(_otherUserId, permissions: ["inventory.asset.manage"], orgId: _orgId);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/test/assets/{assetOfUserA.Id}/manage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenUserB);

        using var response = await client.SendAsync(request);
        // OrganizationId không được dùng làm authorization boundary -> Phải bị từ chối
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ManageAsset_OwnerMatchesButMissingPermission_Returns403Forbidden()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        var asset = CreateTestAsset(factory, _ownerUserId);
        // Đúng chủ sở hữu nhưng thiếu permission inventory.asset.manage
        string token = factory.CreateHumanToken(_ownerUserId, permissions: ["inventory.calendar.read"]);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/test/assets/{asset.Id}/manage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ManageAsset_HumanAdminWithRoleAdm_CanManageOtherUserAsset()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        var asset = CreateTestAsset(factory, _ownerUserId);
        // Human Admin có r=adm, không cần permission p hay quyền sở hữu
        string adminToken = factory.CreateAdminToken(_otherUserId);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/test/assets/{asset.Id}/manage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ManageAsset_ServiceWithRoleAdm_CannotBypassHumanPolicies()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        var asset = CreateTestAsset(factory, _ownerUserId);
        // Service token giả mạo claim r=adm
        string serviceToken = factory.CreateServiceToken(Guid.NewGuid(), roles: ["adm"]);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/test/assets/{asset.Id}/manage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AllocationWrite_HumanAdmin_CannotCallServicePolicy()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        // Human Admin không được phép gọi lệnh allocation-write vốn dành riêng cho service
        string adminToken = factory.CreateAdminToken(_ownerUserId);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/allocations/commands");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AllocationWrite_HumanTokenWithScopeClaim_IsNotTreatedAsService()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        // Human token (không có u=svc) chứa claim s=inventory.allocation.write
        var extraClaims = new[] { new Claim("s", "inventory.allocation.write") };
        string humanWithScope = factory.CreateToken(_ownerUserId.ToString("D"), extraClaims, InventorySecurityTestFactory.HumanAudience);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/allocations/commands");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", humanWithScope);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ScopedService_CanReadAvailability_AndExecuteAllocationCommands()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        string readServiceToken = factory.CreateServiceToken(
            Guid.NewGuid(),
            scopes: ["inventory.availability.read"]);

        using var readReq = new HttpRequestMessage(HttpMethod.Get, "/api/test/availability/sanitized");
        readReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", readServiceToken);
        var readResp = await client.SendAsync(readReq);
        Assert.Equal(HttpStatusCode.OK, readResp.StatusCode);

        // inventory.calendar.read riêng lẻ bị từ chối đọc availability
        string calendarReadServiceToken = factory.CreateServiceToken(
            Guid.NewGuid(),
            scopes: ["inventory.calendar.read"]);
        using var calReq = new HttpRequestMessage(HttpMethod.Get, "/api/test/availability/sanitized");
        calReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", calendarReadServiceToken);
        var calResp = await client.SendAsync(calReq);
        Assert.Equal(HttpStatusCode.Forbidden, calResp.StatusCode);

        // inventory.allocation.write riêng lẻ bị từ chối đọc availability
        string writeOnlyToken = factory.CreateServiceToken(
            Guid.NewGuid(),
            scopes: ["inventory.allocation.write"]);
        using var writeOnlyReq = new HttpRequestMessage(HttpMethod.Get, "/api/test/availability/sanitized");
        writeOnlyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", writeOnlyToken);
        var writeOnlyResp = await client.SendAsync(writeOnlyReq);
        Assert.Equal(HttpStatusCode.Forbidden, writeOnlyResp.StatusCode);

        // Service có availability.read gọi operational calendar bị từ chối
        var testAsset = CreateTestAsset(factory, _ownerUserId);
        using var opCalReq = new HttpRequestMessage(HttpMethod.Get, $"/api/test/assets/{testAsset.Id}/calendar");
        opCalReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", readServiceToken);
        var opCalResp = await client.SendAsync(opCalReq);
        Assert.Equal(HttpStatusCode.Forbidden, opCalResp.StatusCode);

        // Service có allocation.write thực thi allocation command thành công
        string writeServiceToken = factory.CreateServiceToken(
            Guid.NewGuid(),
            scopes: ["inventory.allocation.write"]);

        using var writeReq = new HttpRequestMessage(HttpMethod.Post, "/api/test/allocations/commands");
        writeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", writeServiceToken);
        var writeResp = await client.SendAsync(writeReq);
        Assert.Equal(HttpStatusCode.OK, writeResp.StatusCode);
    }
    [Fact]
    public async Task ForgedIdentityHeaders_DoNotBypassAuthorization()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        var asset = CreateTestAsset(factory, _ownerUserId);
        // Token của user B
        string tokenUserB = factory.CreateHumanToken(_otherUserId, permissions: ["inventory.asset.manage"]);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/test/assets/{asset.Id}/manage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenUserB);
        // Giả mạo header X-User-Id thành ownerUserId
        request.Headers.Add("X-User-Id", _ownerUserId.ToString("D"));
        request.Headers.Add("X-Role", "adm");
        request.Headers.Add("X-Organization-Id", _orgId.ToString("D"));

        using var response = await client.SendAsync(request);
        // Header bị bỏ qua, phân quyền dựa trên JWT claims -> 403
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CalendarRead_OwnerMatchesWithoutPermission_Succeeds()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        var asset = CreateTestAsset(factory, _ownerUserId);
        // DPR-149: Không yêu cầu permission 'p', chỉ cần JWT sub == asset.OwnerUserId
        string token = factory.CreateHumanToken(_ownerUserId, permissions: null);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/test/assets/{asset.Id}/calendar");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CalendarRead_AdminNonOwner_IsForbidden()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        var asset = CreateTestAsset(factory, _ownerUserId);
        // DPR-149: Admin không được bypass ownership
        string adminToken = factory.CreateAdminToken(_otherUserId);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/test/assets/{asset.Id}/calendar");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CalendarRead_ServicePrincipal_IsForbidden()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        var asset = CreateTestAsset(factory, _ownerUserId);
        // DPR-149: Service principal bị từ chối
        string serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: ["inventory.calendar.read"]);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/test/assets/{asset.Id}/calendar");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ManualAllocate_OwnerMatches_Succeeds()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        var asset = CreateTestAsset(factory, _ownerUserId);
        string token = factory.CreateHumanToken(_ownerUserId, permissions: ["inventory.asset.manage"]);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/test/assets/{asset.Id}/manual-allocate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
