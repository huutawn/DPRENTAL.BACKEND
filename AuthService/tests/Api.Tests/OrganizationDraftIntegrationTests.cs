using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.Media;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class OrganizationDraftIntegrationTests
{
    private static readonly Guid OwnerRoleId = new("a1010000-0000-0000-0000-000000000001");
    private static readonly Guid AdminRoleId = new("a2020000-0000-0000-0000-000000000002");
    private static readonly Guid MemberRoleId = new("a3030000-0000-0000-0000-000000000003");
    private static readonly Guid CustomerRoleId = new("82127c20-a094-4d8b-a1c7-b94266c001d2");

    [Fact]
    public async Task SaveDraftCreatesNewOrganizationInDraftStatusWhenNoOrgExists()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        Guid userId = Guid.NewGuid();
        await CreateUserAsync(factory, userId, "draft-owner@example.com", "Draft Owner", CustomerRoleId);
        using HttpClient client = CreateAuthenticatedClient(factory, userId, "customer");

        OrganizationDraftRequest request = new(
            Name: "Công ty Cơ Giới Nháp",
            Type: OrganizationType.Supplier,
            TaxCode: null,
            LegalRepresentativeName: null,
            ContactEmail: null,
            ContactPhone: null);

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/organizations/draft", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        OrganizationResponse? draft = await response.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(draft);
        Assert.Equal("Công ty Cơ Giới Nháp", draft.Name);
        Assert.Equal(OrganizationStatus.Draft, draft.Status);
        Assert.NotNull(draft.MissingRequirements);
        Assert.Contains("Mã số thuế", draft.MissingRequirements);
        Assert.Contains("Giấy phép kinh doanh", draft.MissingRequirements);
    }

    [Fact]
    public async Task SaveDraftUpdatesExistingDraftWhenDraftAlreadyExists()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        Guid userId = Guid.NewGuid();
        await CreateUserAsync(factory, userId, "draft-update@example.com", "Draft Update", CustomerRoleId);
        using HttpClient client = CreateAuthenticatedClient(factory, userId, "customer");

        OrganizationDraftRequest initialRequest = new(
            Name: "Tên ban đầu",
            Type: OrganizationType.Supplier);

        using HttpResponseMessage initialResponse = await client.PostAsJsonAsync("/api/v1/auth/organizations/draft", initialRequest);
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);

        OrganizationDraftRequest updateRequest = new(
            Name: "Tên đã cập nhật",
            Type: OrganizationType.Supplier,
            TaxCode: "0123456789",
            ContactPhone: "0912345678");

        using HttpResponseMessage updateResponse = await client.PostAsJsonAsync("/api/v1/auth/organizations/draft", updateRequest);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        OrganizationResponse? updated = await updateResponse.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(updated);
        Assert.Equal("Tên đã cập nhật", updated.Name);
        Assert.Equal("0123456789", updated.TaxCode);
        Assert.Equal("0912345678", updated.ContactPhone);
        Assert.Equal(OrganizationStatus.Draft, updated.Status);
    }

    [Fact]
    public async Task SaveDraftRejectsUnauthorizedMediaAttachment()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        Guid userId = Guid.NewGuid();
        Guid foreignUserId = Guid.NewGuid();
        var fakeMedia = (FakeMediaServiceClient)factory.Services.GetRequiredService<IMediaServiceClient>();
        fakeMedia.AutoVerify = false;

        Guid foreignMediaId = Guid.NewGuid();
        fakeMedia.Verifications[foreignMediaId] = new MediaVerificationDto(
            foreignMediaId, "Ready", foreignUserId.ToString("D"), "license.pdf", "application/pdf", 1024, DateTimeOffset.UtcNow);

        await CreateUserAsync(factory, userId, "draft-attacker@example.com", "Attacker", CustomerRoleId);
        using HttpClient client = CreateAuthenticatedClient(factory, userId, "customer");

        OrganizationDraftRequest request = new(
            Name: "Công ty Cơ Giới Tặc",
            Type: OrganizationType.Supplier,
            BusinessLicenseDocumentMediaId: foreignMediaId);

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/organizations/draft", request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SaveDraftRejectsUnreadyMediaAttachment()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        Guid userId = Guid.NewGuid();
        var fakeMedia = (FakeMediaServiceClient)factory.Services.GetRequiredService<IMediaServiceClient>();
        fakeMedia.AutoVerify = false;

        Guid unreadyMediaId = Guid.NewGuid();
        fakeMedia.Verifications[unreadyMediaId] = new MediaVerificationDto(
            unreadyMediaId, "Processing", userId.ToString("D"), "license.pdf", "application/pdf", 1024, DateTimeOffset.UtcNow);

        await CreateUserAsync(factory, userId, "draft-unready@example.com", "Unready", CustomerRoleId);
        using HttpClient client = CreateAuthenticatedClient(factory, userId, "customer");

        OrganizationDraftRequest request = new(
            Name: "Công ty Cơ Giới Unready",
            Type: OrganizationType.Supplier,
            BusinessLicenseDocumentMediaId: unreadyMediaId);

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/organizations/draft", request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DraftAccessDeniesPrivateUrlForUnauthorizedMedia()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        Guid ownerId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        Guid foreignMediaId = Guid.NewGuid();

        var fakeMedia = (FakeMediaServiceClient)factory.Services.GetRequiredService<IMediaServiceClient>();
        fakeMedia.AutoVerify = false;
        fakeMedia.Verifications[foreignMediaId] = new MediaVerificationDto(
            foreignMediaId, "Ready", otherUserId.ToString("D"), "license.pdf", "application/pdf", 1024, DateTimeOffset.UtcNow);
        fakeMedia.AccessUrls[foreignMediaId] = new MediaReadUrlDto(
            foreignMediaId, "https://media.example.test/private/leak.pdf", DateTimeOffset.UtcNow.AddMinutes(15));

        await CreateUserAsync(factory, ownerId, "victim-owner@example.com", "Victim Owner", CustomerRoleId);
        Guid orgId = Guid.NewGuid();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization org = new()
            {
                Id = orgId,
                Name = "Công ty Có Media Sai",
                Slug = "cong-ty-media-sai",
                Type = OrganizationType.Supplier,
                Status = OrganizationStatus.Draft,
                BusinessLicenseDocumentMediaId = foreignMediaId,
                CreatedAtUtc = DateTime.UtcNow
            };
            Role? ownerRole = await db.Roles.FindAsync(OwnerRoleId);
            OrganizationMembership membership = new()
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                UserId = ownerId,
                RoleId = ownerRole!.Id,
                Status = MembershipStatus.Active,
                JoinedAtUtc = DateTime.UtcNow
            };
            db.Organizations.Add(org);
            db.OrganizationMemberships.Add(membership);
            await db.SaveChangesAsync();
        }

        using HttpClient client = CreateAuthenticatedClient(factory, ownerId, "customer");
        using HttpResponseMessage response = await client.GetAsync($"/api/v1/auth/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        OrganizationResponse? body = await response.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(body);
        Assert.Null(body.BusinessLicenseDocumentUrl);
    }

    [Fact]
    public async Task AdminCanAccessValidSubmittedLicenseUrl()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        Guid adminId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        Guid validMediaId = Guid.NewGuid();

        var fakeMedia = (FakeMediaServiceClient)factory.Services.GetRequiredService<IMediaServiceClient>();
        fakeMedia.AutoVerify = false;
        fakeMedia.Verifications[validMediaId] = new MediaVerificationDto(
            validMediaId, "Ready", ownerId.ToString("D"), "license.pdf", "application/pdf", 1024, DateTimeOffset.UtcNow);
        fakeMedia.AccessUrls[validMediaId] = new MediaReadUrlDto(
            validMediaId, "https://media.example.test/private/admin-license.pdf", DateTimeOffset.UtcNow.AddMinutes(15));

        await CreateUserAsync(factory, adminId, "admin@example.com", "Admin User", AdminRoleId);
        await CreateUserAsync(factory, ownerId, "owner@example.com", "Org Owner", CustomerRoleId);

        Guid orgId = Guid.NewGuid();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization org = new()
            {
                Id = orgId,
                Name = "Công ty Đã Nộp Hợp Lệ",
                Slug = "cong-ty-da-nop-hop-le",
                Type = OrganizationType.Supplier,
                Status = OrganizationStatus.Pending,
                BusinessLicenseDocumentMediaId = validMediaId,
                CreatedAtUtc = DateTime.UtcNow
            };
            Role? ownerRole = await db.Roles.FindAsync(OwnerRoleId);
            OrganizationMembership membership = new()
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                UserId = ownerId,
                RoleId = ownerRole!.Id,
                Status = MembershipStatus.Active,
                JoinedAtUtc = DateTime.UtcNow
            };
            db.Organizations.Add(org);
            db.OrganizationMemberships.Add(membership);
            await db.SaveChangesAsync();
        }

        using HttpClient adminClient = CreateAuthenticatedClient(factory, adminId, "admin");
        using HttpResponseMessage response = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        OrganizationResponse? body = await response.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(body);
        Assert.Equal("https://media.example.test/private/admin-license.pdf", body.BusinessLicenseDocumentUrl);
    }

    [Fact]
    public async Task SaveDraftRejectsPendingOrActiveOrganization()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        Guid ownerId = Guid.NewGuid();
        await CreateUserAsync(factory, ownerId, "locked-owner@example.com", "Locked Owner", CustomerRoleId);

        Guid orgId = Guid.NewGuid();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization org = new()
            {
                Id = orgId,
                Name = "Công ty Đang Chờ Duyệt",
                Slug = "cong-ty-dang-cho-duyet",
                Type = OrganizationType.Supplier,
                Status = OrganizationStatus.Pending,
                CreatedAtUtc = DateTime.UtcNow
            };
            Role? ownerRole = await db.Roles.FindAsync(OwnerRoleId);
            OrganizationMembership membership = new()
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                UserId = ownerId,
                RoleId = ownerRole!.Id,
                Status = MembershipStatus.Active,
                JoinedAtUtc = DateTime.UtcNow
            };
            db.Organizations.Add(org);
            db.OrganizationMemberships.Add(membership);
            await db.SaveChangesAsync();
        }

        using HttpClient client = CreateAuthenticatedClient(factory, ownerId, "customer");
        OrganizationDraftRequest updateRequest = new(
            Name: "Tên muốn sửa",
            Type: OrganizationType.Supplier);

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/organizations/draft", updateRequest);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task NonOwnerCannotSaveOrSubmitDraft()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        Guid memberId = Guid.NewGuid();
        await CreateUserAsync(factory, memberId, "member@example.com", "Regular Member", CustomerRoleId);

        Guid orgId = Guid.NewGuid();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization org = new()
            {
                Id = orgId,
                Name = "Công ty Của Người Khác",
                Slug = "cong-ty-nguoi-khac",
                Type = OrganizationType.Supplier,
                Status = OrganizationStatus.Draft,
                CreatedAtUtc = DateTime.UtcNow
            };
            Role? memberRole = await db.Roles.FindAsync(MemberRoleId);
            OrganizationMembership membership = new()
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                UserId = memberId,
                RoleId = memberRole!.Id,
                Status = MembershipStatus.Active,
                JoinedAtUtc = DateTime.UtcNow
            };
            db.Organizations.Add(org);
            db.OrganizationMemberships.Add(membership);
            await db.SaveChangesAsync();
        }

        using HttpClient client = CreateAuthenticatedClient(factory, memberId, "customer");
        OrganizationDraftRequest draftRequest = new(
            Name: "Sửa trái phép",
            Type: OrganizationType.Supplier);

        using HttpResponseMessage saveResponse = await client.PostAsJsonAsync("/api/v1/auth/organizations/draft", draftRequest);
        Assert.Equal(HttpStatusCode.Forbidden, saveResponse.StatusCode);

        using HttpResponseMessage submitResponse = await client.PostAsync($"/api/v1/auth/organizations/{orgId}/submit", null);
        Assert.Equal(HttpStatusCode.Forbidden, submitResponse.StatusCode);
    }

    [Fact]
    public async Task SubmitDraftFailsWithMissingRequirementsWhenIncomplete()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        Guid userId = Guid.NewGuid();
        await CreateUserAsync(factory, userId, "draft-incomplete@example.com", "Incomplete", CustomerRoleId);
        using HttpClient client = CreateAuthenticatedClient(factory, userId, "customer");

        OrganizationDraftRequest request = new(
            Name: "Tổ chức thiếu hồ sơ",
            Type: OrganizationType.Supplier,
            TaxCode: "0123456789");

        using HttpResponseMessage draftResponse = await client.PostAsJsonAsync("/api/v1/auth/organizations/draft", request);
        OrganizationResponse? draft = await draftResponse.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(draft);

        using HttpResponseMessage submitResponse = await client.PostAsync($"/api/v1/auth/organizations/{draft.Id}/submit", null);
        Assert.Equal(HttpStatusCode.BadRequest, submitResponse.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await submitResponse.Content.ReadAsStringAsync());
        string detail = document.RootElement.GetProperty("detail").GetString() ?? "";
        Assert.Contains("Hồ sơ chưa đủ điều kiện nộp duyệt", detail);
    }

    [Fact]
    public async Task SubmitDraftSucceedsWhenAllRequirementsMet()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        Guid userId = Guid.NewGuid();
        var fakeMedia = (FakeMediaServiceClient)factory.Services.GetRequiredService<IMediaServiceClient>();
        fakeMedia.DefaultUserId = userId;
        fakeMedia.DefaultContentType = "application/pdf";
        await CreateUserAsync(factory, userId, "draft-complete@example.com", "Complete", CustomerRoleId, citizenId: "012345678901");
        using HttpClient client = CreateAuthenticatedClient(factory, userId, "customer");

        Guid validMediaId = Guid.NewGuid();
        OrganizationDraftRequest completeDraft = new(
            Name: "Công ty Cổ Phần Máy Xây Dựng Số 1",
            Type: OrganizationType.Supplier,
            TaxCode: "0123456789",
            LegalRepresentativeName: "Nguyễn Văn Đại Diện",
            ContactEmail: "contact@mayxaydung.vn",
            ContactPhone: "0901234567",
            Address: "123 Đường Công Trường",
            Province: "Thành phố Hồ Chí Minh",
            Ward: "Phường Bến Nghé",
            BusinessLicenseDocumentMediaId: validMediaId);

        using HttpResponseMessage draftResponse = await client.PostAsJsonAsync("/api/v1/auth/organizations/draft", completeDraft);
        Assert.Equal(HttpStatusCode.OK, draftResponse.StatusCode);
        OrganizationResponse? draft = await draftResponse.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(draft);

        using HttpResponseMessage submitResponse = await client.PostAsync($"/api/v1/auth/organizations/{draft.Id}/submit", null);
        Assert.Equal(HttpStatusCode.OK, submitResponse.StatusCode);

        OrganizationResponse? submitted = await submitResponse.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(submitted);
        Assert.Equal(OrganizationStatus.Pending, submitted.Status);
        Assert.NotNull(submitted.SubmittedAtUtc);
        Assert.Null(submitted.MissingRequirements);
    }

    [Fact]
    public async Task SubmitDraftPreservesCreatedAtUtcAndSetsResubmittedAtUtc()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        Guid ownerId = Guid.NewGuid();
        var fakeMedia = (FakeMediaServiceClient)factory.Services.GetRequiredService<IMediaServiceClient>();
        fakeMedia.DefaultUserId = ownerId;
        fakeMedia.DefaultContentType = "application/pdf";
        await CreateUserAsync(factory, ownerId, "resubmit-owner@example.com", "Resubmit Owner", CustomerRoleId, citizenId: "012345678901");

        Guid orgId = Guid.NewGuid();
        DateTime originalCreatedAt = DateTime.UtcNow.AddDays(-10);
        Guid licenseId = Guid.NewGuid();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization org = new()
            {
                Id = orgId,
                Name = "Công ty Từng Bị Từ Chối",
                Slug = "cong-ty-tung-bi-tu-choi",
                Type = OrganizationType.Supplier,
                Status = OrganizationStatus.Rejected,
                RejectionReason = "Giấy phép mờ",
                ReviewedAtUtc = DateTime.UtcNow.AddDays(-2),
                CreatedAtUtc = originalCreatedAt,
                TaxCode = "0123456789",
                LegalRepresentativeName = "Nguyễn Văn Đại Diện",
                ContactEmail = "contact@resubmit.vn",
                ContactPhone = "0901234567",
                Address = "123 Đường Cũ",
                Province = "TP. Hồ Chí Minh",
                Ward = "Phường Bến Nghé",
                BusinessLicenseDocumentMediaId = licenseId
            };
            Role? ownerRole = await db.Roles.FindAsync(OwnerRoleId);
            OrganizationMembership membership = new()
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                UserId = ownerId,
                RoleId = ownerRole!.Id,
                Status = MembershipStatus.Active,
                JoinedAtUtc = originalCreatedAt
            };
            db.Organizations.Add(org);
            db.OrganizationMemberships.Add(membership);
            await db.SaveChangesAsync();
        }

        using HttpClient client = CreateAuthenticatedClient(factory, ownerId, "customer");

        // Owner edits draft first - should retain rejection reason
        OrganizationDraftRequest editDraft = new(
            Name: "Công ty Đã Sửa Đẹp",
            Type: OrganizationType.Supplier,
            TaxCode: "0123456789",
            LegalRepresentativeName: "Nguyễn Văn Đại Diện",
            ContactEmail: "contact@resubmit.vn",
            ContactPhone: "0901234567",
            Address: "123 Đường Mới",
            Province: "TP. Hồ Chí Minh",
            Ward: "Phường Bến Nghé",
            BusinessLicenseDocumentMediaId: licenseId);
        using HttpResponseMessage editResponse = await client.PostAsJsonAsync("/api/v1/auth/organizations/draft", editDraft);
        Assert.Equal(HttpStatusCode.OK, editResponse.StatusCode);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization orgAfterDraft = await db.Organizations.SingleAsync(o => o.Id == orgId);
            Assert.Equal("Giấy phép mờ", orgAfterDraft.RejectionReason);
            Assert.Equal(OrganizationStatus.Draft, orgAfterDraft.Status);
        }

        // Owner submits draft - should transition to Pending, keep CreatedAtUtc, and set ResubmittedAtUtc
        using HttpResponseMessage submitResponse = await client.PostAsync($"/api/v1/auth/organizations/{orgId}/submit", null);
        Assert.Equal(HttpStatusCode.OK, submitResponse.StatusCode);

        OrganizationResponse? submitted = await submitResponse.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(submitted);
        Assert.Equal(OrganizationStatus.Pending, submitted.Status);
        Assert.Equal(originalCreatedAt, submitted.CreatedAtUtc);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization finalOrg = await db.Organizations.SingleAsync(o => o.Id == orgId);
            Assert.Equal(originalCreatedAt, finalOrg.CreatedAtUtc);
            Assert.NotNull(finalOrg.ResubmittedAtUtc);
            Assert.Null(finalOrg.RejectionReason);
        }
    }

    [Fact]
    public async Task SubmitDraftQueuesPublicLogoOutbox()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        Guid ownerId = Guid.NewGuid();
        Guid logoId = Guid.NewGuid();
        Guid licenseId = Guid.NewGuid();

        var fakeMedia = (FakeMediaServiceClient)factory.Services.GetRequiredService<IMediaServiceClient>();
        fakeMedia.DefaultUserId = ownerId;
        fakeMedia.DefaultContentType = "image/png";
        fakeMedia.Verifications[licenseId] = new MediaVerificationDto(
            licenseId, "Ready", ownerId.ToString("D"), "license.pdf", "application/pdf", 1024, DateTimeOffset.UtcNow);

        await CreateUserAsync(factory, ownerId, "outbox-owner@example.com", "Outbox Owner", CustomerRoleId, citizenId: "012345678901");
        using HttpClient client = CreateAuthenticatedClient(factory, ownerId, "customer");

        OrganizationDraftRequest draftRequest = new(
            Name: "Công ty Có Logo",
            Type: OrganizationType.Supplier,
            TaxCode: "0123456789",
            LegalRepresentativeName: "Nguyễn Văn Đại Diện",
            ContactEmail: "contact@logo.vn",
            ContactPhone: "0901234567",
            Address: "123 Đường Công Trường",
            Province: "Thành phố Hồ Chí Minh",
            Ward: "Phường Bến Nghé",
            LogoMediaId: logoId,
            BusinessLicenseDocumentMediaId: licenseId);

        using HttpResponseMessage draftResponse = await client.PostAsJsonAsync("/api/v1/auth/organizations/draft", draftRequest);
        Assert.Equal(HttpStatusCode.OK, draftResponse.StatusCode);
        OrganizationResponse? draft = await draftResponse.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(draft);

        using HttpResponseMessage submitResponse = await client.PostAsync($"/api/v1/auth/organizations/{draft.Id}/submit", null);
        Assert.Equal(HttpStatusCode.OK, submitResponse.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        List<MediaOutboxMessage> outboxMessages = await db.MediaOutboxMessages.ToListAsync();

        Assert.Contains(outboxMessages, m => m.MediaId == logoId && m.Visibility == "public");
        Assert.DoesNotContain(outboxMessages, m => m.MediaId == licenseId);
    }

    [Fact]
    public async Task MediaDraftAuditToolUnlinksUnauthorizedMedia()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);
        Guid ownerId = Guid.NewGuid();
        Guid attackerId = Guid.NewGuid();
        Guid unauthorizedLicenseId = Guid.NewGuid();

        var fakeMedia = (FakeMediaServiceClient)factory.Services.GetRequiredService<IMediaServiceClient>();
        fakeMedia.AutoVerify = false;
        fakeMedia.Verifications[unauthorizedLicenseId] = new MediaVerificationDto(
            unauthorizedLicenseId, "Ready", attackerId.ToString("D"), "stolen-license.pdf", "application/pdf", 1024, DateTimeOffset.UtcNow);

        await CreateUserAsync(factory, ownerId, "audit-owner@example.com", "Audit Owner", CustomerRoleId);

        Guid orgId = Guid.NewGuid();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization org = new()
            {
                Id = orgId,
                Name = "Công ty Bị Gắn License Lậu",
                Slug = "cong-ty-license-lau",
                Type = OrganizationType.Supplier,
                Status = OrganizationStatus.Draft,
                BusinessLicenseDocumentMediaId = unauthorizedLicenseId,
                CreatedAtUtc = DateTime.UtcNow
            };
            Role? ownerRole = await db.Roles.FindAsync(OwnerRoleId);
            OrganizationMembership membership = new()
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                UserId = ownerId,
                RoleId = ownerRole!.Id,
                Status = MembershipStatus.Active,
                JoinedAtUtc = DateTime.UtcNow
            };
            db.Organizations.Add(org);
            db.OrganizationMemberships.Add(membership);
            await db.SaveChangesAsync();
        }

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            var auditService = scope.ServiceProvider.GetRequiredService<IMediaDraftAuditService>();
            var result = await auditService.AuditAndRepairDraftMediaAsync();
            Assert.True(result.IsSuccess);
            Assert.Equal(1, result.Value.ScannedCount);
            Assert.Equal(1, result.Value.UnlinkedCount);
            Assert.Contains(orgId, result.Value.RepairedOrganizationIds);

            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization repairedOrg = await db.Organizations.SingleAsync(o => o.Id == orgId);
            Assert.Null(repairedOrg.BusinessLicenseDocumentMediaId);

            AuditLog? log = await db.AuditLogs.FirstOrDefaultAsync(l => l.Target == orgId.ToString() && l.Action == "Organization_MediaAuditRepair");
            Assert.NotNull(log);
            Assert.Equal("Success", log.Result);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        string inMemoryDatabaseName = Guid.NewGuid().ToString("N");
        using RSA rsa = RSA.Create(2048);
        Dictionary<string, string?> settings = new()
        {
            ["Auth:Jwt:Issuer"] = "https://auth.example.test",
            ["Auth:Jwt:Audience"] = "dprental-clients",
            ["Auth:Jwt:AccessTokenLifetimeMinutes"] = "15",
            ["Auth:Jwt:ActiveKeyId"] = "current",
            ["Auth:Jwt:Keys:0:KeyId"] = "current",
            ["Auth:Jwt:Keys:0:PublicKeyPem"] = rsa.ExportRSAPublicKeyPem(),
            ["Auth:Jwt:Keys:0:PrivateKeyPem"] = rsa.ExportRSAPrivateKeyPem(),
            ["Auth:AdminBootstrap:CreationKey"] = "test-bootstrap-creation-key"
        };

        return new AuthWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MediaService:ApiKey", "test-media-key");
            builder.UseSetting("MediaService:PublicGatewayUrl", "http://gateway.example.test");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AuthDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AuthDbContext>>();
                services.AddDbContext<AuthDbContext>(options => options.UseInMemoryDatabase(inMemoryDatabaseName));
                services.RemoveAll<IMediaServiceClient>();
                services.AddSingleton<IMediaServiceClient>(new FakeMediaServiceClient { AutoVerify = true });
            });
        });
    }

    private static async Task SeedDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        Role customerRole = new() { Id = CustomerRoleId, Name = "customer", Scope = RoleScope.Global };
        Role adminRole = new() { Id = AdminRoleId, Name = "Admin", Scope = RoleScope.Global };
        Role ownerRole = new() { Id = OwnerRoleId, Name = "Owner", Scope = RoleScope.Organization };
        Role memberRole = new() { Id = MemberRoleId, Name = "Member", Scope = RoleScope.Organization };

        Permission pRead = new() { Id = Guid.NewGuid(), Code = Permissions.Organizations.Read };
        Permission pUpdate = new() { Id = Guid.NewGuid(), Code = Permissions.Organizations.Update };
        Permission pSuspend = new() { Id = Guid.NewGuid(), Code = Permissions.Organizations.Suspend };

        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pRead });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pUpdate });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pSuspend });

        ownerRole.RolePermissions.Add(new RolePermission { Role = ownerRole, Permission = pRead });
        ownerRole.RolePermissions.Add(new RolePermission { Role = ownerRole, Permission = pUpdate });

        db.Permissions.AddRange(pRead, pUpdate, pSuspend);
        db.Roles.AddRange(customerRole, adminRole, ownerRole, memberRole);
        await db.SaveChangesAsync();
    }

    private static async Task CreateUserAsync(
        WebApplicationFactory<Program> factory,
        Guid userId,
        string email,
        string fullName,
        Guid roleId,
        string? citizenId = null)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        User user = new()
        {
            Id = userId,
            Email = email,
            FullName = fullName,
            PasswordHash = "hash",
            CitizenId = citizenId,
            CreatedAt = DateTime.UtcNow,
            ProfileCompletedAtUtc = DateTime.UtcNow,
            IsActive = true
        };
        user.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    private static HttpClient CreateAuthenticatedClient(
        WebApplicationFactory<Program> factory,
        Guid userId,
        params string[] roles)
    {
        HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Session session = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            RevokedAtUtc = null
        };
        dbContext.Sessions.Add(session);
        dbContext.SaveChanges();

        List<string> permissions = dbContext.UserRoles
            .Where(ur => ur.UserId == userId)
            .SelectMany(ur => ur.Role.RolePermissions.Select(rp => rp.Permission.Code))
            .Distinct()
            .ToList();

        IAccessTokenIssuer issuer = scope.ServiceProvider.GetRequiredService<IAccessTokenIssuer>();
        IssuedAccessToken token = issuer.Issue(new AccessTokenRequest(userId, roles, Permissions: permissions, SessionId: session.Id));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return client;
    }
}
