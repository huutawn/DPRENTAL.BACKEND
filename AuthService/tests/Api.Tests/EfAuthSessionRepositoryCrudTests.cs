
using Dprental.AuthService.Domain.Entities;

using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class EfAuthSessionRepositoryCrudTests
{
    [Theory]
    [InlineData(Dprental.AuthService.Common.Enums.MembershipStatus.Active, Dprental.AuthService.Common.Enums.OrganizationStatus.Active, true)]
    [InlineData(Dprental.AuthService.Common.Enums.MembershipStatus.Invited, Dprental.AuthService.Common.Enums.OrganizationStatus.Active, false)]
    [InlineData(Dprental.AuthService.Common.Enums.MembershipStatus.Suspended, Dprental.AuthService.Common.Enums.OrganizationStatus.Active, false)]
    [InlineData(Dprental.AuthService.Common.Enums.MembershipStatus.Active, Dprental.AuthService.Common.Enums.OrganizationStatus.Pending, false)]
    [InlineData(Dprental.AuthService.Common.Enums.MembershipStatus.Active, Dprental.AuthService.Common.Enums.OrganizationStatus.Suspended, false)]
    public async Task NewSessionOnlyGrantsPermissionsFromActiveMemberships(
        Dprental.AuthService.Common.Enums.MembershipStatus membershipStatus,
        Dprental.AuthService.Common.Enums.OrganizationStatus organizationStatus, bool allowed)
    {
        await using var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var permission = new Permission { Id = Guid.NewGuid(), Code = "inventory.asset.manage" };
        var role = new Role { Id = Guid.NewGuid(), Name = "Owner" };
        role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id, Permission = permission });
        var organization = new Organization { Id = Guid.NewGuid(), Status = organizationStatus, Name = "Supplier", Slug = Guid.NewGuid().ToString("N") };
        var user = new User { Id = Guid.NewGuid(), Email = "owner@test.local", IsActive = true };
        user.OrganizationMemberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            OrganizationId = organization.Id,
            Organization = organization,
            RoleId = role.Id,
            Role = role,
            Status = membershipStatus
        });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var repository = new EfAuthSessionRepository(db, TimeProvider.System);
        var now = DateTime.UtcNow;
        var principal = await repository.CreateAsync(user.Id, Guid.NewGuid().ToString("N"), now, now.AddDays(1), null, null);
        Assert.NotNull(principal);
        Assert.Equal(allowed, principal.Permissions.Contains(permission.Code));
        Assert.Equal(allowed, principal.OrganizationIds.Contains(organization.Id));
    }

    [Fact]
    public async Task CrudMethodsPersistAndRemoveSession()
    {
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using AuthDbContext dbContext = new(options);
        EfAuthSessionRepository repository = new(dbContext, TimeProvider.System);
        Session session = new()
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30)
        };

        Session created = await repository.CreateAsync(session);
        Session? found = await repository.GetByIdAsync(session.Id);
        IReadOnlyList<Session> sessions = await repository.GetAllAsync();

        Assert.Same(session, created);
        Assert.NotNull(found);
        Assert.Single(sessions);

        session.RevokedAtUtc = DateTime.UtcNow;
        Assert.True(await repository.UpdateAsync(session));
        Assert.Equal(session.RevokedAtUtc, (await repository.GetByIdAsync(session.Id))!.RevokedAtUtc);

        Assert.True(await repository.DeleteAsync(session.Id));
        Assert.Null(await repository.GetByIdAsync(session.Id));
        Assert.False(await repository.DeleteAsync(session.Id));
    }
}
