using Dprental.RentalService.Domain.Errors;
using System.Security.Claims;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Security.Jwt;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;
using Dprental.RentalService.Api.Tests.Domain;

namespace Dprental.RentalService.Api.Tests.Contracts;

public sealed class AuthRentalContractAndPolicyTests
{
    // 1. Claims Parsing & IRentalCallerContext Mapping
    [Fact]
    public void FromClaims_ValidInteractiveUser_ParsesAllAttributes()
    {
        Guid userId = Guid.NewGuid();
        List<KeyValuePair<string, string>> claims =
        [
            new(RentalAuthClaims.Subject, userId.ToString("D")),
            new(RentalAuthClaims.ProfileCompleted, "true"),
            new(RentalAuthClaims.UserTypeOrAlias, "tan_renter"),
            new(RentalAuthClaims.Roles, "customer,member"),
            new(RentalAuthClaims.Permissions, "orders.create,orders.read")
        ];

        var context = RentalCallerContext.FromClaims(claims);

        Assert.True(context.IsAuthenticated);
        Assert.False(context.IsService);
        Assert.Equal(userId, context.UserId);
        Assert.True(context.IsProfileCompleted);
        Assert.True(context.HasRole("customer"));
        Assert.True(context.HasRole("member"));
        Assert.True(context.HasPermission("orders.create"));
        Assert.False(context.IsAdmin());
    }

    [Fact]
    public void FromClaims_ServiceTokenWithUuidSubAndUEqualsSvc_IdentifiedAsService()
    {
        Guid serviceUuid = Guid.NewGuid();
        List<KeyValuePair<string, string>> claims =
        [
            new(RentalAuthClaims.Subject, serviceUuid.ToString("D")),
            new(RentalAuthClaims.UserTypeOrAlias, RentalAuthClaims.ServiceIndicator), // "svc"
            new(RentalAuthClaims.Roles, "service")
        ];

        var context = RentalCallerContext.FromClaims(claims);

        Assert.True(context.IsAuthenticated);
        Assert.True(context.IsService);
        Assert.Null(context.UserId); // Invariant: service has no interactive UserId
        Assert.False(context.IsProfileCompleted); // Invariant: service profile is never completed
    }

    [Fact]
    public void FromClaims_MalformedNonUuidSubject_IsNotAuthenticated_NeverInferredAsService()
    {
        // Invariant: User must have valid UUID in 'sub'. Do NOT infer service from malformed sub!
        List<KeyValuePair<string, string>> claims =
        [
            new(RentalAuthClaims.Subject, "inventory-sync-worker"),
            new(RentalAuthClaims.Roles, "service")
        ];

        var context = RentalCallerContext.FromClaims(claims);

        Assert.False(context.IsAuthenticated);
        Assert.False(context.IsService);
        Assert.Null(context.UserId);
    }

    [Fact]
    public void FromClaims_RoleAliases_AreMappedBothWays()
    {
        Guid userId = Guid.NewGuid();
        List<KeyValuePair<string, string>> claims =
        [
            new(RentalAuthClaims.Subject, userId.ToString("D")),
            new(RentalAuthClaims.ProfileCompleted, "true"),
            new(RentalAuthClaims.Roles, "adm,cus,own,mem")
        ];

        var context = RentalCallerContext.FromClaims(claims);

        // Alias 'adm' expands to 'admin'
        Assert.True(context.HasRole("adm"));
        Assert.True(context.HasRole("admin"));
        Assert.True(context.IsAdmin());

        // Alias 'cus' expands to 'customer'
        Assert.True(context.HasRole("cus"));
        Assert.True(context.HasRole("customer"));

        // Alias 'own' expands to 'owner'
        Assert.True(context.HasRole("own"));
        Assert.True(context.HasRole("owner"));

        // Alias 'mem' expands to 'member'
        Assert.True(context.HasRole("mem"));
        Assert.True(context.HasRole("member"));
    }

    [Fact]
    public void FromClaimsPrincipal_MapsCorrectly()
    {
        Guid userId = Guid.NewGuid();
        ClaimsIdentity identity = new("TestAuth");
        identity.AddClaim(new Claim(RentalAuthClaims.Subject, userId.ToString("D")));
        identity.AddClaim(new Claim(RentalAuthClaims.ProfileCompleted, "true"));
        identity.AddClaim(new Claim(RentalAuthClaims.Roles, "adp")); // alias for admin_platform
        ClaimsPrincipal principal = new(identity);

        var context = RentalCallerContext.FromClaimsPrincipal(principal);

        Assert.True(context.IsAuthenticated);
        Assert.True(context.IsAdmin());
        Assert.True(context.HasRole("admin_platform"));
        Assert.True(context.IsProfileCompleted);
    }

