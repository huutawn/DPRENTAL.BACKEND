using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Organization;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.IRepositories;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class OrganizationServiceTests
{
    private static readonly Guid ActorAdminId = Guid.NewGuid();
    private static readonly Guid ActorUserId = Guid.NewGuid();
    private static readonly Guid OwnerRoleId = new("a1010000-0000-0000-0000-000000000001");
    private static readonly Guid MemberRoleId = new("a1010000-0000-0000-0000-000000000002");

    [Theory]
    [InlineData("Invalid Slug")]
    [InlineData("slug_with_underscore")]
    [InlineData("-leading-hyphen")]
    [InlineData("trailing-hyphen-")]
    [InlineData("double--hyphen")]
    [InlineData("Uppercase")]
    public async Task CreateAsyncInvalidSlugFormatReturnsValidationError(string invalidSlug)
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        Result<OrganizationResponse> result = await service.CreateAsync(
            ActorAdminId,
            new CreateOrganizationRequest("Acme Corp", invalidSlug, OrganizationType.Supplier));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.InvalidSlugFormat, result.Error);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsyncEmptyNameReturnsValidationError(string invalidName)
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        Result<OrganizationResponse> result = await service.CreateAsync(
            ActorAdminId,
            new CreateOrganizationRequest(invalidName, "valid-slug", OrganizationType.Supplier));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.InvalidName, result.Error);
    }

    [Fact]
    public async Task CreateAsyncPlatformOrganizationRequiresPlatformAdmin()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        userRepo.Users[ActorUserId] = new User { Id = ActorUserId, IsActive = true, Email = "user@example.com" };
        memRepo.GlobalPermissions[ActorUserId] = [Permissions.Organizations.Create];

        Result<OrganizationResponse> result = await service.CreateAsync(
            ActorUserId,
            new CreateOrganizationRequest("Platform Org", "platform-org", OrganizationType.Platform));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.PlatformAdminRequired, result.Error);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
    }

    [Fact]
    public async Task CreateAsyncSupplierWithoutPermissionReturnsForbidden()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        userRepo.Users[ActorUserId] = new User { Id = ActorUserId, IsActive = true, Email = "user@example.com" };
        memRepo.GlobalPermissions[ActorUserId] = [];

        Result<OrganizationResponse> result = await service.CreateAsync(
            ActorUserId,
            new CreateOrganizationRequest("Supplier Org", "supplier-org", OrganizationType.Supplier));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.CreationNotAllowed, result.Error);
    }

    [Fact]
    public async Task CreateAsyncExistingSlugReturnsConflict()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        userRepo.Users[ActorAdminId] = new User { Id = ActorAdminId, IsActive = true, Email = "admin@example.com" };
        memRepo.GlobalPermissions[ActorAdminId] = AdminPermissions;
        orgRepo.ExistingSlugs.Add("existing-slug");

        Result<OrganizationResponse> result = await service.CreateAsync(
            ActorAdminId,
            new CreateOrganizationRequest("Acme", "existing-slug", OrganizationType.Supplier));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.SlugAlreadyExists, result.Error);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    [Fact]
    public async Task CreateAsyncSuccessCreatesActiveOwnerMembershipAtomically()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo, new FakeNavigationCatalogRepository());

        userRepo.Users[ActorAdminId] = new User { Id = ActorAdminId, IsActive = true, Email = "admin@example.com" };
        memRepo.GlobalPermissions[ActorAdminId] = AdminPermissions;
        roleRepo.Roles[OwnerRoleId] = new Role { Id = OwnerRoleId, Name = "Owner", Scope = RoleScope.Organization };

        Result<OrganizationResponse> result = await service.CreateAsync(
            ActorAdminId,
            new CreateOrganizationRequest("Acme Corp", "acme-corp", OrganizationType.Supplier));

        Assert.True(result.IsSuccess);
        Assert.Equal("Acme Corp", result.Value.Name);
        Assert.Equal("acme-corp", result.Value.Slug);
        Assert.True(result.Value.IsActive);
        Assert.NotNull(orgRepo.LastCreatedInitialMembership);
        Assert.Equal(ActorAdminId, orgRepo.LastCreatedInitialMembership!.UserId);
        Assert.Equal(OwnerRoleId, orgRepo.LastCreatedInitialMembership.RoleId);
        Assert.Equal(MembershipStatus.Active, orgRepo.LastCreatedInitialMembership.Status);
    }

    [Fact]
    public async Task GetByIdAsyncInactiveOrganizationReturnsForbiddenForNormalUser()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        Guid orgId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Inactive", Slug = "inactive", Status = OrganizationStatus.Suspended };
        userRepo.Users[ActorUserId] = new User { Id = ActorUserId, IsActive = true };
        memRepo.GlobalPermissions[ActorUserId] = [];

        Result<OrganizationResponse> result = await service.GetByIdAsync(ActorUserId, orgId);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.InactiveOrganization, result.Error);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
    }

    [Fact]
    public async Task GetByIdAsyncCrossOrganizationRequestReturnsForbidden()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        Guid orgId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active };
        userRepo.Users[ActorUserId] = new User { Id = ActorUserId, IsActive = true };
        memRepo.GlobalPermissions[ActorUserId] = []; // Not platform admin, no membership

        Result<OrganizationResponse> result = await service.GetByIdAsync(ActorUserId, orgId);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.CrossOrganizationAccessDenied, result.Error);
    }

    [Fact]
    public async Task AddMemberAsyncCreatesInvitedMembership()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        Guid orgId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active };
        userRepo.Users[ActorAdminId] = new User { Id = ActorAdminId, IsActive = true };
        memRepo.GlobalPermissions[ActorAdminId] = AdminPermissions;

        userRepo.Users[targetUserId] = new User { Id = targetUserId, IsActive = true, Email = "target@example.com", FullName = "Target User" };
        roleRepo.Roles[MemberRoleId] = new Role { Id = MemberRoleId, Name = "Member", Scope = RoleScope.Organization };

        Result<OrganizationMemberResponse> result = await service.AddMemberAsync(
            ActorAdminId,
            orgId,
            new AddOrganizationMemberRequest(targetUserId, MemberRoleId));

        Assert.True(result.IsSuccess);
        Assert.Equal(MembershipStatus.Invited, result.Value.Status);
        Assert.Equal(targetUserId, result.Value.UserId);
        Assert.Equal(MemberRoleId, result.Value.RoleId);
    }

    [Fact]
    public async Task AddMemberAsyncRejectsGlobalRole()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        Guid orgId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Guid globalRoleId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active };
        userRepo.Users[ActorAdminId] = new User { Id = ActorAdminId, IsActive = true };
        memRepo.GlobalPermissions[ActorAdminId] = AdminPermissions;

        userRepo.Users[targetUserId] = new User { Id = targetUserId, IsActive = true };
        roleRepo.Roles[globalRoleId] = new Role { Id = globalRoleId, Name = "GlobalRole", Scope = RoleScope.Global };

        Result<OrganizationMemberResponse> result = await service.AddMemberAsync(
            ActorAdminId,
            orgId,
            new AddOrganizationMemberRequest(targetUserId, globalRoleId));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.InvalidRoleScope, result.Error);
    }

    [Fact]
    public async Task AddMemberAsyncRejectsInactiveTargetUser()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        Guid orgId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active };
        userRepo.Users[ActorAdminId] = new User { Id = ActorAdminId, IsActive = true };
        memRepo.GlobalPermissions[ActorAdminId] = AdminPermissions;

        userRepo.Users[targetUserId] = new User { Id = targetUserId, IsActive = false }; // inactive!
        roleRepo.Roles[MemberRoleId] = new Role { Id = MemberRoleId, Name = "Member", Scope = RoleScope.Organization };

        Result<OrganizationMemberResponse> result = await service.AddMemberAsync(
            ActorAdminId,
            orgId,
            new AddOrganizationMemberRequest(targetUserId, MemberRoleId));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.UserInactive, result.Error);
    }

    [Fact]
    public async Task AddMemberAsyncDuplicateMembershipReturnsConflict()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        Guid orgId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active };
        userRepo.Users[ActorAdminId] = new User { Id = ActorAdminId, IsActive = true };
        memRepo.GlobalPermissions[ActorAdminId] = AdminPermissions;

        userRepo.Users[targetUserId] = new User { Id = targetUserId, IsActive = true };
        roleRepo.Roles[MemberRoleId] = new Role { Id = MemberRoleId, Name = "Member", Scope = RoleScope.Organization };

        memRepo.Memberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            UserId = targetUserId,
            RoleId = MemberRoleId,
            Status = MembershipStatus.Active
        });

        Result<OrganizationMemberResponse> result = await service.AddMemberAsync(
            ActorAdminId,
            orgId,
            new AddOrganizationMemberRequest(targetUserId, MemberRoleId));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.MembershipAlreadyExists, result.Error);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    [Fact]
    public async Task AddMemberAsyncPreventsPrivilegeEscalationForNonPlatformActor()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        Guid orgId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Guid customRoleId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active };

        userRepo.Users[ActorUserId] = new User { Id = ActorUserId, IsActive = true };
        memRepo.GlobalPermissions[ActorUserId] = []; // Not platform admin
        memRepo.Memberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            UserId = ActorUserId,
            RoleId = MemberRoleId,
            Status = MembershipStatus.Active
        });
        // Actor only has "organizations.members.manage"
        memRepo.EffectivePermissions[(ActorUserId, orgId)] = [Permissions.Organizations.Members.Manage];

        userRepo.Users[targetUserId] = new User { Id = targetUserId, IsActive = true };
        roleRepo.Roles[customRoleId] = new Role { Id = customRoleId, Name = "SuperRole", Scope = RoleScope.Organization };
        // Role has a permission actor does not have!
        roleRepo.RolePermissions[customRoleId] = [Permissions.Organizations.Members.Manage, Permissions.Organizations.Update];

        Result<OrganizationMemberResponse> result = await service.AddMemberAsync(
            ActorUserId,
            orgId,
            new AddOrganizationMemberRequest(targetUserId, customRoleId));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.PrivilegeEscalation, result.Error);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
    }

    [Fact]
    public async Task PatchMemberAsyncManagerCannotActivateInvitation()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        Guid orgId = Guid.NewGuid();
        Guid memberId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active };
        userRepo.Users[ActorAdminId] = new User { Id = ActorAdminId, IsActive = true };
        memRepo.GlobalPermissions[ActorAdminId] = AdminPermissions;

        memRepo.Memberships.Add(new OrganizationMembership
        {
            Id = memberId,
            OrganizationId = orgId,
            UserId = Guid.NewGuid(),
            RoleId = MemberRoleId,
            Status = MembershipStatus.Invited
        });

        Result result = await service.PatchMemberAsync(
            ActorAdminId,
            orgId,
            memberId,
            new PatchOrganizationMemberRequest(Status: MembershipStatus.Active));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.CannotActivateInvitation, result.Error);
    }

    [Fact]
    public async Task AcceptInvitationAsyncSelfServiceTransitionsToActive()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        Guid orgId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active };
        userRepo.Users[ActorUserId] = new User { Id = ActorUserId, IsActive = true };
        memRepo.GlobalPermissions[ActorUserId] = [];

        OrganizationMembership membership = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            UserId = ActorUserId,
            RoleId = MemberRoleId,
            Status = MembershipStatus.Invited
        };
        memRepo.Memberships.Add(membership);

        Result result = await service.AcceptInvitationAsync(ActorUserId, orgId);

        Assert.True(result.IsSuccess);
        Assert.Equal(MembershipStatus.Active, membership.Status);
    }

    [Fact]
    public async Task PatchMemberAsyncCannotSuspendLastOwner()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        Guid orgId = Guid.NewGuid();
        Guid ownerMemberId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active };
        userRepo.Users[ActorAdminId] = new User { Id = ActorAdminId, IsActive = true };
        memRepo.GlobalPermissions[ActorAdminId] = AdminPermissions;

        Role ownerRole = new() { Id = OwnerRoleId, Name = "Owner", Scope = RoleScope.Organization };
        roleRepo.Roles[OwnerRoleId] = ownerRole;

        memRepo.Memberships.Add(new OrganizationMembership
        {
            Id = ownerMemberId,
            OrganizationId = orgId,
            UserId = Guid.NewGuid(),
            RoleId = OwnerRoleId,
            Role = ownerRole,
            Status = MembershipStatus.Active
        });

        Result result = await service.PatchMemberAsync(
            ActorAdminId,
            orgId,
            ownerMemberId,
            new PatchOrganizationMemberRequest(Status: MembershipStatus.Suspended));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.LastOwner, result.Error);
    }

    [Fact]
    public async Task PatchMemberAsyncCannotDemoteLastOwner()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        Guid orgId = Guid.NewGuid();
        Guid ownerMemberId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active };
        userRepo.Users[ActorAdminId] = new User { Id = ActorAdminId, IsActive = true };
        memRepo.GlobalPermissions[ActorAdminId] = AdminPermissions;

        Role ownerRole = new() { Id = OwnerRoleId, Name = "Owner", Scope = RoleScope.Organization };
        Role memberRole = new() { Id = MemberRoleId, Name = "Member", Scope = RoleScope.Organization };
        roleRepo.Roles[OwnerRoleId] = ownerRole;
        roleRepo.Roles[MemberRoleId] = memberRole;

        memRepo.Memberships.Add(new OrganizationMembership
        {
            Id = ownerMemberId,
            OrganizationId = orgId,
            UserId = Guid.NewGuid(),
            RoleId = OwnerRoleId,
            Role = ownerRole,
            Status = MembershipStatus.Active
        });

        Result result = await service.PatchMemberAsync(
            ActorAdminId,
            orgId,
            ownerMemberId,
            new PatchOrganizationMemberRequest(RoleId: MemberRoleId));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.LastOwner, result.Error);
    }

    [Fact]
    public async Task DeleteMemberAsyncCannotRemoveLastOwner()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        Guid orgId = Guid.NewGuid();
        Guid ownerMemberId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active };
        userRepo.Users[ActorAdminId] = new User { Id = ActorAdminId, IsActive = true };
        memRepo.GlobalPermissions[ActorAdminId] = AdminPermissions;

        Role ownerRole = new() { Id = OwnerRoleId, Name = "Owner", Scope = RoleScope.Organization };
        roleRepo.Roles[OwnerRoleId] = ownerRole;

        memRepo.Memberships.Add(new OrganizationMembership
        {
            Id = ownerMemberId,
            OrganizationId = orgId,
            UserId = Guid.NewGuid(),
            RoleId = OwnerRoleId,
            Role = ownerRole,
            Status = MembershipStatus.Active
        });

        Result result = await service.DeleteMemberAsync(ActorAdminId, orgId, ownerMemberId);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.LastOwner, result.Error);
    }

    [Fact]
    public async Task UpdateAsyncNonPlatformAdminCannotChangeIsActive()
    {
        FakeOrganizationRepository orgRepo = new();
        FakeMembershipRepository memRepo = new();
        FakeUserRepository userRepo = new();
        FakeRoleRepository roleRepo = new();
        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo);

        Guid orgId = Guid.NewGuid();
        orgRepo.Organizations[orgId] = new Organization { Id = orgId, Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active };
        userRepo.Users[ActorUserId] = new User { Id = ActorUserId, IsActive = true };
        memRepo.GlobalPermissions[ActorUserId] = [Permissions.Organizations.Update]; // NOT platform admin

        Result result = await service.UpdateAsync(
            ActorUserId,
            orgId,
            new UpdateOrganizationRequest(IsActive: false));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Organization.PlatformAdminRequired, result.Error);
    }

    private static readonly string[] AdminPermissions =
