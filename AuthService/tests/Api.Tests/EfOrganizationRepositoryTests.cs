
using Dprental.AuthService.Domain.Entities;


using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class EfOrganizationRepositoryTests
{
    [Fact]
    public async Task CreateAsyncPersistsOrganizationAndInitialMembershipAtomically()
    {
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using AuthDbContext dbContext = new(options);
        EfOrganizationRepository orgRepo = new(dbContext);

        Guid orgId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        Guid roleId = Guid.NewGuid();

        Organization org = new()
        {
            Id = orgId,
            Name = "Acme Corp",
            Slug = "acme-corp",
            Type = OrganizationType.Supplier,
            Status = OrganizationStatus.Active,
            CreatedAtUtc = DateTime.UtcNow
        };

        OrganizationMembership membership = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrganizationId = orgId,
            RoleId = roleId,
            Status = MembershipStatus.Active,
            JoinedAtUtc = DateTime.UtcNow
        };

        Assert.False(await orgRepo.SlugExistsAsync(org.Slug));
        Organization created = await orgRepo.CreateAsync(org, membership);

        Assert.Equal(orgId, created.Id);
        Assert.True(await orgRepo.SlugExistsAsync("acme-corp"));

        Organization? fetched = await orgRepo.GetByIdAsync(orgId);
        Assert.NotNull(fetched);
        Assert.Equal("Acme Corp", fetched.Name);

        Organization? bySlug = await orgRepo.GetBySlugAsync("acme-corp");
        Assert.NotNull(bySlug);
        Assert.Equal(orgId, bySlug.Id);
    }


    [Fact]
    public async Task MembershipRepositoryCountsOwnersCorrectly()
    {
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using AuthDbContext dbContext = new(options);
        EfOrganizationMembershipRepository memRepo = new(dbContext);

        Guid orgId = Guid.NewGuid();
        Role ownerRole = new() { Id = Guid.NewGuid(), Name = "Owner", Scope = RoleScope.Organization };
        Role memberRole = new() { Id = Guid.NewGuid(), Name = "Member", Scope = RoleScope.Organization };
        dbContext.Roles.AddRange(ownerRole, memberRole);

        OrganizationMembership activeOwner = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            UserId = Guid.NewGuid(),
            RoleId = ownerRole.Id,
            Role = ownerRole,
            Status = MembershipStatus.Active,
            JoinedAtUtc = DateTime.UtcNow
        };

        OrganizationMembership suspendedOwner = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            UserId = Guid.NewGuid(),
            RoleId = ownerRole.Id,
            Role = ownerRole,
            Status = MembershipStatus.Suspended,
            JoinedAtUtc = DateTime.UtcNow
        };

        OrganizationMembership activeMember = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            UserId = Guid.NewGuid(),
            RoleId = memberRole.Id,
            Role = memberRole,
            Status = MembershipStatus.Active,
            JoinedAtUtc = DateTime.UtcNow
        };

        dbContext.OrganizationMemberships.AddRange(activeOwner, suspendedOwner, activeMember);
        await dbContext.SaveChangesAsync();

        Assert.Equal(2, await memRepo.CountOwnersAsync(orgId));
        Assert.Equal(1, await memRepo.CountActiveOwnersAsync(orgId));
    }

    [Fact]
    public async Task PermissionsCombinesGlobalAndActiveScopedPermissions()
    {
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using AuthDbContext dbContext = new(options);
        EfUserRepository userRepo = new(dbContext);

        Guid userId = Guid.NewGuid();
        Guid orgId = Guid.NewGuid();

        Permission pGlobal = new() { Id = Guid.NewGuid(), Code = "global.perm" };
        Permission pScoped = new() { Id = Guid.NewGuid(), Code = "scoped.perm" };
        Permission pInactive = new() { Id = Guid.NewGuid(), Code = "inactive.perm" };

        Role globalRole = new() { Id = Guid.NewGuid(), Name = "GlobalAdmin", Scope = RoleScope.Global };
        globalRole.RolePermissions.Add(new RolePermission { RoleId = globalRole.Id, PermissionId = pGlobal.Id, Permission = pGlobal });

        Role activeOrgRole = new() { Id = Guid.NewGuid(), Name = "Owner", Scope = RoleScope.Organization };
        activeOrgRole.RolePermissions.Add(new RolePermission { RoleId = activeOrgRole.Id, PermissionId = pScoped.Id, Permission = pScoped });

        Role inactiveOrgRole = new() { Id = Guid.NewGuid(), Name = "SuspendedRole", Scope = RoleScope.Organization };
        inactiveOrgRole.RolePermissions.Add(new RolePermission { RoleId = inactiveOrgRole.Id, PermissionId = pInactive.Id, Permission = pInactive });

        dbContext.Permissions.AddRange(pGlobal, pScoped, pInactive);
        dbContext.Roles.AddRange(globalRole, activeOrgRole, inactiveOrgRole);
        dbContext.Users.Add(new User { Id = userId, Email = "test@example.com", PasswordHash = "hash", FullName = "Test", IsActive = true });
        dbContext.UserRoles.Add(new UserRole { UserId = userId, RoleId = globalRole.Id, Role = globalRole });

        dbContext.OrganizationMemberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrganizationId = orgId,
            RoleId = activeOrgRole.Id,
            Role = activeOrgRole,
            Organization = new Organization { Id = orgId, Name = "Org", Slug = "org", Status = OrganizationStatus.Active },
            Status = MembershipStatus.Active,
            JoinedAtUtc = DateTime.UtcNow
        });

        await dbContext.SaveChangesAsync();

        User? user = await userRepo.GetByIdWithAccessAsync(userId);
        Assert.NotNull(user);
        IReadOnlyList<string> effective = Dprental.AuthService.Application.Security.UserAccessResolver.Permissions(user);

        Assert.Contains("global.perm", effective);
        Assert.Contains("scoped.perm", effective);
        Assert.DoesNotContain("inactive.perm", effective);
    }
}