    // 3. Order & Resource Ownership Policy
    [Fact]
    public void AuthorizeOrderRead_BlocksServiceTokens_AllowsRenterAndAdmin_BlocksStrangers()
    {
        Guid renterId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        RentalOrder order = CreateTestOrder(renterId, Guid.NewGuid());

        // 1. Service token -> strictly blocked before admin/ownership check
        IRentalCallerContext service = CreateServiceCaller(roles: ["admin"]); // Even if service has admin role!
        Result serviceRes = RentalAuthorizationPolicy.AuthorizeOrderRead(service, order);
        Assert.True(serviceRes.IsFailure);
        Assert.Equal("Auth.ServiceTokenNotAllowed", serviceRes.Error.Code);

        // 2. Renter -> Success
        IRentalCallerContext renter = CreateUserCaller(renterId);
        Assert.True(RentalAuthorizationPolicy.AuthorizeOrderRead(renter, order).IsSuccess);

        // 3. Admin -> Success
        IRentalCallerContext admin = CreateUserCaller(Guid.NewGuid(), roles: ["admin"]);
        Assert.True(RentalAuthorizationPolicy.AuthorizeOrderRead(admin, order).IsSuccess);

        // 4. Stranger -> NotFound, the same error as a missing order (no IDOR probing)
        IRentalCallerContext stranger = CreateUserCaller(otherUserId);
        Result strangerRes = RentalAuthorizationPolicy.AuthorizeOrderRead(stranger, order);
        Assert.True(strangerRes.IsFailure);
        Assert.Equal(RentalErrors.OrderNotFound, strangerRes.Error);
    }

    [Fact]
    public void AuthorizeRenterAction_BlocksServiceTokensAndOtherUsers()
    {
        Guid renterId = Guid.NewGuid();
        RentalOrder order = CreateTestOrder(renterId, Guid.NewGuid());

        // 1. Owner renter -> Success
        IRentalCallerContext renter = CreateUserCaller(renterId);
        Assert.True(RentalAuthorizationPolicy.AuthorizeRenterAction(renter, order).IsSuccess);

        // 2. Service token -> ServiceTokenNotAllowed
        IRentalCallerContext service = CreateServiceCaller();
        Result serviceRes = RentalAuthorizationPolicy.AuthorizeRenterAction(service, order);
        Assert.True(serviceRes.IsFailure);
        Assert.Equal("Auth.ServiceTokenNotAllowed", serviceRes.Error.Code);

        // 3. Other user -> NotFound, the same error as a missing order
        IRentalCallerContext other = CreateUserCaller(Guid.NewGuid());
        Result otherRes = RentalAuthorizationPolicy.AuthorizeRenterAction(other, order);
        Assert.True(otherRes.IsFailure);
        Assert.Equal(RentalErrors.OrderNotFound, otherRes.Error);
    }

    [Fact]
    public void AuthorizeSupplierAction_RequiresExactSupplierOwnership_BlocksServiceAndStrangers()
    {
        Guid supplierId = Guid.NewGuid();
        RentalOrder order = CreateTestOrder(Guid.NewGuid(), supplierId);
        RentalItem item = order.Items.First();

        // 1. Exact supplier -> Success
        IRentalCallerContext supplier = CreateUserCaller(supplierId);
        Assert.True(RentalAuthorizationPolicy.AuthorizeSupplierAction(supplier, item).IsSuccess);

        // 2. Service token -> ServiceTokenNotAllowed
        IRentalCallerContext service = CreateServiceCaller();
        Result serviceRes = RentalAuthorizationPolicy.AuthorizeSupplierAction(service, item);
        Assert.True(serviceRes.IsFailure);
        Assert.Equal("Auth.ServiceTokenNotAllowed", serviceRes.Error.Code);

        // 3. User with Owner/Member role of another organization -> same NotFound as a missing item
        IRentalCallerContext otherOrgOwner = CreateUserCaller(Guid.NewGuid(), roles: ["owner", "member"]);
        Result mismatchRes = RentalAuthorizationPolicy.AuthorizeSupplierAction(otherOrgOwner, item);
        Assert.True(mismatchRes.IsFailure);
        Assert.Equal(RentalErrors.ItemNotFound, mismatchRes.Error);
    }

    [Fact]
    public void AuthorizeDisputeResolution_StrictlyRestrictedToAdmins()
    {
        // 1. Normal user -> AdminOnly
        IRentalCallerContext normalUser = CreateUserCaller(Guid.NewGuid());
        Result normalRes = RentalAuthorizationPolicy.AuthorizeDisputeResolution(normalUser);
        Assert.True(normalRes.IsFailure);
        Assert.Equal("Dispute.AdminOnly", normalRes.Error.Code);

        // 2. Service token -> ServiceTokenNotAllowed
        IRentalCallerContext service = CreateServiceCaller();
        Result serviceRes = RentalAuthorizationPolicy.AuthorizeDisputeResolution(service);
        Assert.True(serviceRes.IsFailure);
        Assert.Equal("Auth.ServiceTokenNotAllowed", serviceRes.Error.Code);

        // 3. Platform Admin -> Success
        IRentalCallerContext platformAdmin = CreateUserCaller(Guid.NewGuid(), roles: ["admin_platform"]);
        Assert.True(RentalAuthorizationPolicy.AuthorizeDisputeResolution(platformAdmin).IsSuccess);
    }