[
    PermissionCode.IdentityOrganizationsSuspend,
        PermissionCode.IdentityOrganizationsCreate,
        PermissionCode.IdentityOrganizationsRead,
        PermissionCode.IdentityOrganizationsUpdate,
        PermissionCode.IdentityMembersRead,
        PermissionCode.IdentityMembersManage,
        PermissionCode.IdentityRolesAssignOrganization,
        PermissionCode.IdentityRolesRead
];

    private sealed class FakeOrganizationRepository : IOrganizationRepository
    {
        public Task<bool> TaxCodeExistsAsync(string taxCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);


        public Dictionary<Guid, Organization> Organizations { get; } = [];
        public HashSet<string> ExistingSlugs { get; } = [];
        public OrganizationMembership? LastCreatedInitialMembership { get; private set; }

        public Task<Organization?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Organizations.GetValueOrDefault(id));

        public Task<Organization?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult(Organizations.Values.FirstOrDefault(o => o.Slug == slug));

        public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult(ExistingSlugs.Contains(slug) || Organizations.Values.Any(o => o.Slug == slug));

        public Task<PagedResult<Organization>> GetAllAsync(PageRequest request, CancellationToken cancellationToken = default)
        {
            List<Organization> list = Organizations.Values.OrderBy(o => o.CreatedAtUtc).ThenBy(o => o.Id).ToList();
            List<Organization> paged = list.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToList();
            return Task.FromResult(PagedResult.Create(paged, request.Page, request.PageSize, list.Count));
        }

        public Task<PagedResult<Organization>> GetUserOrganizationsAsync(Guid userId, PageRequest request, CancellationToken cancellationToken = default)
        {
            List<Organization> list = Organizations.Values.OrderBy(o => o.CreatedAtUtc).ThenBy(o => o.Id).ToList();
            List<Organization> paged = list.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToList();
            return Task.FromResult(PagedResult.Create(paged, request.Page, request.PageSize, list.Count));
        }

        public Task<Organization> CreateAsync(Organization organization, OrganizationMembership initialMembership, CancellationToken cancellationToken = default)
        {
            if (ExistingSlugs.Contains(organization.Slug))
            {
                throw new DuplicateSlugException();
            }

            Organizations[organization.Id] = organization;
            LastCreatedInitialMembership = initialMembership;
            return Task.FromResult(organization);
        }

        public Task<Organization> CreateAsync(
            Organization organization,
            OrganizationMembership initialMembership,
            NavigationCatalog navigationCatalog,
            CancellationToken cancellationToken = default) =>
            CreateAsync(organization, initialMembership, cancellationToken);

        public Task<bool> UpdateAsync(Organization organization, CancellationToken cancellationToken = default)
        {
            Organizations[organization.Id] = organization;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeMembershipRepository : IOrganizationMembershipRepository
    {
        public List<OrganizationMembership> Memberships { get; } = [];
        public Dictionary<Guid, IReadOnlyList<string>> GlobalPermissions { get; } = [];
        public Dictionary<(Guid, Guid), IReadOnlyList<string>> EffectivePermissions { get; } = [];

        public Task<OrganizationMembership?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.FirstOrDefault(m => m.Id == id));

        public Task<OrganizationMembership?> GetByOrganizationAndUserAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.FirstOrDefault(m => m.OrganizationId == organizationId && m.UserId == userId));

        public Task<OrganizationMembership?> FindMemberAsync(Guid organizationId, Guid memberOrUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.FirstOrDefault(m => m.OrganizationId == organizationId && (m.Id == memberOrUserId || m.UserId == memberOrUserId)));

        public Task<PagedResult<OrganizationMembership>> GetMembersAsync(Guid organizationId, PageRequest request, CancellationToken cancellationToken = default)
        {
            List<OrganizationMembership> list = Memberships.Where(m => m.OrganizationId == organizationId).OrderBy(m => m.JoinedAtUtc).ThenBy(m => m.Id).ToList();
            List<OrganizationMembership> paged = list.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToList();
            return Task.FromResult(PagedResult.Create(paged, request.Page, request.PageSize, list.Count));
        }

        public Task<bool> MembershipExistsAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.Any(m => m.OrganizationId == organizationId && m.UserId == userId));

        public Task<OrganizationMembership> AddMemberAsync(OrganizationMembership membership, CancellationToken cancellationToken = default)
        {
            if (Memberships.Any(m => m.OrganizationId == membership.OrganizationId && m.UserId == membership.UserId))
            {
                throw new DuplicateMembershipException();
            }

            Memberships.Add(membership);
            return Task.FromResult(membership);
        }

        public Task<bool> UpdateAsync(OrganizationMembership membership, CancellationToken cancellationToken = default)
        {
            int index = Memberships.FindIndex(m => m.Id == membership.Id);
            if (index >= 0)
            {
                Memberships[index] = membership;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<bool> DeleteAsync(Guid membershipId, CancellationToken cancellationToken = default)
        {
            int removed = Memberships.RemoveAll(m => m.Id == membershipId);
            return Task.FromResult(removed > 0);
        }

        public Task<int> CountOwnersAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.Count(m => m.OrganizationId == organizationId && string.Equals(m.Role?.Name, "Owner", StringComparison.OrdinalIgnoreCase)));

        public Task<int> CountActiveOwnersAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.Count(m => m.OrganizationId == organizationId && string.Equals(m.Role?.Name, "Owner", StringComparison.OrdinalIgnoreCase) && m.Status == MembershipStatus.Active));

        public Task<IReadOnlyList<string>> GetEffectivePermissionsAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EffectivePermissions.GetValueOrDefault((userId, organizationId)) ?? (IReadOnlyList<string>)[]);

        public Task<IReadOnlyList<string>> GetGlobalPermissionsAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GlobalPermissions.GetValueOrDefault(userId) ?? (IReadOnlyList<string>)[]);
    }

    private sealed class FakeUserRepository : IUserRepository
    {
        public Task<bool> CitizenIdExistsAsync(string citizenId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Dictionary<Guid, User> Users { get; } = [];

        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Users.GetValueOrDefault(id));

        public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<User>>(Users.Values.ToList());

        public Task<User> CreateAsync(User entity, CancellationToken cancellationToken = default) => Task.FromResult(entity);
        public Task<User> CreateWithRoleAsync(User user, Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(user);
        public Task<bool> UpdateAsync(User entity, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) => Task.FromResult<User?>(null);
        public Task<User?> GetByIdWithAccessAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Users.GetValueOrDefault(id));
    }

    private sealed class FakeRoleRepository : IRoleRepository
    {
        public Dictionary<Guid, Role> Roles { get; } = [];
        public Dictionary<Guid, IReadOnlyList<string>> RolePermissions { get; } = [];

        public Task<bool> RoleExistsAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Roles.ContainsKey(roleId));

        public Task<Role?> GetByIdAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Roles.GetValueOrDefault(roleId));

        public Task<Role?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(Roles.Values.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<string>> GetPermissionCodesAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(RolePermissions.GetValueOrDefault(roleId) ?? (IReadOnlyList<string>)[]);

        public Task<IReadOnlyList<string>> GetUserPermissionCodesAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> IsAssignedAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> AssignAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RevokeAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeNavigationCatalogRepository : INavigationCatalogRepository
    {
        public Task<NavigationCatalog?> GetTemplateAsync(OrganizationType dashboardType, CancellationToken cancellationToken = default) =>
            Task.FromResult<NavigationCatalog?>(new NavigationCatalog { Id = Guid.NewGuid(), DashboardType = dashboardType });

        public Task<NavigationCatalog?> GetOrganizationCatalogAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<NavigationCatalog?>(null);

        public Task<IReadOnlyList<Permission>> GetAssignablePermissionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Permission>>([]);

        public Task<Permission?> GetAssignablePermissionAsync(Guid permissionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Permission?>(null);

        public Task<NavigationItem?> GetItemAsync(Guid catalogId, Guid itemId, CancellationToken cancellationToken = default) =>
            Task.FromResult<NavigationItem?>(null);

        public Task<bool> KeyExistsAsync(Guid catalogId, string key, Guid? excludingItemId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task AddItemAsync(NavigationItem item, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateItemAsync(NavigationItem item, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteItemAsync(NavigationItem item, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddPermissionAsync(NavigationItemPermission permission, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> RemovePermissionAsync(Guid itemId, Guid permissionId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
