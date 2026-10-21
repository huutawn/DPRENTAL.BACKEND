using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Security;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test method naming convention")]
public sealed class UserAccessResolverTests
{
    [Theory]
    [InlineData(MembershipStatus.Active, OrganizationStatus.Active, true)]
    [InlineData(MembershipStatus.Active, OrganizationStatus.Pending, false)]
    [InlineData(MembershipStatus.Active, OrganizationStatus.Rejected, false)]
    [InlineData(MembershipStatus.Active, OrganizationStatus.Suspended, false)]
    [InlineData(MembershipStatus.Invited, OrganizationStatus.Active, false)]
    [InlineData(MembershipStatus.Suspended, OrganizationStatus.Active, false)]
    public void EffectiveRoles_AccumulatesCustomerAndOwner_OnlyWhenBothMembershipAndOrganizationAreActive(
        MembershipStatus membershipStatus,
        OrganizationStatus organizationStatus,
        bool expectOwnerRole)
    {
        Role customerRole = new() { Id = Guid.NewGuid(), Name = SystemRoleCode.Customer, Scope = RoleScope.Global };
        Role ownerRole = new() { Id = Guid.NewGuid(), Name = SystemRoleCode.Owner, Scope = RoleScope.Organization };

        Organization org = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Org",
            Status = organizationStatus
        };

        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "user@test.local",
            IsActive = true
        };

        user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = customerRole.Id, Role = customerRole });
        user.OrganizationMemberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            OrganizationId = org.Id,
            Organization = org,
            RoleId = ownerRole.Id,
            Role = ownerRole,
            Status = membershipStatus
        });

        string[] roles = UserAccessResolver.EffectiveRoles(user);

        if (expectOwnerRole)
        {
            Assert.Equal([SystemRoleCode.Customer, SystemRoleCode.Owner], roles);
        }
        else
        {
            Assert.Equal([SystemRoleCode.Customer], roles);
        }
    }

    [Fact]
    public void EffectiveRoles_MultipleActiveMemberships_DoesNotDuplicateRoles()
    {
        Role customerRole = new() { Id = Guid.NewGuid(), Name = SystemRoleCode.Customer, Scope = RoleScope.Global };
        Role ownerRole = new() { Id = Guid.NewGuid(), Name = SystemRoleCode.Owner, Scope = RoleScope.Organization };

        Organization org1 = new() { Id = Guid.NewGuid(), Name = "Org 1", Status = OrganizationStatus.Active };
        Organization org2 = new() { Id = Guid.NewGuid(), Name = "Org 2", Status = OrganizationStatus.Active };

        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "multi@test.local",
            IsActive = true
        };

        user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = customerRole.Id, Role = customerRole });
        user.OrganizationMemberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            OrganizationId = org1.Id,
            Organization = org1,
            RoleId = ownerRole.Id,
            Role = ownerRole,
            Status = MembershipStatus.Active
        });
        user.OrganizationMemberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            OrganizationId = org2.Id,
            Organization = org2,
            RoleId = ownerRole.Id,
            Role = ownerRole,
            Status = MembershipStatus.Active
        });

        string[] roles = UserAccessResolver.EffectiveRoles(user);
        Assert.Equal([SystemRoleCode.Customer, SystemRoleCode.Owner], roles);

        Guid[] orgIds = UserAccessResolver.OrganizationIds(user);
        Assert.Equal(2, orgIds.Length);
        Assert.Contains(org1.Id, orgIds);
        Assert.Contains(org2.Id, orgIds);
    }

    [Fact]
    public void EffectiveRoles_UserWithoutCustomerInDb_DoesNotGetCustomerRole()
    {
        Role ownerRole = new() { Id = Guid.NewGuid(), Name = SystemRoleCode.Owner, Scope = RoleScope.Organization };
        Organization org = new() { Id = Guid.NewGuid(), Name = "Test Org", Status = OrganizationStatus.Active };

        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "nocustomer@test.local",
            IsActive = true
        };

        user.OrganizationMemberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            OrganizationId = org.Id,
            Organization = org,
            RoleId = ownerRole.Id,
            Role = ownerRole,
            Status = MembershipStatus.Active
        });

        string[] roles = UserAccessResolver.EffectiveRoles(user);
        Assert.Equal([SystemRoleCode.Owner], roles);
        Assert.DoesNotContain(SystemRoleCode.Customer, roles);
    }

    [Fact]
    public void Permissions_CombinesGlobalAndActiveOrgPermissions()
    {
        Permission globalPerm = new() { Id = Guid.NewGuid(), Code = "users.read" };
        Permission orgPerm = new() { Id = Guid.NewGuid(), Code = "inventory.manage" };
        Permission inactiveOrgPerm = new() { Id = Guid.NewGuid(), Code = "secret.manage" };

        Role customerRole = new() { Id = Guid.NewGuid(), Name = SystemRoleCode.Customer, Scope = RoleScope.Global };
        customerRole.RolePermissions.Add(new RolePermission { RoleId = customerRole.Id, PermissionId = globalPerm.Id, Permission = globalPerm });

        Role ownerRole = new() { Id = Guid.NewGuid(), Name = SystemRoleCode.Owner, Scope = RoleScope.Organization };
        ownerRole.RolePermissions.Add(new RolePermission { RoleId = ownerRole.Id, PermissionId = orgPerm.Id, Permission = orgPerm });

        Role inactiveRole = new() { Id = Guid.NewGuid(), Name = "Member", Scope = RoleScope.Organization };
        inactiveRole.RolePermissions.Add(new RolePermission { RoleId = inactiveRole.Id, PermissionId = inactiveOrgPerm.Id, Permission = inactiveOrgPerm });

        Organization activeOrg = new() { Id = Guid.NewGuid(), Name = "Active Org", Status = OrganizationStatus.Active };
        Organization pendingOrg = new() { Id = Guid.NewGuid(), Name = "Pending Org", Status = OrganizationStatus.Pending };

        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "perms@test.local",
            IsActive = true
        };

        user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = customerRole.Id, Role = customerRole });
        user.OrganizationMemberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            OrganizationId = activeOrg.Id,
            Organization = activeOrg,
            RoleId = ownerRole.Id,
            Role = ownerRole,
            Status = MembershipStatus.Active
        });
        user.OrganizationMemberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            OrganizationId = pendingOrg.Id,
            Organization = pendingOrg,
            RoleId = inactiveRole.Id,
            Role = inactiveRole,
            Status = MembershipStatus.Active
        });

        string[] perms = UserAccessResolver.Permissions(user);
        Assert.Contains("users.read", perms);
        Assert.Contains("inventory.manage", perms);
        Assert.DoesNotContain("secret.manage", perms);
    }
}