    [Fact]
    public void AuthorizeEvidenceRead_ValidatesOwnershipAndMediaBelonging()
    {
        Guid renterId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();
        Guid evidenceMediaId = Guid.NewGuid();
        Guid unrelatedMediaId = Guid.NewGuid();

        RentalOrder order = CreateTestOrder(renterId, supplierId);
        RentalItem item = order.Items.First();
        RentalUnitAssignment unit = item.UnitAssignments.First();
        unit.RecordHandover(Guid.NewGuid(), UnitCondition.Normal, "Good", [evidenceMediaId]);
        // 1. Renter -> Success
        IRentalCallerContext renter = CreateUserCaller(renterId);
        Assert.True(RentalAuthorizationPolicy.AuthorizeEvidenceRead(renter, order, item, evidenceMediaId).IsSuccess);

        // 2. Supplier -> Success
        IRentalCallerContext supplier = CreateUserCaller(supplierId);
        Assert.True(RentalAuthorizationPolicy.AuthorizeEvidenceRead(supplier, order, item, evidenceMediaId).IsSuccess);

        // 3. Admin -> Success
        IRentalCallerContext admin = CreateUserCaller(Guid.NewGuid(), roles: ["admin"]);
        Assert.True(RentalAuthorizationPolicy.AuthorizeEvidenceRead(admin, order, item, evidenceMediaId).IsSuccess);

        // 4. Stranger -> the same EvidenceNotFound as an unrelated media id (evidence ids cannot be probed)
        IRentalCallerContext stranger = CreateUserCaller(Guid.NewGuid());
        Result strangerRes = RentalAuthorizationPolicy.AuthorizeEvidenceRead(stranger, order, item, evidenceMediaId);
        Assert.True(strangerRes.IsFailure);
        Assert.Equal("Media.EvidenceNotFound", strangerRes.Error.Code);
        Result unrelatedForStranger = RentalAuthorizationPolicy.AuthorizeEvidenceRead(stranger, order, item, unrelatedMediaId);
        Assert.Equal(strangerRes.Error, unrelatedForStranger.Error);

        // 5. Service token -> ServiceTokenNotAllowed
        IRentalCallerContext service = CreateServiceCaller();
        Result serviceRes = RentalAuthorizationPolicy.AuthorizeEvidenceRead(service, order, item, evidenceMediaId);
        Assert.True(serviceRes.IsFailure);
        Assert.Equal("Auth.ServiceTokenNotAllowed", serviceRes.Error.Code);

        // 6. Media ID does not belong to item evidence -> EvidenceNotFound
        Result notFoundRes = RentalAuthorizationPolicy.AuthorizeEvidenceRead(renter, order, item, unrelatedMediaId);
        Assert.True(notFoundRes.IsFailure);
        Assert.Equal("Media.EvidenceNotFound", notFoundRes.Error.Code);
    }

    private static RentalCallerContext CreateUserCaller(Guid userId, bool isProfileCompleted = true, IEnumerable<string>? roles = null)
    {
        List<KeyValuePair<string, string>> claims =
        [
            new(RentalAuthClaims.Subject, userId.ToString("D")),
            new(RentalAuthClaims.ProfileCompleted, isProfileCompleted ? "true" : "false")
        ];
        if (roles is not null)
        {
            claims.Add(new(RentalAuthClaims.Roles, string.Join(",", roles)));
        }
        return RentalCallerContext.FromClaims(claims);
    }

    private static RentalCallerContext CreateServiceCaller(Guid? serviceId = null, IEnumerable<string>? roles = null)
    {
        List<KeyValuePair<string, string>> claims =
        [
            new(RentalAuthClaims.Subject, (serviceId ?? Guid.NewGuid()).ToString("D")),
            new(RentalAuthClaims.UserTypeOrAlias, RentalAuthClaims.ServiceIndicator)
        ];
        if (roles is not null)
        {
            claims.Add(new(RentalAuthClaims.Roles, string.Join(",", roles)));
        }
        return RentalCallerContext.FromClaims(claims);
    }

    private static RentalOrder CreateTestOrder(Guid renterId, Guid supplierId)
    {
        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        Guid unitId = Guid.NewGuid();

        InitialRentalUnitAssignmentDto unit = new(unitId, Guid.NewGuid(), Guid.NewGuid());
        InitialRentalItemDto item = new(
            itemId,
            Guid.NewGuid(),
            supplierId,
            1,
            new(2026, 10, 10),
            new(2026, 10, 15),
            "Camera",
            "Desc",
            "Loc",
            200_000m,
            500_000m,
            "grp-01",
            [unit],
            TestListingSnapshots.Default);

        return RentalOrder.Create(
            orderId,
            "ORD-TEST-AUTH",
            renterId,
            "VND",
            "Pickup",
            "Customer",
            "0909123456",
            "Address",
            null,
            null,
            "v1.0",
            "doc-01",
            "hash-01",
            "op-create-auth",
            DateTimeOffset.UtcNow,
            [item]).Value;
    }
}
