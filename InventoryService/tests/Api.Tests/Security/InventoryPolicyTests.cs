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
            Assert.False(Dprental.InventoryService.Application.Security.InventoryAccessEvaluator.CanReadCalendarDetail(caller, owner));
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

    private InventoryAsset CreateTestAsset(InventorySecurityTestFactory factory, Guid ownerId, Guid? assetId = null)
    {
        var id = assetId ?? Guid.NewGuid();
        var asset = InventoryAsset.Create(
            id,
            ownerId,
            _orgId,
            Guid.NewGuid(),
            1,
            "{\"serial\":\"TEST\"}",
            true,
            DateTimeOffset.UtcNow).Value;
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
    public async Task ScopedService_CanReadSanitizedAvailability_AndExecuteAllocationCommands()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        string readServiceToken = factory.CreateServiceToken(
            Guid.NewGuid(),
            scopes: ["inventory.calendar.read"]);

        using var readReq = new HttpRequestMessage(HttpMethod.Get, "/api/test/availability/sanitized");
        readReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", readServiceToken);
        var readResp = await client.SendAsync(readReq);
        Assert.Equal(HttpStatusCode.OK, readResp.StatusCode);

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
    public async Task CalendarRead_OwnerMatches_Succeeds()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        var asset = CreateTestAsset(factory, _ownerUserId);
        string token = factory.CreateHumanToken(_ownerUserId, permissions: ["inventory.calendar.read"]);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/test/assets/{asset.Id}/calendar");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
