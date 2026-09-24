using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Data;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

/// <summary>Đăng ký tổ chức chỉ dành cho user đã đăng nhập — người chưa có tài khoản phải đăng ký customer trước.</summary>
public sealed class OrganizationRegistrationServiceTests
{
    private static readonly Guid OwnerRoleId = new("a1010000-0000-0000-0000-000000000001");

    [Fact]
    public async Task RegisterForExistingUserCreatesPendingOrganizationAndOwnerMembership()
    {
        await using AuthDbContext dbContext = await CreateDbContextAsync();
        User owner = await CreateUserAsync(dbContext, "owner@example.com");
        OrganizationRegistrationService service = CreateService(dbContext);

        Result<OrganizationRegisteredResponse> result = await service.RegisterForExistingUserAsync(
            owner.Id, CreateProfile(name: "Công ty TNHH Đông Á"));

        Assert.True(result.IsSuccess);
        Assert.Equal(owner.Id, result.Value.AccountId);
        Assert.Equal(OrganizationStatus.Pending, result.Value.Status);
        Assert.Equal("cong-ty-tnhh-dong-a", result.Value.Slug);

        Organization org = await dbContext.Organizations.SingleAsync();
        Assert.Equal(OrganizationType.Supplier, org.Type);
        Assert.Equal("0312345678", org.TaxCode);
        Assert.Equal("contact@donga.vn", org.ContactEmail);

        OrganizationMembership membership = await dbContext.OrganizationMemberships.SingleAsync();
        Assert.Equal(owner.Id, membership.UserId);
        Assert.Equal(OwnerRoleId, membership.RoleId);
        Assert.Equal(MembershipStatus.Active, membership.Status);
    }

    [Fact]
    public async Task RegisterRejectsUnknownActor()
    {
        await using AuthDbContext dbContext = await CreateDbContextAsync();
        OrganizationRegistrationService service = CreateService(dbContext);

        Result<OrganizationRegisteredResponse> result = await service.RegisterForExistingUserAsync(Guid.NewGuid(), CreateProfile());

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrorCode.User.NotFound, result.Error.Code);
    }

    [Fact]
    public async Task RegisterRejectsPlatformType()
    {
        await using AuthDbContext dbContext = await CreateDbContextAsync();
        User owner = await CreateUserAsync(dbContext, "owner@example.com");
        OrganizationRegistrationService service = CreateService(dbContext);

        Result<OrganizationRegisteredResponse> result = await service.RegisterForExistingUserAsync(
            owner.Id, CreateProfile() with { Type = OrganizationType.Platform });

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrorCode.Organization.SelfRegistrationTypeNotAllowed, result.Error.Code);
        Assert.Empty(dbContext.Organizations);
    }

    [Fact]
    public async Task RegisterRejectsDuplicateTaxCode()
    {
        await using AuthDbContext dbContext = await CreateDbContextAsync();
        User owner = await CreateUserAsync(dbContext, "owner@example.com");
        dbContext.Organizations.Add(new Organization { Id = Guid.NewGuid(), Name = "Existing", Slug = "existing", TaxCode = "0312345678", Status = OrganizationStatus.Active });
        await dbContext.SaveChangesAsync();
        OrganizationRegistrationService service = CreateService(dbContext);

        Result<OrganizationRegisteredResponse> result = await service.RegisterForExistingUserAsync(owner.Id, CreateProfile());

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrorCode.Organization.TaxCodeAlreadyExists, result.Error.Code);
    }

    [Fact]
    public async Task RegisterAddsSuffixWhenGeneratedSlugIsTaken()
    {
        await using AuthDbContext dbContext = await CreateDbContextAsync();
        User owner = await CreateUserAsync(dbContext, "owner@example.com");
        dbContext.Organizations.Add(new Organization { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active });
        await dbContext.SaveChangesAsync();
        OrganizationRegistrationService service = CreateService(dbContext);

        Result<OrganizationRegisteredResponse> result = await service.RegisterForExistingUserAsync(owner.Id, CreateProfile(name: "Acme"));

        Assert.True(result.IsSuccess);
        Assert.StartsWith("acme-", result.Value.Slug, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterRejectsTakenExplicitSlug()
    {
        await using AuthDbContext dbContext = await CreateDbContextAsync();
        User owner = await CreateUserAsync(dbContext, "owner@example.com");
        dbContext.Organizations.Add(new Organization { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme", Status = OrganizationStatus.Active });
        await dbContext.SaveChangesAsync();
        OrganizationRegistrationService service = CreateService(dbContext);

        Result<OrganizationRegisteredResponse> result = await service.RegisterForExistingUserAsync(owner.Id, CreateProfile(slug: "acme"));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrorCode.Organization.SlugAlreadyExists, result.Error.Code);
    }

    [Fact]
    public async Task PendingOrganizationDoesNotContributeToUserAccess()
    {
        await using AuthDbContext dbContext = await CreateDbContextAsync();
        User owner = await CreateUserAsync(dbContext, "owner@example.com");
        OrganizationRegistrationService service = CreateService(dbContext);
        await service.RegisterForExistingUserAsync(owner.Id, CreateProfile());

        User? reloaded = await new EfUserRepository(dbContext).GetByIdWithAccessAsync(owner.Id);

        Assert.NotNull(reloaded);
        Assert.Empty(Dprental.AuthService.Domain.Access.UserAccessResolver.ActiveMemberships(reloaded));
    }

    private static OrganizationProfileRequest CreateProfile(string name = "Dong A Rental", string? slug = null) =>
        new(
            Name: name,
            Type: OrganizationType.Supplier,
            TaxCode: "0312345678",
            LegalRepresentativeName: "Nguyen Van A",
            ContactEmail: "Contact@DongA.vn",
            ContactPhone: "0281234567",
            Address: "12 Nguyen Hue",
            Province: "TP. Ho Chi Minh",
            Ward: "Phuong Sai Gon",
            BusinessLicenseDocumentUrl: "https://files.example.com/license.pdf",
            Slug: slug);

    private static async Task<User> CreateUserAsync(AuthDbContext dbContext, string email)
    {
        User user = new() { Id = Guid.NewGuid(), Email = email, FullName = "Owner", PasswordHash = "hash", IsActive = true, ProfileCompletedAtUtc = DateTime.UtcNow };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user;
    }

    private static async Task<AuthDbContext> CreateDbContextAsync()
    {
        DbContextOptions<AuthDbContext> options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        AuthDbContext dbContext = new(options);
        dbContext.Roles.Add(new Role { Id = OwnerRoleId, Name = SystemRoleCode.Owner, Scope = RoleScope.Organization });
        await dbContext.SaveChangesAsync();
        return dbContext;
    }

    private static OrganizationRegistrationService CreateService(AuthDbContext dbContext) =>
        new(
            new EfOrganizationRepository(dbContext),
            new EfUserRepository(dbContext),
            new EfRoleRepository(dbContext),
            TimeProvider.System);
}
