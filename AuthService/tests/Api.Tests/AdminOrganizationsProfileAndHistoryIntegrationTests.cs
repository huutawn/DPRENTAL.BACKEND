using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.DTOs.Media;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed partial class AdminOrganizationsEndpointsIntegrationTests
{
    // =========================================================================
    // 6.1. GET /api/v1/auth/admin/organizations (Filters)
    // =========================================================================

    [Fact]
    public async Task ListOrganizations_Filters_TypeProvinceOverdueRemindedAndInvalidEnum()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);

        DateTime threeDaysAgo = DateTime.UtcNow.AddDays(-3);
        DateTime oneHourAgo = DateTime.UtcNow.AddHours(-1);
        DateTime tenMinutesAgo = DateTime.UtcNow.AddMinutes(-10);

        // Pending 1: Supplier, Hà Nội, Created 3 days ago (overdue), not reminded
        Guid org1 = await SeedOrganizationAsync(factory, "Org 1", "org-1", OrganizationStatus.Pending, threeDaysAgo,
            type: OrganizationType.Supplier, province: "Hà Nội");

        // Pending 2: Renter, TP.HCM, Created 1 hour ago (not overdue), reminded
        Guid org2 = await SeedOrganizationAsync(factory, "Org 2", "org-2", OrganizationStatus.Pending, oneHourAgo,
            type: OrganizationType.Renter, province: "TP.HCM", lastRemindedAt: tenMinutesAgo);

        // Pending 3: Supplier, Đà Nẵng, Created 3 days ago (overdue), reminded
        Guid org3 = await SeedOrganizationAsync(factory, "Org 3", "org-3", OrganizationStatus.Pending, threeDaysAgo,
            type: OrganizationType.Supplier, province: "Đà Nẵng", lastRemindedAt: tenMinutesAgo);

        // Active 1: Supplier, Hà Nội
        Guid orgActive = await SeedOrganizationAsync(factory, "Org Active", "org-active", OrganizationStatus.Active, DateTime.UtcNow,
            type: OrganizationType.Supplier, province: "Hà Nội");

        using HttpClient adminClient = CreateClientWithToken(factory, adminId, ["Admin"], [Permissions.Organizations.Read]);

        // 1. Filter by type=Supplier -> org1, org3, orgActive (3 total)
        using HttpResponseMessage supplierRes = await adminClient.GetAsync("/api/v1/auth/admin/organizations?type=Supplier");
        Assert.Equal(HttpStatusCode.OK, supplierRes.StatusCode);
        PagedResult<OrganizationSummaryResponse>? supplierPaged = await supplierRes.Content.ReadFromJsonAsync<PagedResult<OrganizationSummaryResponse>>();
        Assert.NotNull(supplierPaged);
        Assert.Equal(3, supplierPaged.TotalCount);

        // 2. Filter by type=Renter -> org2 (1 total)
        using HttpResponseMessage renterRes = await adminClient.GetAsync("/api/v1/auth/admin/organizations?type=Renter");
        Assert.Equal(HttpStatusCode.OK, renterRes.StatusCode);
        PagedResult<OrganizationSummaryResponse>? renterPaged = await renterRes.Content.ReadFromJsonAsync<PagedResult<OrganizationSummaryResponse>>();
        Assert.NotNull(renterPaged);
        Assert.Single(renterPaged.Items, o => o.Id == org2);

        // 3. Filter by province=hà nội (case-insensitive) -> org1, orgActive
        using HttpResponseMessage provinceRes = await adminClient.GetAsync("/api/v1/auth/admin/organizations?province=h%C3%A0%20n%E1%BB%99i");
        Assert.Equal(HttpStatusCode.OK, provinceRes.StatusCode);
        PagedResult<OrganizationSummaryResponse>? provincePaged = await provinceRes.Content.ReadFromJsonAsync<PagedResult<OrganizationSummaryResponse>>();
        Assert.NotNull(provincePaged);
        Assert.Equal(2, provincePaged.TotalCount);

        // 4. Filter overdueOnly=true -> only pending orgs that are overdue (org1, org3)
        using HttpResponseMessage overdueRes = await adminClient.GetAsync("/api/v1/auth/admin/organizations?overdueOnly=true");
        Assert.Equal(HttpStatusCode.OK, overdueRes.StatusCode);
        PagedResult<OrganizationSummaryResponse>? overduePaged = await overdueRes.Content.ReadFromJsonAsync<PagedResult<OrganizationSummaryResponse>>();
        Assert.NotNull(overduePaged);
        Assert.Equal(2, overduePaged.TotalCount);
        Assert.Contains(overduePaged.Items, o => o.Id == org1);
        Assert.Contains(overduePaged.Items, o => o.Id == org3);

        // 5. Filter remindedOnly=true -> only pending orgs that are reminded (org2, org3)
        using HttpResponseMessage remindedRes = await adminClient.GetAsync("/api/v1/auth/admin/organizations?remindedOnly=true");
        Assert.Equal(HttpStatusCode.OK, remindedRes.StatusCode);
        PagedResult<OrganizationSummaryResponse>? remindedPaged = await remindedRes.Content.ReadFromJsonAsync<PagedResult<OrganizationSummaryResponse>>();
        Assert.NotNull(remindedPaged);
        Assert.Equal(2, remindedPaged.TotalCount);
        Assert.Contains(remindedPaged.Items, o => o.Id == org2);
        Assert.Contains(remindedPaged.Items, o => o.Id == org3);

        // 6. Combined filter with AND: type=Supplier & province=Đà Nẵng & overdueOnly=true -> org3
        using HttpResponseMessage combinedRes = await adminClient.GetAsync("/api/v1/auth/admin/organizations?type=Supplier&province=%C4%90%C3%A0%20N%E1%BA%B5ng&overdueOnly=true");
        Assert.Equal(HttpStatusCode.OK, combinedRes.StatusCode);
        PagedResult<OrganizationSummaryResponse>? combinedPaged = await combinedRes.Content.ReadFromJsonAsync<PagedResult<OrganizationSummaryResponse>>();
        Assert.NotNull(combinedPaged);
        Assert.Single(combinedPaged.Items, o => o.Id == org3);

        // 7. Status=Active & overdueOnly=true -> 0 items (AND contradiction)
        using HttpResponseMessage contradictionRes = await adminClient.GetAsync("/api/v1/auth/admin/organizations?status=Active&overdueOnly=true");
        Assert.Equal(HttpStatusCode.OK, contradictionRes.StatusCode);
        PagedResult<OrganizationSummaryResponse>? contradictionPaged = await contradictionRes.Content.ReadFromJsonAsync<PagedResult<OrganizationSummaryResponse>>();
        Assert.NotNull(contradictionPaged);
        Assert.Equal(0, contradictionPaged.TotalCount);

        // 8. Invalid enum -> 400
        using HttpResponseMessage invalidEnumRes = await adminClient.GetAsync("/api/v1/auth/admin/organizations?type=InvalidType");
        Assert.Equal(HttpStatusCode.BadRequest, invalidEnumRes.StatusCode);
        using HttpResponseMessage numericTypeRes = await adminClient.GetAsync("/api/v1/auth/admin/organizations?type=999");
        Assert.Equal(HttpStatusCode.BadRequest, numericTypeRes.StatusCode);
        using HttpResponseMessage numericStatusRes = await adminClient.GetAsync("/api/v1/auth/admin/organizations?status=999");
        Assert.Equal(HttpStatusCode.BadRequest, numericStatusRes.StatusCode);
    }

    // =========================================================================
    // 6.2. PUT /api/v1/auth/admin/organizations/{id} (Clear fields, keep legal, media checks)
    // =========================================================================

    [Fact]
    public async Task UpdateOrganizationProfile_ClearOptionalFields_DoesNotChangeLegalInfo_AndValidatesMedia()
    {
        FakeMediaServiceClient mediaClient = new() { AutoVerify = false };
        using WebApplicationFactory<Program> factory = CreateFactory(fakeMediaClient: mediaClient);
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);

        Guid oldLogoId = Guid.NewGuid();
        Guid licenseDocId = Guid.NewGuid();
        Guid orgId = await SeedOrganizationAsync(factory, "Initial Org", "initial-org", OrganizationStatus.Active, DateTime.UtcNow,
            licenseMediaId: licenseDocId, logoMediaId: oldLogoId);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization org = await db.Organizations.SingleAsync(o => o.Id == orgId);
            org.Website = "https://old-website.com";
            org.Description = "Old Description";
            org.TaxCode = "0102030405";
            org.LegalRepresentativeName = "Nguyễn Văn Pháp Lý";
            await db.SaveChangesAsync();
        }

        using HttpClient adminClient = CreateClientWithToken(factory, adminId, ["Admin"], [Permissions.Organizations.Update]);

        // 1. Clear optional fields (Website=null, LogoMediaId=null, Description=null) -> 204
        UpdateOrganizationRequest clearRequest = new(
            "Updated Name",
            "contact@updated.com",
            "0987654321",
            "999 New Street",
            "Hà Nội",
            "Phường 1",
            Website: null,
            LogoMediaId: null,
            Description: null);

        using HttpResponseMessage clearRes = await adminClient.PutAsJsonAsync($"/api/v1/auth/admin/organizations/{orgId}", clearRequest);
        Assert.Equal(HttpStatusCode.NoContent, clearRes.StatusCode);

        // Verify DB: optional fields cleared, legal info unchanged!
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization updated = await db.Organizations.SingleAsync(o => o.Id == orgId);
            Assert.Equal("Updated Name", updated.Name);
            Assert.Null(updated.Website);
            Assert.Null(updated.LogoMediaId);
            Assert.Null(updated.Description);
            // Legal info must NOT change!
            Assert.Equal("0102030405", updated.TaxCode);
            Assert.Equal("Nguyễn Văn Pháp Lý", updated.LegalRepresentativeName);
            Assert.Equal("initial-org", updated.Slug);
            Assert.Equal(OrganizationType.Supplier, updated.Type);
            Assert.Equal(OrganizationStatus.Active, updated.Status);
            Assert.Equal(licenseDocId, updated.BusinessLicenseDocumentMediaId);
        }

        // 2. Media validation: logo uploaded by another user -> 403 Forbidden
        Guid otherUserId = Guid.NewGuid();
        Guid wrongOwnerMediaId = Guid.NewGuid();
        mediaClient.Verifications[wrongOwnerMediaId] = new MediaVerificationDto(
            wrongOwnerMediaId, "Ready", otherUserId.ToString(), "test.png", "image/png", 1024, DateTimeOffset.UtcNow);

        UpdateOrganizationRequest wrongOwnerReq = new(
            "Updated Name", "contact@updated.com", "0987654321", "999 New Street", "Hà Nội", "Phường 1",
            LogoMediaId: wrongOwnerMediaId);
        using HttpResponseMessage wrongOwnerRes = await adminClient.PutAsJsonAsync($"/api/v1/auth/admin/organizations/{orgId}", wrongOwnerReq);
        Assert.Equal(HttpStatusCode.Forbidden, wrongOwnerRes.StatusCode);

        // 3. Media validation: logo not Ready -> 409 Conflict
        Guid notReadyMediaId = Guid.NewGuid();
        mediaClient.Verifications[notReadyMediaId] = new MediaVerificationDto(
            notReadyMediaId, "Pending", adminId.ToString(), "test.png", "image/png", 1024, DateTimeOffset.UtcNow);
        UpdateOrganizationRequest notReadyReq = new(
            "Updated Name", "contact@updated.com", "0987654321", "999 New Street", "Hà Nội", "Phường 1",
            LogoMediaId: notReadyMediaId);
        using HttpResponseMessage notReadyRes = await adminClient.PutAsJsonAsync($"/api/v1/auth/admin/organizations/{orgId}", notReadyReq);
        Assert.Equal(HttpStatusCode.Conflict, notReadyRes.StatusCode);

        // 4. Media validation: logo is PDF instead of image -> 400 BadRequest
        Guid pdfMediaId = Guid.NewGuid();
        mediaClient.Verifications[pdfMediaId] = new MediaVerificationDto(
            pdfMediaId, "Ready", adminId.ToString(), "doc.pdf", "application/pdf", 1024, DateTimeOffset.UtcNow);
        UpdateOrganizationRequest pdfReq = new(
            "Updated Name", "contact@updated.com", "0987654321", "999 New Street", "Hà Nội", "Phường 1",
            LogoMediaId: pdfMediaId);
        using HttpResponseMessage pdfRes = await adminClient.PutAsJsonAsync($"/api/v1/auth/admin/organizations/{orgId}", pdfReq);
        Assert.Equal(HttpStatusCode.BadRequest, pdfRes.StatusCode);

        // 5. Media validation: MediaService failure -> 500
        Guid failMediaId = Guid.NewGuid();
        mediaClient.VerifyError = AuthErrors.Media.ServiceUnavailable;
        UpdateOrganizationRequest failReq = new(
            "Updated Name", "contact@updated.com", "0987654321", "999 New Street", "Hà Nội", "Phường 1",
            LogoMediaId: failMediaId);
        using HttpResponseMessage failRes = await adminClient.PutAsJsonAsync($"/api/v1/auth/admin/organizations/{orgId}", failReq);
        Assert.Equal(HttpStatusCode.InternalServerError, failRes.StatusCode);
        mediaClient.VerifyError = null;

        // 6. Valid logo uploaded by admin -> 204
        Guid validLogoId = Guid.NewGuid();
        mediaClient.Verifications[validLogoId] = new MediaVerificationDto(
            validLogoId, "Ready", adminId.ToString(), "logo.png", "image/png", 2048, DateTimeOffset.UtcNow);
        UpdateOrganizationRequest validLogoReq = new(
            "Updated Name", "contact@updated.com", "0987654321", "999 New Street", "Hà Nội", "Phường 1",
            LogoMediaId: validLogoId);
        using HttpResponseMessage validLogoRes = await adminClient.PutAsJsonAsync($"/api/v1/auth/admin/organizations/{orgId}", validLogoReq);
        Assert.Equal(HttpStatusCode.NoContent, validLogoRes.StatusCode);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization updated = await db.Organizations.SingleAsync(o => o.Id == orgId);
            Assert.Equal(validLogoId, updated.LogoMediaId);
        }
    }

    // =========================================================================
    // 6.3. Suspend and Activate: Owner sees reason, cleared on activate, history keeps it
    // =========================================================================

    [Fact]
    public async Task SuspendAndActivate_SuspensionReasonVisibleToOwner_ClearedOnActivate_PreservedInHistory()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);
        await SeedUserAsync(factory, ownerId, "owner@example.com", "Owner", CustomerRoleId);

        Guid orgId = await SeedOrganizationAsync(factory, "Suspension Lifecycle Org", "susp-lifecycle", OrganizationStatus.Active, DateTime.UtcNow, ownerId);

        using HttpClient adminClient = CreateClientWithToken(factory, adminId, ["Admin"], [Permissions.Organizations.Suspend, Permissions.Organizations.Read]);
        using HttpClient ownerClient = CreateClientWithToken(factory, ownerId, ["Customer"], []);

        // 1. Admin suspends org with reason
        string reason = "Tài khoản có dấu hiệu vi phạm điều khoản";
        using HttpResponseMessage suspendRes = await adminClient.PostAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{orgId}/suspend",
            new SuspendOrganizationRequest(reason));
        Assert.Equal(HttpStatusCode.NoContent, suspendRes.StatusCode);

        // 2. Owner checks GET /{id} -> reads suspensionReason
        using HttpResponseMessage ownerGetRes = await ownerClient.GetAsync($"/api/v1/auth/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.OK, ownerGetRes.StatusCode);
        OrganizationResponse? ownerOrg = await ownerGetRes.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(ownerOrg);
        Assert.Equal(OrganizationStatus.Suspended, ownerOrg.Status);
        Assert.Equal(reason, ownerOrg.SuspensionReason);

        // 3. Owner checks GET /mine -> reads suspensionReason
        using HttpResponseMessage ownerMineRes = await ownerClient.GetAsync("/api/v1/auth/organizations/mine");
        Assert.Equal(HttpStatusCode.OK, ownerMineRes.StatusCode);
        OrganizationResponse? ownerMine = await ownerMineRes.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(ownerMine);
        Assert.Equal(reason, ownerMine.SuspensionReason);

        // 4. Admin activates org -> 204
        using HttpResponseMessage activateRes = await adminClient.PostAsync($"/api/v1/auth/admin/organizations/{orgId}/activate", null);
        Assert.Equal(HttpStatusCode.NoContent, activateRes.StatusCode);

        // 5. Owner checks GET /{id} and /mine -> suspensionReason is cleared (null)
        using HttpResponseMessage ownerGetAfter = await ownerClient.GetAsync($"/api/v1/auth/organizations/{orgId}");
        OrganizationResponse? activeOrg = await ownerGetAfter.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(activeOrg);
        Assert.Equal(OrganizationStatus.Active, activeOrg.Status);
        Assert.Null(activeOrg.SuspensionReason);

        using HttpResponseMessage ownerMineAfter = await ownerClient.GetAsync("/api/v1/auth/organizations/mine");
        OrganizationResponse? activeMine = await ownerMineAfter.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(activeMine);
        Assert.Null(activeMine.SuspensionReason);

        // 6. Admin checks GET /history -> previous suspension reason is PRESERVED in history
        using HttpResponseMessage historyRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history");
        Assert.Equal(HttpStatusCode.OK, historyRes.StatusCode);
        PagedResult<OrganizationHistoryResponse>? history = await historyRes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(history);

        OrganizationHistoryResponse suspendLog = Assert.Single(history.Items, h => h.Action == "Organization_Suspend");
        Assert.Equal(reason, suspendLog.Reason);
        Assert.Equal(adminId, suspendLog.ActorId);

        OrganizationHistoryResponse activateLog = Assert.Single(history.Items, h => h.Action == "Organization_Activate");
        Assert.Null(activateLog.Reason);
    }

    // =========================================================================
    // 6.4. GET /api/v1/auth/admin/organizations/{id}/history (Paging, Isolation, Security)
    // =========================================================================

    [Fact]
    public async Task GetHistory_PagedNewestFirst_ScopedToOrg_NoLeaksAndForbiddenForOwner()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);
        await SeedUserAsync(factory, ownerId, "owner@example.com", "Owner", CustomerRoleId);

        Guid orgA = await SeedOrganizationAsync(factory, "Org A", "org-a", OrganizationStatus.Active, DateTime.UtcNow, ownerId);
        Guid orgB = await SeedOrganizationAsync(factory, "Org B", "org-b", OrganizationStatus.Active, DateTime.UtcNow);

        // Seed several audit logs for Org A
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            DateTimeOffset baseTime = DateTimeOffset.UtcNow.AddMinutes(-30);

            db.AuditLogs.AddRange(
                new AuditLog { Id = Guid.NewGuid(), ActorId = ownerId, Target = orgA.ToString(), Action = "Organization_Register", Result = "Success", Timestamp = baseTime },
                new AuditLog { Id = Guid.NewGuid(), ActorId = ownerId, Target = orgA.ToString(), Action = "Organization_ProfileUpdate", Result = "Success", Timestamp = baseTime.AddMinutes(5) },
                new AuditLog { Id = Guid.NewGuid(), ActorId = adminId, Target = orgA.ToString(), Action = "Organization_Suspend", Result = "Success", Reason = "Reason A", Timestamp = baseTime.AddMinutes(10) },
                new AuditLog { Id = Guid.NewGuid(), ActorId = adminId, Target = orgA.ToString(), Action = "Organization_Activate", Result = "Success", Timestamp = baseTime.AddMinutes(15) },
                // Audit log for Org B (should not leak into Org A)
                new AuditLog { Id = Guid.NewGuid(), ActorId = adminId, Target = orgB.ToString(), Action = "Organization_Suspend", Result = "Success", Reason = "Reason B", Timestamp = baseTime.AddMinutes(20) },
                // Audit log for a user action (should not leak into org history)
                new AuditLog { Id = Guid.NewGuid(), ActorId = ownerId, Target = ownerId.ToString(), Action = "Login", Result = "Success", Timestamp = baseTime.AddMinutes(25) }
            );
            await db.SaveChangesAsync();
        }

        using HttpClient adminClient = CreateClientWithToken(factory, adminId, ["Admin"], [Permissions.Organizations.Read]);
        using HttpClient ownerClient = CreateClientWithToken(factory, ownerId, ["Customer"], [Permissions.Organizations.Read]);
        using HttpClient anonClient = factory.CreateClient();

        // 1. Success with pagination: page 1, pageSize 2 -> newest first
        using HttpResponseMessage page1Res = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgA}/history?page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, page1Res.StatusCode);
        PagedResult<OrganizationHistoryResponse>? page1 = await page1Res.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(page1);
        Assert.Equal(4, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal("Organization_Activate", page1.Items[0].Action);
        Assert.Equal("Organization_Suspend", page1.Items[1].Action);
        Assert.Equal("Reason A", page1.Items[1].Reason);

        // 2. Page 2 -> older actions
        using HttpResponseMessage page2Res = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgA}/history?page=2&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, page2Res.StatusCode);
        PagedResult<OrganizationHistoryResponse>? page2 = await page2Res.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(page2);
        Assert.Equal(4, page2.TotalCount);
        Assert.Equal(2, page2.Items.Count);
        Assert.Equal("Organization_ProfileUpdate", page2.Items[0].Action);
        Assert.Equal("Organization_Register", page2.Items[1].Action);

        // 3. Security / Privacy verification:
        // Verify NO Org B logs and NO user logs appear in Org A history
        using HttpResponseMessage allOrgARes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgA}/history?pageSize=10");
        PagedResult<OrganizationHistoryResponse>? allOrgA = await allOrgARes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(allOrgA);
        Assert.DoesNotContain(allOrgA.Items, h => h.Reason == "Reason B");
        Assert.DoesNotContain(allOrgA.Items, h => h.Action == "Login");

        // 4. NotFound -> 404 for non-existent org
        using HttpResponseMessage notFoundRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{Guid.NewGuid()}/history");
        Assert.Equal(HttpStatusCode.NotFound, notFoundRes.StatusCode);

        // 5. Owner with permission in JWT but no global DB role -> 403 Forbidden
        using HttpResponseMessage ownerForbidRes = await ownerClient.GetAsync($"/api/v1/auth/admin/organizations/{orgA}/history");
        Assert.Equal(HttpStatusCode.Forbidden, ownerForbidRes.StatusCode);

        // 6. Anonymous -> 401 Unauthorized
        using HttpResponseMessage anonRes = await anonClient.GetAsync($"/api/v1/auth/admin/organizations/{orgA}/history");
        Assert.Equal(HttpStatusCode.Unauthorized, anonRes.StatusCode);
    }

    [Fact]
    public async Task GetHistory_SearchAndFilters_IndividualAndCombinedWithAnd()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin_hist@example.com", "Admin Hist", AdminRoleId);
        Guid orgId = await SeedOrganizationAsync(factory, "Org Hist Test", "org-hist-test", OrganizationStatus.Active, DateTime.UtcNow);

        DateTimeOffset baseTime = DateTimeOffset.UtcNow.AddMinutes(-60);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.AuditLogs.AddRange(
                new AuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorId = adminId,
                    Target = orgId.ToString(),
                    Action = "Organization_Suspend",
                    Status = OrganizationStatus.Suspended,
                    Result = "Success",
                    Reason = "Giấy phép kinh doanh hết hạn",
                    Timestamp = baseTime.AddMinutes(10)
                },
                new AuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorId = adminId,
                    Target = orgId.ToString(),
                    Action = "Organization_Activate",
                    Status = OrganizationStatus.Active,
                    Result = "Success",
                    Reason = "Đã bổ sung giấy phép",
                    Timestamp = baseTime.AddMinutes(20)
                },
                new AuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorId = adminId,
                    Target = orgId.ToString(),
                    Action = "Organization_ProfileUpdate",
                    Status = null,
                    Result = "Success",
                    Reason = null,
                    Timestamp = baseTime.AddMinutes(30)
                },
                new AuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorId = adminId,
                    Target = orgId.ToString(),
                    Action = "Organization_Suspend",
                    Status = OrganizationStatus.Suspended,
                    Result = "Failed",
                    Reason = "Lỗi hệ thống khi khóa",
                    Timestamp = baseTime.AddMinutes(40)
                }
            );
            await db.SaveChangesAsync();
        }

        using HttpClient adminClient = CreateClientWithToken(factory, adminId, ["Admin"], [Permissions.Organizations.Read]);

        // 1. Filter by action: exact match, case-insensitive
        using HttpResponseMessage actionRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?action=organization_suspend");
        Assert.Equal(HttpStatusCode.OK, actionRes.StatusCode);
        PagedResult<OrganizationHistoryResponse>? actionPaged = await actionRes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(actionPaged);
        Assert.Equal(2, actionPaged.TotalCount);
        Assert.All(actionPaged.Items, item => Assert.Equal("Organization_Suspend", item.Action));

        // 2. Filter by status: exact match OrganizationStatus, null rows do not match
        using HttpResponseMessage statusRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?status=Suspended");
        Assert.Equal(HttpStatusCode.OK, statusRes.StatusCode);
        PagedResult<OrganizationHistoryResponse>? statusPaged = await statusRes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(statusPaged);
        Assert.Equal(2, statusPaged.TotalCount);
        Assert.All(statusPaged.Items, item => Assert.Equal(OrganizationStatus.Suspended, item.Status));

        // 3. Filter by result: exact match, case-insensitive
        using HttpResponseMessage resultRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?result=FAILED");
        Assert.Equal(HttpStatusCode.OK, resultRes.StatusCode);
        PagedResult<OrganizationHistoryResponse>? resultPaged = await resultRes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(resultPaged);
        Assert.Equal(1, resultPaged.TotalCount);
        Assert.Equal("Failed", resultPaged.Items[0].Result);

        // 4. Filter by reason: substring match, case-insensitive with Vietnamese
        using HttpResponseMessage reasonRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?reason=giấy%20phép");
        Assert.Equal(HttpStatusCode.OK, reasonRes.StatusCode);
        PagedResult<OrganizationHistoryResponse>? reasonPaged = await reasonRes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(reasonPaged);
        Assert.Equal(2, reasonPaged.TotalCount);

        // 5. AND combination matching prompt example: action=Organization_Suspend&status=Suspended&result=Success&reason=giấy phép
        using HttpResponseMessage andRes = await adminClient.GetAsync(
            $"/api/v1/auth/admin/organizations/{orgId}/history?action=Organization_Suspend&status=Suspended&result=Success&reason=giấy%20phép");
        Assert.Equal(HttpStatusCode.OK, andRes.StatusCode);
        PagedResult<OrganizationHistoryResponse>? andPaged = await andRes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(andPaged);
        Assert.Equal(1, andPaged.TotalCount);
        Assert.Equal("Organization_Suspend", andPaged.Items[0].Action);
        Assert.Equal(OrganizationStatus.Suspended, andPaged.Items[0].Status);
        Assert.Equal("Success", andPaged.Items[0].Result);
        Assert.Equal("Giấy phép kinh doanh hết hạn", andPaged.Items[0].Reason);

        // 6. Whitespace trimming & blank treated as not filtered
        using HttpResponseMessage trimRes = await adminClient.GetAsync(
            $"/api/v1/auth/admin/organizations/{orgId}/history?action=%20Organization_Suspend%20&reason=%20%20");
        Assert.Equal(HttpStatusCode.OK, trimRes.StatusCode);
        PagedResult<OrganizationHistoryResponse>? trimPaged = await trimRes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(trimPaged);
        Assert.Equal(2, trimPaged.TotalCount);

        // 7. No match returns empty result with TotalCount = 0
        using HttpResponseMessage noMatchRes = await adminClient.GetAsync(
            $"/api/v1/auth/admin/organizations/{orgId}/history?action=Organization_Suspend&status=Active");
        Assert.Equal(HttpStatusCode.OK, noMatchRes.StatusCode);
        PagedResult<OrganizationHistoryResponse>? noMatchPaged = await noMatchRes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(noMatchPaged);
        Assert.Equal(0, noMatchPaged.TotalCount);
        Assert.Equal(0, noMatchPaged.TotalPages);
        Assert.Empty(noMatchPaged.Items);
    }

    [Fact]
    public async Task GetHistory_Search_OrCombinationAcrossFields_VietnameseAndSpecialCharacters()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin_search@example.com", "Admin Search", AdminRoleId);
        Guid orgId = await SeedOrganizationAsync(factory, "Org Search Test", "org-search-test", OrganizationStatus.Active, DateTime.UtcNow);

        DateTimeOffset baseTime = DateTimeOffset.UtcNow.AddMinutes(-60);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.AuditLogs.AddRange(
                new AuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorId = adminId,
                    Target = orgId.ToString(),
                    Action = "Organization_Suspend",
                    Status = OrganizationStatus.Suspended,
                    Result = "Success",
                    Reason = @"Lỗi 100%_hợp\đồng",
                    Timestamp = baseTime.AddMinutes(10)
                },
                new AuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorId = adminId,
                    Target = orgId.ToString(),
                    Action = "Organization_Activate",
                    Status = OrganizationStatus.Active,
                    Result = "Success",
                    Reason = "Kích hoạt lại bình thường",
                    Timestamp = baseTime.AddMinutes(20)
                },
                new AuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorId = adminId,
                    Target = orgId.ToString(),
                    Action = "Organization_Register",
                    Status = OrganizationStatus.Pending,
                    Result = "Success",
                    Reason = "Đăng ký mới hồ sơ",
                    Timestamp = baseTime.AddMinutes(30)
                }
            );
            await db.SaveChangesAsync();
        }

        using HttpClient adminClient = CreateClientWithToken(factory, adminId, ["Admin"], [Permissions.Organizations.Read]);

        // 1. Search by Action substring
        using HttpResponseMessage searchActionRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?search=Suspend");
        PagedResult<OrganizationHistoryResponse>? searchAction = await searchActionRes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(searchAction);
        Assert.Equal(1, searchAction.TotalCount);
        Assert.Equal("Organization_Suspend", searchAction.Items[0].Action);

        // 2. Search by Status enum name (matches Pending)
        using HttpResponseMessage searchStatusRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?search=Pending");
        PagedResult<OrganizationHistoryResponse>? searchStatus = await searchStatusRes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(searchStatus);
        Assert.Equal(1, searchStatus.TotalCount);
        Assert.Equal(OrganizationStatus.Pending, searchStatus.Items[0].Status);

        // 3. Search by Reason substring with Vietnamese
        using HttpResponseMessage searchVnRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?search=kích%20hoạt");
        PagedResult<OrganizationHistoryResponse>? searchVn = await searchVnRes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(searchVn);
        Assert.Equal(1, searchVn.TotalCount);
        Assert.Equal("Organization_Activate", searchVn.Items[0].Action);

        // 4. Search with special character '%' as literal (only matches 100%, not all rows)
        using HttpResponseMessage searchPctRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?search=%25");
        PagedResult<OrganizationHistoryResponse>? searchPct = await searchPctRes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(searchPct);
        Assert.Equal(1, searchPct.TotalCount);
        Assert.Equal(@"Lỗi 100%_hợp\đồng", searchPct.Items[0].Reason);

        // 5. Search with special character '_' as literal
        using HttpResponseMessage searchUnderscoreRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?search=_");
        PagedResult<OrganizationHistoryResponse>? searchUnderscore = await searchUnderscoreRes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        // Note: Action has "Organization_" which also contains "_", so both Action and Reason match
        Assert.NotNull(searchUnderscore);
        Assert.Equal(3, searchUnderscore.TotalCount);

        // 6. Reason filter with special character '_' as literal (only matches Reason containing '_')
        using HttpResponseMessage reasonUnderscoreRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?reason=_");
        PagedResult<OrganizationHistoryResponse>? reasonUnderscore = await reasonUnderscoreRes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(reasonUnderscore);
        Assert.Equal(1, reasonUnderscore.TotalCount);
        Assert.Equal(@"Lỗi 100%_hợp\đồng", reasonUnderscore.Items[0].Reason);

        // 7. Search with special character '\' as literal
        using HttpResponseMessage searchBackslashRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?search=%5C");
        PagedResult<OrganizationHistoryResponse>? searchBackslash = await searchBackslashRes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(searchBackslash);
        Assert.Equal(1, searchBackslash.TotalCount);
        Assert.Equal(@"Lỗi 100%_hợp\đồng", searchBackslash.Items[0].Reason);
    }

    [Fact]
    public async Task GetHistory_Paging_ValidationAndOverflow()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin_paging@example.com", "Admin Paging", AdminRoleId);
        Guid orgId = await SeedOrganizationAsync(factory, "Org Paging Test", "org-paging-test", OrganizationStatus.Active, DateTime.UtcNow);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            for (int i = 0; i < 5; i++)
            {
                db.AuditLogs.Add(new AuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorId = adminId,
                    Target = orgId.ToString(),
                    Action = "Organization_ProfileUpdate",
                    Status = OrganizationStatus.Active,
                    Result = "Success",
                    Timestamp = now.AddMinutes(i)
                });
            }
            await db.SaveChangesAsync();
        }

        using HttpClient adminClient = CreateClientWithToken(factory, adminId, ["Admin"], [Permissions.Organizations.Read]);

        // 1. Invalid page <= 0 -> 400
        using HttpResponseMessage pageZeroRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?page=0");
        Assert.Equal(HttpStatusCode.BadRequest, pageZeroRes.StatusCode);

        // 2. Invalid pageSize > 100 -> 400
        using HttpResponseMessage pageSizeRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?pageSize=101");
        Assert.Equal(HttpStatusCode.BadRequest, pageSizeRes.StatusCode);

        // 3. Offset overflow: (page - 1) * pageSize > int.MaxValue -> 400 Bad Request
        using HttpResponseMessage overflowRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?page={int.MaxValue}&pageSize=20");
        Assert.Equal(HttpStatusCode.BadRequest, overflowRes.StatusCode);
        string overflowBody = await overflowRes.Content.ReadAsStringAsync();
        Assert.Contains(AuthErrorCode.Paging.InvalidPage, overflowBody);

        // 4. Invalid status string -> 400 Bad Request
        using HttpResponseMessage invalidStatusRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?status=NotAStatus");
        Assert.Equal(HttpStatusCode.BadRequest, invalidStatusRes.StatusCode);

        // 5. Overlong search > 200 chars -> 400 Bad Request
        string overlong = new('a', 201);
        using HttpResponseMessage overlongSearchRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?search={overlong}");
        Assert.Equal(HttpStatusCode.BadRequest, overlongSearchRes.StatusCode);

        // 6. Overlong reason > 200 chars -> 400 Bad Request
        using HttpResponseMessage overlongReasonRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?reason={overlong}");
        Assert.Equal(HttpStatusCode.BadRequest, overlongReasonRes.StatusCode);

        // 7. Page beyond total results -> 200 OK, items empty, page & totalCount intact
        using HttpResponseMessage beyondRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history?page=99&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, beyondRes.StatusCode);
        PagedResult<OrganizationHistoryResponse>? beyond = await beyondRes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(beyond);
        Assert.Equal(5, beyond.TotalCount);
        Assert.Equal(99, beyond.Page);
        Assert.Empty(beyond.Items);
    }

    [Fact]
    public async Task GetHistory_StatusSnapshot_RecordedAtEventTimeAndImmutableOnLaterChanges()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin_snap@example.com", "Admin Snap", AdminRoleId);
        await SeedUserAsync(factory, ownerId, "owner_snap@example.com", "Owner Snap", CustomerRoleId);

        Guid orgId = await SeedOrganizationAsync(factory, "Org Snap Test", "org-snap-test", OrganizationStatus.Active, DateTime.UtcNow, ownerId);

        using HttpClient adminClient = CreateClientWithToken(factory, adminId, ["Admin"], [
            Permissions.Organizations.Read,
            Permissions.Organizations.Suspend,
            Permissions.Organizations.Update
        ]);

        // Suspend org (transitions Active -> Suspended)
        using HttpResponseMessage suspendRes = await adminClient.PostAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{orgId}/suspend",
            new SuspendOrganizationRequest("Vi phạm lần 1"));
        Assert.Equal(HttpStatusCode.NoContent, suspendRes.StatusCode);

        // Activate org (transitions Suspended -> Active)
        using HttpResponseMessage activateRes = await adminClient.PostAsync(
            $"/api/v1/auth/admin/organizations/{orgId}/activate",
            null);
        Assert.Equal(HttpStatusCode.NoContent, activateRes.StatusCode);

        // Verify history: newest first
        using HttpResponseMessage histRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}/history");
        Assert.Equal(HttpStatusCode.OK, histRes.StatusCode);
        PagedResult<OrganizationHistoryResponse>? hist = await histRes.Content.ReadFromJsonAsync<PagedResult<OrganizationHistoryResponse>>();
        Assert.NotNull(hist);

        OrganizationHistoryResponse activateLog = Assert.Single(hist.Items, h => h.Action == "Organization_Activate");
        Assert.Equal(OrganizationStatus.Active, activateLog.Status);

        OrganizationHistoryResponse suspendLog = Assert.Single(hist.Items, h => h.Action == "Organization_Suspend");
        Assert.Equal(OrganizationStatus.Suspended, suspendLog.Status);
        Assert.Equal("Vi phạm lần 1", suspendLog.Reason);

        // Even though org is currently Active, the suspendLog status is STILL Suspended (immutable snapshot)
        Assert.Equal(OrganizationStatus.Suspended, suspendLog.Status);
    }

}
