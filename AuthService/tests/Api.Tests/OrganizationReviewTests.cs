using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

/// <summary>Vòng đời duyệt organization tự đăng ký: Platform duyệt/từ chối, Owner xem và gửi lại hồ sơ.</summary>
public sealed class OrganizationReviewTests
{
    private static readonly Guid OwnerRoleId = new("a1010000-0000-0000-0000-000000000001");
    private static readonly Guid PlatformRoleId = Guid.NewGuid();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApprovalRejectsIdentifiersOfApprovedOrganization(bool duplicateSlug)
    {
        await using Fixture f = await Fixture.CreateAsync();
        Organization pending = await f.ReloadOrgAsync();
        f.Db.Organizations.Add(new Organization
        {
            Id = Guid.NewGuid(),
            Name = "Approved",
            Slug = duplicateSlug ? pending.Slug : "other-approved",
            TaxCode = duplicateSlug ? "0399999999" : pending.TaxCode,
            Status = OrganizationStatus.Active
        });
        await f.Db.SaveChangesAsync();

        var result = await f.Review.ApproveAsync(f.AdminId, f.OrgId);

        Assert.True(result.IsFailure);
        Assert.Equal(duplicateSlug ? AuthErrors.Organization.SlugAlreadyExists : AuthErrors.Organization.TaxCodeAlreadyExists, result.Error);
        Assert.Equal(OrganizationStatus.Pending, (await f.ReloadOrgAsync()).Status);
        Assert.Empty(f.Db.AuditLogs);
    }

    [Fact]
    public async Task ApprovePendingOrganizationActivatesAndRecordsReviewer()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Result<OrganizationResponse> result = await f.Review.ApproveAsync(f.AdminId, f.OrgId);

