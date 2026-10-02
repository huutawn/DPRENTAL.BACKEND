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

}
