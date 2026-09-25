using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;

using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Domain.Entities;


using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class PagingTests
{
    [Fact]
    public void PageRequestHasCorrectDefaults()
    {
        PageRequest request = new();
        Assert.Equal(1, request.Page);
        Assert.Equal(20, request.PageSize);
        Assert.True(request.Validate().IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void PageRequestRejectsInvalidPage(int invalidPage)
    {
        PageRequest request = new(Page: invalidPage, PageSize: 20);
        Result validation = request.Validate();
        Assert.True(validation.IsFailure);
        Assert.Equal(AuthErrors.Paging.InvalidPage, validation.Error);
        Assert.Equal(ErrorType.Validation, validation.Error.Type);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    [InlineData(1000)]
    public void PageRequestRejectsInvalidPageSize(int invalidPageSize)
    {
        PageRequest request = new(Page: 1, PageSize: invalidPageSize);
        Result validation = request.Validate();
        Assert.True(validation.IsFailure);
        Assert.Equal(AuthErrors.Paging.InvalidPageSize, validation.Error);
        Assert.Equal(ErrorType.Validation, validation.Error.Type);
    }

    [Fact]
    public void PagedResultCalculatesTotalPagesCorrectly()
    {
        // Empty set
        PagedResult<string> empty = PagedResult.Create<string>([], page: 1, pageSize: 20, totalCount: 0);
        Assert.Equal(0, empty.TotalPages);
        Assert.Equal(0, empty.TotalCount);
        Assert.Empty(empty.Items);

        // Exact 1 page
        PagedResult<int> onePage = PagedResult.Create([1, 2], page: 1, pageSize: 20, totalCount: 20);
        Assert.Equal(1, onePage.TotalPages);

        // 2 pages
        PagedResult<int> twoPages = PagedResult.Create([1], page: 2, pageSize: 20, totalCount: 25);
        Assert.Equal(2, twoPages.TotalPages);
        Assert.Equal(2, twoPages.Page);
        Assert.Equal(20, twoPages.PageSize);
        Assert.Equal(25, twoPages.TotalCount);
    }

    [Fact]
    public async Task EfOrganizationRepositoryGetAllAsyncUsesStableOrderingAndPagination()
    {
        string dbName = Guid.NewGuid().ToString("N");
        DbContextOptions<AuthDbContext> dbOptions = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        await using AuthDbContext db = new(dbOptions);

        DateTime fixedTime = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        Guid id1 = new("00000000-0000-0000-0000-000000000002");
        Guid id2 = new("00000000-0000-0000-0000-000000000001");
        Guid id3 = new("00000000-0000-0000-0000-000000000003");

        // Add with same CreatedAtUtc to verify Id tie-breaker
        db.Organizations.AddRange(
            new Organization { Id = id1, Name = "B", Slug = "b", Type = OrganizationType.Supplier, CreatedAtUtc = fixedTime, Status = OrganizationStatus.Active },
            new Organization { Id = id2, Name = "A", Slug = "a", Type = OrganizationType.Supplier, CreatedAtUtc = fixedTime, Status = OrganizationStatus.Active },
            new Organization { Id = id3, Name = "C", Slug = "c", Type = OrganizationType.Supplier, CreatedAtUtc = fixedTime, Status = OrganizationStatus.Active }
        );
        await db.SaveChangesAsync();

        EfOrganizationRepository repo = new(db);

        // Page 1, size 2
        PagedResult<Organization> page1 = await repo.GetAllAsync(new PageRequest(Page: 1, PageSize: 2));
        Assert.Equal(3, page1.TotalCount);
        Assert.Equal(2, page1.TotalPages);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(id2, page1.Items[0].Id); // id2 < id1
        Assert.Equal(id1, page1.Items[1].Id);

        // Page 2, size 2 (next page)
        PagedResult<Organization> page2 = await repo.GetAllAsync(new PageRequest(Page: 2, PageSize: 2));
        Assert.Equal(3, page2.TotalCount);
        Assert.Equal(2, page2.TotalPages);
        Assert.Single(page2.Items);
        Assert.Equal(id3, page2.Items[0].Id);
    }

    [Fact]
    public async Task OrganizationServiceListAsyncReturnsValidatedFailureOnInvalidPaging()
    {
        Guid actorId = Guid.NewGuid();
        FakeOrgRepository orgRepo = new();
        FakeMemRepository memRepo = new();
        FakeUserRepo userRepo = new();
        FakeRoleRepo roleRepo = new();

        OrganizationService service = new(orgRepo, memRepo, userRepo, roleRepo, FixedCorrelationIdProvider.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrganizationService>.Instance);

        Result<PagedResult<OrganizationResponse>> result = await service.ListAsync(actorId, new PageRequest(Page: 0, PageSize: 20));
        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.Paging.InvalidPage, result.Error);
        Assert.Equal(ErrorType.Validation, result.Error.Type);

        Result<PagedResult<OrganizationResponse>> result2 = await service.ListAsync(actorId, new PageRequest(Page: 1, PageSize: 101));
        Assert.True(result2.IsFailure);
        Assert.Equal(AuthErrors.Paging.InvalidPageSize, result2.Error);
        Assert.Equal(ErrorType.Validation, result2.Error.Type);
    }

    private sealed class FakeOrgRepository : IOrganizationRepository
    {
        public Task<bool> TaxCodeExistsAsync(string taxCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);


        public Task<Organization?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Organization?>(null);
        public Task<Organization?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default) => Task.FromResult<Organization?>(null);
        public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<PagedResult<Organization>> GetAllAsync(PageRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(PagedResult.Create<Organization>([], request.Page, request.PageSize, 0));
        public Task<PagedResult<Organization>> GetUserOrganizationsAsync(Guid userId, PageRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(PagedResult.Create<Organization>([], request.Page, request.PageSize, 0));
        public Task<Organization> CreateAsync(Organization organization, OrganizationMembership initialMembership, CancellationToken cancellationToken = default) =>
            Task.FromResult(organization);

        public Task<bool> UpdateAsync(Organization organization, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task LockOrganizationsAsync(IReadOnlyList<Guid> sortedOrganizationIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => Task.FromResult(IExecutionTransaction.None);
    }

    private sealed class FakeMemRepository : IOrganizationMembershipRepository
    {
        public Task<OrganizationMembership?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<OrganizationMembership?>(null);
        public Task<OrganizationMembership?> GetByOrganizationAndUserAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<OrganizationMembership?>(null);
        public Task<OrganizationMembership?> FindMemberAsync(Guid organizationId, Guid memberOrUserId, CancellationToken cancellationToken = default) => Task.FromResult<OrganizationMembership?>(null);
        public Task<PagedResult<OrganizationMembership>> GetMembersAsync(Guid organizationId, PageRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(PagedResult.Create<OrganizationMembership>([], request.Page, request.PageSize, 0));
        public Task<bool> MembershipExistsAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<OrganizationMembership> AddMemberAsync(OrganizationMembership membership, CancellationToken cancellationToken = default) => Task.FromResult(membership);
        public Task<bool> UpdateAsync(OrganizationMembership membership, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> DeleteAsync(Guid membershipId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<int> CountOwnersAsync(Guid organizationId, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> CountActiveOwnersAsync(Guid organizationId, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<IReadOnlyList<OrganizationMembership>> GetMembershipsForUserAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OrganizationMembership>>([]);
        public Task<IReadOnlyList<string>> GetGlobalPermissionsAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> GetScopedPermissionCodesAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeUserRepo : IUserRepository
    {
        public Task<bool> CitizenIdExistsAsync(string citizenId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<User?>(null);
        public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) => Task.FromResult<User?>(null);
        public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<User> CreateAsync(User user, CancellationToken cancellationToken = default) => Task.FromResult(user);
        public Task<User> CreateWithRoleAsync(User user, Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(user);
        public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<User>>([]);
        public Task<bool> UpdateAsync(User user, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<User?> GetByIdWithAccessAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<User?>(null);
        public Task<(IReadOnlyCollection<User> Items, int TotalCount)> GetUsersPagedAsync(int skip, int take, CancellationToken cancellationToken = default)
        => Task.FromResult<(IReadOnlyCollection<User> Items, int TotalCount)>((Array.Empty<User>(), 0));
        public Task<bool> ChangePasswordAndRevokeSessionsAsync(Guid userId, string newPasswordHash, DateTime utcNow, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ResetPasswordWithTokenAsync(Guid userId, string tokenHash, string newPasswordHash, DateTime utcNow, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsUserActiveAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(true);

    }
    private sealed class FakeRoleRepo : IRoleRepository
    {
        public Task<bool> RoleExistsAsync(Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<Role?> GetByIdAsync(Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult<Role?>(null);
        public Task<Role?> GetByNameAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult<Role?>(null);
        public Task<IReadOnlyList<string>> GetPermissionCodesAsync(Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> GetUserPermissionCodesAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> IsAssignedAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> AssignAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RevokeAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