        Assert.True(result.IsSuccess);
        Organization org = await f.ReloadOrgAsync();
        Assert.Equal(OrganizationStatus.Active, org.Status);
        Assert.Equal(f.AdminId, org.ReviewedByUserId);
        Assert.NotNull(org.ReviewedAtUtc);
        Assert.Null(org.RejectionReason);
        Assert.Single(f.Db.AuditLogs, log => log.Action == "Organization_Approve" && log.Target == f.OrgId.ToString());
    }

    [Fact]
    public async Task RejectPendingOrganizationStoresReason()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Result<OrganizationResponse> result = await f.Review.RejectAsync(f.AdminId, f.OrgId, new RejectOrganizationRequest("  Giấy phép bị mờ  "));

        Assert.True(result.IsSuccess);
        Assert.Equal("Giấy phép bị mờ", result.Value.RejectionReason);
        Organization org = await f.ReloadOrgAsync();
        Assert.Equal(OrganizationStatus.Rejected, org.Status);
        Assert.Equal(f.AdminId, org.ReviewedByUserId);
    }

    [Fact]
    public async Task SecondReviewAfterDecisionReturnsConflictAndKeepsFirstDecision()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.Review.ApproveAsync(f.AdminId, f.OrgId);

        // Admin thứ hai bấm từ chối sau khi admin đầu đã duyệt: không được ghi đè.
        Result<OrganizationResponse> late = await f.Review.RejectAsync(f.AdminId, f.OrgId, new RejectOrganizationRequest("Trễ"));

        Assert.True(late.IsFailure);
        Assert.Equal(AuthErrors.Organization.NotPendingReview, late.Error);
        Assert.Equal(OrganizationStatus.Active, (await f.ReloadOrgAsync()).Status);
    }

    [Fact]
    public async Task NonAdminCannotReview()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Result<OrganizationResponse> approve = await f.Review.ApproveAsync(f.OwnerId, f.OrgId);

        Assert.True(approve.IsFailure);
        Assert.Equal(AuthErrors.Organization.PlatformAdminRequired, approve.Error);
        Assert.Equal(OrganizationStatus.Pending, (await f.ReloadOrgAsync()).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectRequiresReason(string reason)
    {
        await using Fixture f = await Fixture.CreateAsync();

        Result<OrganizationResponse> result = await f.Review.RejectAsync(f.AdminId, f.OrgId, new RejectOrganizationRequest(reason));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal(OrganizationStatus.Pending, (await f.ReloadOrgAsync()).Status);
    }

    [Fact]
    public async Task PatchIsActiveNoLongerApprovesPendingOrganization()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Result result = await f.Organizations.UpdateAsync(f.AdminId, f.OrgId, new UpdateOrganizationRequest(IsActive: true));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.InvalidStateTransition, result.Error);
        Assert.Equal(OrganizationStatus.Pending, (await f.ReloadOrgAsync()).Status);
    }

    [Fact]
    public async Task OnlyPlatformCanRenameActiveOrganizationViaPatch()
    {
        await using Fixture f = await Fixture.CreateAsync(OrganizationStatus.Active);

        Result owner = await f.Organizations.UpdateAsync(f.OwnerId, f.OrgId, new UpdateOrganizationRequest(Name: "Owner Rename"));
        Result admin = await f.Organizations.UpdateAsync(f.AdminId, f.OrgId, new UpdateOrganizationRequest(Name: "Admin Rename"));

        Assert.True(owner.IsFailure);
        Assert.Equal(AuthErrors.Organization.LegalInfoLocked, owner.Error);
        Assert.True(admin.IsSuccess);
        Assert.Equal("Admin Rename", (await f.ReloadOrgAsync()).Name);
    }

    [Fact]
    public async Task PendingQueueListsOnlyPendingOldestFirstForAdmin()
    {
        await using Fixture f = await Fixture.CreateAsync();
        Guid older = await f.AddOrganizationAsync("Older", OrganizationStatus.Pending, createdAt: DateTime.UtcNow.AddDays(-3));
        await f.AddOrganizationAsync("Approved", OrganizationStatus.Active, createdAt: DateTime.UtcNow.AddDays(-5));

        Result<PagedResult<OrganizationSummaryResponse>> result = await f.Organizations.ListAsync(f.AdminId, new PageRequest(), OrganizationStatus.Pending);

        Assert.True(result.IsSuccess);
        Assert.Equal([older, f.OrgId], result.Value.Items.Select(o => o.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(OrganizationStatus.Pending)]
    public async Task NonAdminCannotListOrganizations(OrganizationStatus? status)
    {
        await using Fixture f = await Fixture.CreateAsync();

        // User thường (kể cả Owner) xem tổ chức của mình qua GET /users/organizations.
        Result<PagedResult<OrganizationSummaryResponse>> result = await f.Organizations.ListAsync(f.OwnerId, new PageRequest(), status);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.PlatformAdminRequired, result.Error);
    }

    [Fact]
    public async Task OwnerSeesOwnOrganizationViaMyOrganizations()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Result<IReadOnlyList<MyOrganizationResponse>> result = await f.Organizations.GetMyOrganizationsAsync(f.OwnerId);

        Assert.True(result.IsSuccess);
        MyOrganizationResponse mine = Assert.Single(result.Value);
        Assert.Equal(f.OrgId, mine.OrganizationId);
        Assert.Equal(OrganizationStatus.Pending, mine.OrganizationStatus);
    }

    [Theory]
    [InlineData(OrganizationStatus.Pending)]
    [InlineData(OrganizationStatus.Rejected)]
    public async Task OwnerCanViewOwnOrganizationBeforeApproval(OrganizationStatus status)
    {
        await using Fixture f = await Fixture.CreateAsync(status);

        Result<OrganizationResponse> owner = await f.Organizations.GetByIdAsync(f.OwnerId, f.OrgId);
        Result<OrganizationResponse> outsider = await f.Organizations.GetByIdAsync(f.OutsiderId, f.OrgId);

        Assert.True(owner.IsSuccess);
        Assert.Equal(status, owner.Value.Status);
        Assert.True(outsider.IsFailure);
        Assert.Equal(AuthErrors.Organization.CrossOrganizationAccessDenied, outsider.Error);
    }

    [Fact]
    public async Task OwnerEditingRejectedOrganizationResubmitsIt()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.Review.RejectAsync(f.AdminId, f.OrgId, new RejectOrganizationRequest("Sai mã số thuế"));

        Result<OrganizationResponse> result = await f.Registration.UpdateProfileAsync(
            f.OwnerId, f.OrgId, Profile() with { TaxCode = "0399999999" });

        Assert.True(result.IsSuccess);
        Organization org = await f.ReloadOrgAsync();
        Assert.Equal(OrganizationStatus.Pending, org.Status);
        Assert.Equal("0399999999", org.TaxCode);
        Assert.Null(org.RejectionReason);
        Assert.Null(org.ReviewedByUserId);
        Assert.Single(f.Db.AuditLogs, log => log.Action == "Organization_Resubmit");
    }

    [Fact]
    public async Task ActiveOrganizationAllowsContactChangesOnly()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.Registration.UpdateProfileAsync(f.OwnerId, f.OrgId, Profile());
        await f.Review.ApproveAsync(f.AdminId, f.OrgId);

        Result<OrganizationResponse> contact = await f.Registration.UpdateProfileAsync(
            f.OwnerId, f.OrgId, Profile() with { ContactPhone = "0909999999", Description = "Cho thuê thiết bị" });
        Result<OrganizationResponse> legal = await f.Registration.UpdateProfileAsync(
            f.OwnerId, f.OrgId, Profile() with { Name = "Tên khác" });

        Assert.True(contact.IsSuccess);
        Assert.Equal("0909999999", contact.Value.ContactPhone);
        Assert.Equal(OrganizationStatus.Active, contact.Value.Status);
        Assert.True(legal.IsFailure);
        Assert.Equal(AuthErrors.Organization.LegalInfoLocked, legal.Error);
        Assert.Equal("Dong A Rental", (await f.ReloadOrgAsync()).Name);
    }

    [Fact]
    public async Task OnlyOwnerCanEditProfile()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Result<OrganizationResponse> result = await f.Registration.UpdateProfileAsync(f.OutsiderId, f.OrgId, Profile());

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.Forbidden, result.Error);
    }

    [Fact]
    public async Task SuspendedOrganizationCannotBeEdited()
    {
        await using Fixture f = await Fixture.CreateAsync(OrganizationStatus.Suspended);

        Result<OrganizationResponse> result = await f.Registration.UpdateProfileAsync(f.OwnerId, f.OrgId, Profile());

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.InactiveOrganization, result.Error);
    }

    [Fact]
    public async Task ChangingTaxCodeToAnotherOrganizationsReturnsConflict()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.AddOrganizationAsync("Other", OrganizationStatus.Active, taxCode: "0311111111");

        Result<OrganizationResponse> result = await f.Registration.UpdateProfileAsync(
            f.OwnerId, f.OrgId, Profile() with { TaxCode = "0311111111" });

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.TaxCodeAlreadyExists, result.Error);
    }

    private static UpdateOrganizationProfileRequest Profile() =>
        new(
            Name: "Dong A Rental",
            TaxCode: "0312345678",
            LegalRepresentativeName: "Nguyen Van A",
            ContactEmail: "contact@donga.vn",
            ContactPhone: "0281234567",
            Address: "12 Nguyen Hue",
            Province: "TP. Ho Chi Minh",
            Ward: "Phuong Sai Gon",
            BusinessLicenseDocumentMediaId: Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(AuthDbContext db) => Db = db;

        public AuthDbContext Db { get; }
        public Guid AdminId { get; } = Guid.NewGuid();
        public Guid OwnerId { get; } = Guid.NewGuid();
        public Guid OutsiderId { get; } = Guid.NewGuid();
        public Guid OrgId { get; } = Guid.NewGuid();

        public OrganizationReviewService Review => new(
            new EfOrganizationRepository(Db),
            new EfOrganizationMembershipRepository(Db),
            new EfRoleRepository(Db),
            new EfAuditLogRepository(Db),
            TimeProvider.System,
            FixedCorrelationIdProvider.Instance,
            FakeMediaUrlResolver.Instance);

        public OrganizationRegistrationService Registration => new(
            new EfOrganizationRepository(Db),
            new EfOrganizationMembershipRepository(Db),
            new EfUserRepository(Db),
            new EfRoleRepository(Db),
            new EfAuditLogRepository(Db),
            TimeProvider.System,
            FixedCorrelationIdProvider.Instance,
            new FakeMediaServiceClient { AutoVerify = true, DefaultUserId = OwnerId },
            FakeMediaUrlResolver.Instance,
            new FakeMediaOutboxService());

        public OrganizationService Organizations => new(
            new EfOrganizationRepository(Db),
            new EfOrganizationMembershipRepository(Db),
            new EfRoleRepository(Db),
            FakeMediaUrlResolver.Instance);

        public static async Task<Fixture> CreateAsync(OrganizationStatus status = OrganizationStatus.Pending)
        {
            AuthDbContext db = new(new DbContextOptionsBuilder<AuthDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
            Fixture f = new(db);

            Permission suspend = new() { Id = Guid.NewGuid(), Code = PermissionCode.IdentityOrganizationsSuspend };
            Permission read = new() { Id = Guid.NewGuid(), Code = PermissionCode.IdentityOrganizationsRead };
            Permission update = new() { Id = Guid.NewGuid(), Code = PermissionCode.IdentityOrganizationsUpdate };
            db.Permissions.AddRange(suspend, read, update);
            db.Roles.Add(new Role { Id = OwnerRoleId, Name = SystemRoleCode.Owner, Scope = RoleScope.Organization });
            db.Roles.Add(new Role { Id = PlatformRoleId, Name = SystemRoleCode.AdminPlatform, Scope = RoleScope.Global });
            db.RolePermissions.AddRange(
                new RolePermission { RoleId = PlatformRoleId, PermissionId = suspend.Id },
                new RolePermission { RoleId = PlatformRoleId, PermissionId = read.Id },
                new RolePermission { RoleId = PlatformRoleId, PermissionId = update.Id },
                new RolePermission { RoleId = OwnerRoleId, PermissionId = update.Id });

            foreach ((Guid id, string email) in new[] { (f.AdminId, "admin@x.vn"), (f.OwnerId, "owner@x.vn"), (f.OutsiderId, "outsider@x.vn") })
            {
                db.Users.Add(new User { Id = id, Email = email, FullName = email, PasswordHash = "hash", IsActive = true, ProfileCompletedAtUtc = DateTime.UtcNow });
            }

            db.UserRoles.Add(new UserRole { UserId = f.AdminId, RoleId = PlatformRoleId });
            await db.SaveChangesAsync();

            await f.AddOrganizationAsync("Dong A Rental", status, id: f.OrgId, taxCode: "0312345678", createdAt: DateTime.UtcNow.AddDays(-1));
            db.OrganizationMemberships.Add(new OrganizationMembership
            {
                Id = Guid.NewGuid(),
                UserId = f.OwnerId,
                OrganizationId = f.OrgId,
                RoleId = OwnerRoleId,
                Status = MembershipStatus.Active,
                JoinedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return f;
        }

        public async Task<Guid> AddOrganizationAsync(
            string name,
            OrganizationStatus status,
            Guid? id = null,
            string? taxCode = null,
            DateTime? createdAt = null)
        {
            Organization org = new()
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Slug = $"org-{Guid.NewGuid():N}",
                Type = OrganizationType.Supplier,
                Status = status,
                TaxCode = taxCode,
                CreatedAtUtc = createdAt ?? DateTime.UtcNow,
            };
            Db.Organizations.Add(org);
            await Db.SaveChangesAsync();
            Db.Entry(org).State = EntityState.Detached;
            return org.Id;
        }

        public Task<Organization> ReloadOrgAsync() =>
            Db.Organizations.AsNoTracking().SingleAsync(o => o.Id == OrgId);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
