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

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test method naming convention")]
public sealed partial class AdminOrganizationsEndpointsIntegrationTests
{
    private static readonly Guid AdminRoleId = new("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid CustomerRoleId = new("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid OwnerRoleId = new("a0000000-0000-0000-0000-000000000003");

    // =========================================================================
    // 1. GET /api/v1/auth/admin/organizations (List)
    // =========================================================================

    [Fact]
    public async Task ListOrganizations_UnauthorizedAndForbidden()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        // 1. Unauthenticated -> 401
        using HttpClient anonClient = factory.CreateClient();
        using HttpResponseMessage unauthRes = await anonClient.GetAsync("/api/v1/auth/admin/organizations");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthRes.StatusCode);

        // 2. Authenticated user without Organizations.Read permission in JWT -> 403
        Guid normalUserId = Guid.NewGuid();
        await SeedUserAsync(factory, normalUserId, "normal@example.com", "Normal User", CustomerRoleId);
        using HttpClient normalClient = CreateClientWithToken(factory, normalUserId, ["Customer"], []);
        using HttpResponseMessage forbidRes = await normalClient.GetAsync("/api/v1/auth/admin/organizations");
        Assert.Equal(HttpStatusCode.Forbidden, forbidRes.StatusCode);

        // 3. Owner with Organizations.Read in JWT but NO global role in DB -> 403 (service checks DB global perms)
        Guid ownerUserId = Guid.NewGuid();
        await SeedUserAsync(factory, ownerUserId, "owner@example.com", "Owner User", CustomerRoleId);
        using HttpClient ownerClient = CreateClientWithToken(factory, ownerUserId, ["Customer"], [Permissions.Organizations.Read]);
        using HttpResponseMessage ownerForbidRes = await ownerClient.GetAsync("/api/v1/auth/admin/organizations");
        Assert.Equal(HttpStatusCode.Forbidden, ownerForbidRes.StatusCode);
    }

    [Fact]
    public async Task ListOrganizations_PagingSearchStatusFilterAndQueueOrdering()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);

        Guid orgPending1 = await SeedOrganizationAsync(factory, "Alpha Rental", "alpha-rent", OrganizationStatus.Pending, DateTime.UtcNow.AddDays(-5));
        Guid orgPending2 = await SeedOrganizationAsync(factory, "Beta Transport", "beta-trans", OrganizationStatus.Pending, DateTime.UtcNow.AddDays(-2));
        Guid orgActive = await SeedOrganizationAsync(factory, "Gamma Logistics", "gamma-log", OrganizationStatus.Active, DateTime.UtcNow.AddDays(-10));
        Guid orgSuspended = await SeedOrganizationAsync(factory, "Delta Cars", "delta-cars", OrganizationStatus.Suspended, DateTime.UtcNow.AddDays(-1));

        using HttpClient adminClient = CreateClientWithToken(factory, adminId, ["Admin"], [Permissions.Organizations.Read]);

        // 1. Filter status=Pending -> only pending orgs, oldest first
        using HttpResponseMessage pendingRes = await adminClient.GetAsync("/api/v1/auth/admin/organizations?status=Pending");
        Assert.Equal(HttpStatusCode.OK, pendingRes.StatusCode);
        PagedResult<OrganizationSummaryResponse>? pendingPaged = await pendingRes.Content.ReadFromJsonAsync<PagedResult<OrganizationSummaryResponse>>();
        Assert.NotNull(pendingPaged);
        Assert.Equal(2, pendingPaged.TotalCount);
        Assert.Equal(orgPending1, pendingPaged.Items[0].Id); // oldest first
        Assert.Equal(orgPending2, pendingPaged.Items[1].Id);

        // 2. Search by name
        using HttpResponseMessage searchRes = await adminClient.GetAsync("/api/v1/auth/admin/organizations?search=Gamma");
        Assert.Equal(HttpStatusCode.OK, searchRes.StatusCode);
        PagedResult<OrganizationSummaryResponse>? searchPaged = await searchRes.Content.ReadFromJsonAsync<PagedResult<OrganizationSummaryResponse>>();
        Assert.NotNull(searchPaged);
        Assert.Single(searchPaged.Items, o => o.Id == orgActive);

        // 3. Search by slug
        using HttpResponseMessage searchSlugRes = await adminClient.GetAsync("/api/v1/auth/admin/organizations?search=delta-cars");
        Assert.Equal(HttpStatusCode.OK, searchSlugRes.StatusCode);
        PagedResult<OrganizationSummaryResponse>? searchSlugPaged = await searchSlugRes.Content.ReadFromJsonAsync<PagedResult<OrganizationSummaryResponse>>();
        Assert.NotNull(searchSlugPaged);
        Assert.Single(searchSlugPaged.Items, o => o.Id == orgSuspended);

        // 4. Invalid page -> 400
        using HttpResponseMessage badPageRes = await adminClient.GetAsync("/api/v1/auth/admin/organizations?page=0");
        Assert.Equal(HttpStatusCode.BadRequest, badPageRes.StatusCode);

        // 5. Invalid pageSize -> 400
        using HttpResponseMessage badPageSizeRes = await adminClient.GetAsync("/api/v1/auth/admin/organizations?pageSize=101");
        Assert.Equal(HttpStatusCode.BadRequest, badPageSizeRes.StatusCode);
    }

    // =========================================================================
    // 2. GET /api/v1/auth/admin/organizations/{id} (Detail)
    // =========================================================================

    [Fact]
    public async Task GetById_SuccessNotFoundAndForbidden()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);

        Guid ownerId = Guid.NewGuid();
        await SeedUserAsync(factory, ownerId, "owner@example.com", "Owner", CustomerRoleId);

        Guid orgId = await SeedOrganizationAsync(factory, "Acme Corp", "acme-corp", OrganizationStatus.Active, DateTime.UtcNow, ownerId);

        using HttpClient adminClient = CreateClientWithToken(factory, adminId, ["Admin"], [Permissions.Organizations.Read]);

        // 1. Success 200
        using HttpResponseMessage okRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.OK, okRes.StatusCode);
        OrganizationResponse? org = await okRes.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(org);
        Assert.Equal(orgId, org.Id);
        Assert.Equal("Acme Corp", org.Name);

        // 2. NotFound 404
        using HttpResponseMessage notFoundRes = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, notFoundRes.StatusCode);

        // 3. Owner with permission in JWT but no global DB role -> 403
        using HttpClient ownerClient = CreateClientWithToken(factory, ownerId, ["Customer"], [Permissions.Organizations.Read]);
        using HttpResponseMessage forbidRes = await ownerClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidRes.StatusCode);
    }

    [Fact]
    public async Task GetById_MediaServiceError_ReturnsServiceUnavailable500()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(mediaFail: true);
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);

        Guid orgId = await SeedOrganizationAsync(factory, "Media Test Corp", "media-test", OrganizationStatus.Active, DateTime.UtcNow,
            licenseMediaId: Guid.NewGuid());

        using HttpClient adminClient = CreateClientWithToken(factory, adminId, ["Admin"], [Permissions.Organizations.Read]);

        using HttpResponseMessage res = await adminClient.GetAsync($"/api/v1/auth/admin/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
    }

    // =========================================================================
    // 3. PUT /api/v1/auth/admin/organizations/{id} (Rename)
    // =========================================================================

    [Fact]
    public async Task UpdateOrganizationProfile_SuccessValidationAndIdempotency()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);

        Guid orgId = await SeedOrganizationAsync(factory, "Original Name", "original-org", OrganizationStatus.Active);

        using HttpClient adminClient = CreateClientWithToken(factory, adminId, ["Admin"], [Permissions.Organizations.Update]);

        UpdateOrganizationRequest validRequest = new(
            "Renamed Name",
            "contact@renamed.com",
            "0901234567",
            "123 Street",
            "Hà Nội",
            "Phường Bến Nghé",
            "https://renamed.com",
            null,
            "Updated description");

        // 1. Update profile -> 204
        using HttpResponseMessage updateRes = await adminClient.PutAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{orgId}",
            validRequest);
        Assert.Equal(HttpStatusCode.NoContent, updateRes.StatusCode);

        // Verify data changed and audit log written
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization updated = await db.Organizations.SingleAsync(o => o.Id == orgId);
            Assert.Equal("Renamed Name", updated.Name);
            Assert.Equal("contact@renamed.com", updated.ContactEmail);
            Assert.Equal("0901234567", updated.ContactPhone);
            Assert.Equal("123 Street", updated.Address);
            Assert.Equal("Hà Nội", updated.Province);
            Assert.Equal("Phường Bến Nghé", updated.Ward);
            Assert.Equal("https://renamed.com", updated.Website);
            Assert.Equal("Updated description", updated.Description);

            AuditLog audit = Assert.Single(db.AuditLogs.Where(l => l.Target == orgId.ToString() && l.Action == "Organization_ProfileUpdate"));
            Assert.Equal(adminId, audit.ActorId);
            Assert.Equal("Success", audit.Result);
            Assert.Null(audit.Reason);
        }

        // 2. Idempotency: send same data again -> 204, no new audit log!
        using HttpResponseMessage sameDataRes = await adminClient.PutAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{orgId}",
            validRequest);
        Assert.Equal(HttpStatusCode.NoContent, sameDataRes.StatusCode);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Assert.Single(db.AuditLogs.Where(l => l.Target == orgId.ToString() && l.Action == "Organization_ProfileUpdate"));
        }

        // 3. Update works for organization in ANY status (e.g. Suspended)
        Guid suspendedOrgId = await SeedOrganizationAsync(factory, "Suspended Corp", "suspended-corp", OrganizationStatus.Suspended);
        using HttpResponseMessage updateSuspendedRes = await adminClient.PutAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{suspendedOrgId}",
            new UpdateOrganizationRequest("Suspended Corp Renamed", "contact@susp.com", "0901234567", "123 Street", "Hà Nội", "Phường Bến Nghé"));
        Assert.Equal(HttpStatusCode.NoContent, updateSuspendedRes.StatusCode);

        // 4. Invalid empty name -> 400
        using HttpResponseMessage emptyNameRes = await adminClient.PutAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{orgId}",
            new { name = "", contactEmail = "c@e.com", contactPhone = "0901234567", address = "A", province = "P", ward = "W" });
        Assert.Equal(HttpStatusCode.BadRequest, emptyNameRes.StatusCode);

        // 5. Invalid whitespace name -> 400
        using HttpResponseMessage wsNameRes = await adminClient.PutAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{orgId}",
            new { name = "   ", contactEmail = "c@e.com", contactPhone = "0901234567", address = "A", province = "P", ward = "W" });
        Assert.Equal(HttpStatusCode.BadRequest, wsNameRes.StatusCode);

        // 6. Invalid name > 150 chars -> 400
        using HttpResponseMessage tooLongRes = await adminClient.PutAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{orgId}",
            new { name = new string('x', 151), contactEmail = "c@e.com", contactPhone = "0901234567", address = "A", province = "P", ward = "W" });
        Assert.Equal(HttpStatusCode.BadRequest, tooLongRes.StatusCode);

        // 7. NotFound -> 404
        using HttpResponseMessage notFoundRes = await adminClient.PutAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{Guid.NewGuid()}",
            new UpdateOrganizationRequest("Valid Name", "c@e.com", "0901234567", "A", "P", "W"));
        Assert.Equal(HttpStatusCode.NotFound, notFoundRes.StatusCode);

        // 8. Owner with permission in JWT but no global role -> 403
        Guid ownerId = Guid.NewGuid();
        await SeedUserAsync(factory, ownerId, "owner@example.com", "Owner", CustomerRoleId);
        using HttpClient ownerClient = CreateClientWithToken(factory, ownerId, ["Customer"], [Permissions.Organizations.Update]);
        using HttpResponseMessage forbidRes = await ownerClient.PutAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{orgId}",
            new UpdateOrganizationRequest("Owner Attempt", "c@e.com", "0901234567", "A", "P", "W"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidRes.StatusCode);
    }

    // =========================================================================
    // 4. POST /api/v1/auth/admin/organizations/{id}/suspend (Suspend)
    // =========================================================================

    [Fact]
    public async Task SuspendOrganization_TransitionsActiveToSuspendedAndEnforcesRules()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);

        Guid activeOrgId = await SeedOrganizationAsync(factory, "Active Org", "active-org", OrganizationStatus.Active);
        Guid pendingOrgId = await SeedOrganizationAsync(factory, "Pending Org", "pending-org", OrganizationStatus.Pending);
        Guid rejectedOrgId = await SeedOrganizationAsync(factory, "Rejected Org", "rejected-org", OrganizationStatus.Rejected);

        using HttpClient adminClient = CreateClientWithToken(factory, adminId, ["Admin"], [Permissions.Organizations.Suspend]);

        // 1. Active -> Suspended: 204 NoContent with reason
        using HttpResponseMessage suspendRes = await adminClient.PostAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{activeOrgId}/suspend",
            new SuspendOrganizationRequest("Vi phạm hợp đồng thuê"));
        Assert.Equal(HttpStatusCode.NoContent, suspendRes.StatusCode);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization org = await db.Organizations.SingleAsync(o => o.Id == activeOrgId);
            Assert.Equal(OrganizationStatus.Suspended, org.Status);
            Assert.Equal("Vi phạm hợp đồng thuê", org.SuspensionReason);

            AuditLog log = Assert.Single(db.AuditLogs.Where(l => l.Target == activeOrgId.ToString() && l.Action == "Organization_Suspend"));
            Assert.Equal(adminId, log.ActorId);
            Assert.Equal("Success", log.Result);
            Assert.Equal("Vi phạm hợp đồng thuê", log.Reason);
        }

        // 2. Calling suspend when ALREADY Suspended -> 204 NoContent, NO duplicate audit log, NO reason change
        using HttpResponseMessage repeatRes = await adminClient.PostAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{activeOrgId}/suspend",
            new SuspendOrganizationRequest("Lý do mới không được đổi"));
        Assert.Equal(HttpStatusCode.NoContent, repeatRes.StatusCode);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization org = await db.Organizations.SingleAsync(o => o.Id == activeOrgId);
            Assert.Equal("Vi phạm hợp đồng thuê", org.SuspensionReason);
            Assert.Single(db.AuditLogs.Where(l => l.Target == activeOrgId.ToString() && l.Action == "Organization_Suspend"));
        }

        // 3. Suspending Pending org -> 409 Conflict
        using HttpResponseMessage pendingRes = await adminClient.PostAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{pendingOrgId}/suspend",
            new SuspendOrganizationRequest("Reason"));
        Assert.Equal(HttpStatusCode.Conflict, pendingRes.StatusCode);

        // 4. Suspending Rejected org -> 409 Conflict
        using HttpResponseMessage rejectedRes = await adminClient.PostAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{rejectedOrgId}/suspend",
            new SuspendOrganizationRequest("Reason"));
        Assert.Equal(HttpStatusCode.Conflict, rejectedRes.StatusCode);

        // 5. NotFound -> 404
        using HttpResponseMessage notFoundRes = await adminClient.PostAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{Guid.NewGuid()}/suspend",
            new SuspendOrganizationRequest("Reason"));
        Assert.Equal(HttpStatusCode.NotFound, notFoundRes.StatusCode);

        // 6. Invalid reason (empty/whitespace/too long) -> 400 BadRequest
        using HttpResponseMessage emptyReasonRes = await adminClient.PostAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{activeOrgId}/suspend",
            new { reason = "" });
        Assert.Equal(HttpStatusCode.BadRequest, emptyReasonRes.StatusCode);

        using HttpResponseMessage wsReasonRes = await adminClient.PostAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{activeOrgId}/suspend",
            new { reason = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, wsReasonRes.StatusCode);

        using HttpResponseMessage tooLongReasonRes = await adminClient.PostAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{activeOrgId}/suspend",
            new { reason = new string('r', 501) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLongReasonRes.StatusCode);
    }
    // =========================================================================
    // 5. POST /api/v1/auth/admin/organizations/{id}/activate (Activate)
    // =========================================================================

    [Fact]
    public async Task ActivateOrganization_TransitionsSuspendedToActiveAndRejectsPendingRejected()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);

        Guid suspendedOrgId = await SeedOrganizationAsync(factory, "Suspended Org", "suspended-org", OrganizationStatus.Suspended);
        Guid pendingOrgId = await SeedOrganizationAsync(factory, "Pending Org", "pending-org", OrganizationStatus.Pending);
        Guid rejectedOrgId = await SeedOrganizationAsync(factory, "Rejected Org", "rejected-org", OrganizationStatus.Rejected);

        using HttpClient adminClient = CreateClientWithToken(factory, adminId, ["Admin"], [Permissions.Organizations.Suspend]);

        // 1. Suspended -> Active: 204 NoContent
        using HttpResponseMessage activateRes = await adminClient.PostAsync($"/api/v1/auth/admin/organizations/{suspendedOrgId}/activate", null);
        Assert.Equal(HttpStatusCode.NoContent, activateRes.StatusCode);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Organization org = await db.Organizations.SingleAsync(o => o.Id == suspendedOrgId);
            Assert.Equal(OrganizationStatus.Active, org.Status);
            Assert.Null(org.SuspensionReason);
            AuditLog log = Assert.Single(db.AuditLogs.Where(l => l.Target == suspendedOrgId.ToString() && l.Action == "Organization_Activate"));
            Assert.Equal(adminId, log.ActorId);
            Assert.Equal("Success", log.Result);
        }

        // 2. Calling activate when ALREADY Active -> 204 NoContent, NO duplicate audit log
        using HttpResponseMessage repeatRes = await adminClient.PostAsync($"/api/v1/auth/admin/organizations/{suspendedOrgId}/activate", null);
        Assert.Equal(HttpStatusCode.NoContent, repeatRes.StatusCode);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Assert.Single(db.AuditLogs.Where(l => l.Target == suspendedOrgId.ToString() && l.Action == "Organization_Activate"));
        }

        // 3. Activating Pending org -> 409 Conflict (cannot use activate instead of approve)
        using HttpResponseMessage pendingRes = await adminClient.PostAsync($"/api/v1/auth/admin/organizations/{pendingOrgId}/activate", null);
        Assert.Equal(HttpStatusCode.Conflict, pendingRes.StatusCode);

        // 4. Activating Rejected org -> 409 Conflict
        using HttpResponseMessage rejectedRes = await adminClient.PostAsync($"/api/v1/auth/admin/organizations/{rejectedOrgId}/activate", null);
        Assert.Equal(HttpStatusCode.Conflict, rejectedRes.StatusCode);

        // 5. NotFound -> 404
        using HttpResponseMessage notFoundRes = await adminClient.PostAsync($"/api/v1/auth/admin/organizations/{Guid.NewGuid()}/activate", null);
        Assert.Equal(HttpStatusCode.NotFound, notFoundRes.StatusCode);
    }

    // =========================================================================
    // 6. PATCH /api/v1/auth/admin/organizations/{id}/approve & reject
    // =========================================================================

    [Fact]
    public async Task ApproveAndReject_SuccessAndAntiSelfReview()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        Guid otherAdminId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);
        await SeedUserAsync(factory, otherAdminId, "otheradmin@example.com", "Other Admin", AdminRoleId);

        // Admin owns orgPending1
        Guid orgPending1 = await SeedOrganizationAsync(factory, "Admin Org", "admin-org", OrganizationStatus.Pending, DateTime.UtcNow, adminId);
        Guid orgPending2 = await SeedOrganizationAsync(factory, "Other Org", "other-org", OrganizationStatus.Pending, DateTime.UtcNow);

        using HttpClient adminClient = CreateClientWithToken(factory, adminId, ["Admin"], [Permissions.Organizations.Suspend]);
        using HttpClient otherAdminClient = CreateClientWithToken(factory, otherAdminId, ["Admin"], [Permissions.Organizations.Suspend]);

        // 1. Anti-self-review: Admin cannot approve own org -> 403
        using HttpResponseMessage selfApproveRes = await adminClient.PatchAsync($"/api/v1/auth/admin/organizations/{orgPending1}/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, selfApproveRes.StatusCode);

        // 2. Anti-self-review: Admin cannot reject own org -> 403
        using HttpResponseMessage selfRejectRes = await adminClient.PatchAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{orgPending1}/reject",
            new RejectOrganizationRequest("Self reject"));
        Assert.Equal(HttpStatusCode.Forbidden, selfRejectRes.StatusCode);

        // 3. Other admin approves orgPending1 -> 200 OK
        using HttpResponseMessage otherApproveRes = await otherAdminClient.PatchAsync($"/api/v1/auth/admin/organizations/{orgPending1}/approve", null);
        Assert.Equal(HttpStatusCode.OK, otherApproveRes.StatusCode);
        OrganizationResponse? approved = await otherApproveRes.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(approved);
        Assert.Equal(OrganizationStatus.Active, approved.Status);

        // 4. Approving already Active org -> 409 Conflict
        using HttpResponseMessage repeatApproveRes = await otherAdminClient.PatchAsync($"/api/v1/auth/admin/organizations/{orgPending1}/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, repeatApproveRes.StatusCode);

        // 5. Reject orgPending2 with empty reason -> 400 BadRequest
        using HttpResponseMessage badRejectRes = await adminClient.PatchAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{orgPending2}/reject",
            new RejectOrganizationRequest(""));
        Assert.Equal(HttpStatusCode.BadRequest, badRejectRes.StatusCode);

        // 6. Admin rejects orgPending2 -> 200 OK
        using HttpResponseMessage rejectRes = await adminClient.PatchAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{orgPending2}/reject",
            new RejectOrganizationRequest("Giấy phép kinh doanh không hợp lệ"));
        Assert.Equal(HttpStatusCode.OK, rejectRes.StatusCode);
        OrganizationResponse? rejected = await rejectRes.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(rejected);
        Assert.Equal(OrganizationStatus.Rejected, rejected.Status);
        Assert.Equal("Giấy phép kinh doanh không hợp lệ", rejected.RejectionReason);

        // 7. Rejecting already Rejected org -> 409 Conflict
        using HttpResponseMessage repeatRejectRes = await adminClient.PatchAsJsonAsync(
            $"/api/v1/auth/admin/organizations/{orgPending2}/reject",
            new RejectOrganizationRequest("Again"));
        Assert.Equal(HttpStatusCode.Conflict, repeatRejectRes.StatusCode);
    }

    // =========================================================================
    // 7. Old Routes Removed (Cutover verification)
    // =========================================================================

    [Fact]
    public async Task OldAdminRoutesAreRemovedAndOwnerRouteRestricted()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        Guid adminId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        await SeedUserAsync(factory, adminId, "admin@example.com", "Admin", AdminRoleId);
        await SeedUserAsync(factory, ownerId, "owner@example.com", "Owner", CustomerRoleId);

        Guid orgId = await SeedOrganizationAsync(factory, "Cutover Org", "cutover-org", OrganizationStatus.Active, DateTime.UtcNow, ownerId);

        using HttpClient adminClient = CreateClientWithToken(factory, adminId, ["Admin"], [
            Permissions.Organizations.Read,
            Permissions.Organizations.Update,
            Permissions.Organizations.Suspend
        ]);
        using HttpClient ownerClient = CreateClientWithToken(factory, ownerId, ["Customer"], []);

        // 1. GET /organizations -> 404/405 (removed from OrganizationsController)
        using HttpResponseMessage oldListRes = await adminClient.GetAsync("/api/v1/auth/organizations");
        Assert.True(oldListRes.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);

        // 2. PATCH /organizations/{id}/approve -> 404/405
        using HttpResponseMessage oldApproveRes = await adminClient.PatchAsync($"/api/v1/auth/organizations/{orgId}/approve", null);
        Assert.True(oldApproveRes.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);

        // 3. PATCH /organizations/{id}/reject -> 404/405
        using HttpResponseMessage oldRejectRes = await adminClient.PatchAsJsonAsync($"/api/v1/auth/organizations/{orgId}/reject", new { reason = "abc" });
        Assert.True(oldRejectRes.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);

        // 4. PATCH /organizations/{id} -> 404/405
        using HttpResponseMessage oldPatchRes = await adminClient.PatchAsJsonAsync($"/api/v1/auth/organizations/{orgId}", new { name = "New Name" });
        Assert.True(oldPatchRes.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);

        // 5. GET /organizations/{id}: Owner gets 200 OK
        using HttpResponseMessage ownerGetRes = await ownerClient.GetAsync($"/api/v1/auth/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.OK, ownerGetRes.StatusCode);

        // 6. GET /organizations/{id}: Admin (not owner) gets 403 Forbidden
        using HttpResponseMessage adminGetOldRes = await adminClient.GetAsync($"/api/v1/auth/organizations/{orgId}");
        Assert.Equal(HttpStatusCode.Forbidden, adminGetOldRes.StatusCode);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static HttpClient CreateClientWithToken(
        WebApplicationFactory<Program> factory,
        Guid userId,
        IReadOnlyCollection<string> roles,
        IReadOnlyCollection<string> permissions)
    {
        HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        Guid sessionId = Guid.NewGuid();
        db.Sessions.Add(new Session
        {
            Id = sessionId,
            UserId = userId,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
        });
        db.SaveChanges();

        IAccessTokenIssuer issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        IssuedAccessToken token = issuer.Issue(new AccessTokenRequest(
            userId,
            Roles: roles,
            Permissions: permissions,
            SessionId: sessionId));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return client;
    }

    private static async Task SeedUserAsync(
        WebApplicationFactory<Program> factory,
        Guid userId,
        string email,
        string fullName,
        Guid roleId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        User user = new()
        {
            Id = userId,
            Email = email,
            FullName = fullName,
            CreatedAt = DateTime.UtcNow,
            IsActive = true,
            HasPassword = true,
            ProfileCompletedAtUtc = DateTime.UtcNow
        };
        user.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });

        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedOrganizationAsync(
        WebApplicationFactory<Program> factory,
        string name,
        string slug,
        OrganizationStatus status,
        DateTime? createdAt = null,
        Guid? ownerUserId = null,
        Guid? licenseMediaId = null,
        OrganizationType type = OrganizationType.Supplier,
        string? province = null,
        DateTime? lastRemindedAt = null,
        DateTime? resubmittedAt = null,
        Guid? logoMediaId = null,
        string? suspensionReason = null)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        Guid orgId = Guid.NewGuid();
        Organization org = new()
        {
            Id = orgId,
            Name = name,
            Slug = slug,
            Status = status,
            Type = type,
            Province = province,
            TaxCode = $"TX_{Guid.NewGuid():N}"[..14],
            CreatedAtUtc = createdAt ?? DateTime.UtcNow,
            ResubmittedAtUtc = resubmittedAt,
            LastRemindedAtUtc = lastRemindedAt,
            BusinessLicenseDocumentMediaId = licenseMediaId,
            LogoMediaId = logoMediaId,
            SuspensionReason = suspensionReason
        };
        db.Organizations.Add(org);

        if (ownerUserId.HasValue)
        {
            db.OrganizationMemberships.Add(new OrganizationMembership
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                UserId = ownerUserId.Value,
                RoleId = OwnerRoleId,
                Status = MembershipStatus.Active,
                JoinedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
        return orgId;
    }

    private static async Task SeedDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        Role adminRole = new() { Id = AdminRoleId, Name = "Admin", Scope = RoleScope.Global };
        Role customerRole = new() { Id = CustomerRoleId, Name = "Customer", Scope = RoleScope.Global };
        Role ownerRole = new() { Id = OwnerRoleId, Name = "Owner", Scope = RoleScope.Organization };

        Permission pRead = new() { Id = Guid.NewGuid(), Code = Permissions.Organizations.Read };
        Permission pUpdate = new() { Id = Guid.NewGuid(), Code = Permissions.Organizations.Update };
        Permission pSuspend = new() { Id = Guid.NewGuid(), Code = Permissions.Organizations.Suspend };

        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pRead });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pUpdate });
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pSuspend });

        db.Permissions.AddRange(pRead, pUpdate, pSuspend);
        db.Roles.AddRange(adminRole, customerRole, ownerRole);
        await db.SaveChangesAsync();
    }

    private static WebApplicationFactory<Program> CreateFactory(bool mediaFail = false, FakeMediaServiceClient? fakeMediaClient = null)
    {
        string inMemoryDatabaseName = Guid.NewGuid().ToString("N");
        using RSA rsa = RSA.Create(2048);
        string publicKey = rsa.ExportRSAPublicKeyPem();
        string privateKey = rsa.ExportRSAPrivateKeyPem();

        return new AuthWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MediaService:ApiKey", "test-media-key");
            builder.UseSetting("MediaService:PublicGatewayUrl", "http://gateway.example.test");
            builder.UseSetting(WebHostDefaults.EnvironmentKey, Environments.Development);
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:Jwt:Issuer"] = "https://auth.example.test",
                    ["Auth:Jwt:Audience"] = "dprental-clients",
                    ["Auth:Jwt:AccessTokenLifetimeMinutes"] = "15",
                    ["Auth:Jwt:ActiveKeyId"] = "current",
                    ["Auth:Jwt:Keys:0:KeyId"] = "current",
                    ["Auth:Jwt:Keys:0:PublicKeyPem"] = publicKey,
                    ["Auth:Jwt:Keys:0:PrivateKeyPem"] = privateKey,
                    ["Auth:AdminBootstrap:CreationKey"] = "test-bootstrap-creation-key"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AuthDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AuthDbContext>>();
                services.AddDbContext<AuthDbContext>(options => options.UseInMemoryDatabase(inMemoryDatabaseName));

                services.RemoveAll<IMediaServiceClient>();
                services.AddSingleton<IMediaServiceClient>(fakeMediaClient ?? new FakeMediaServiceClient { AutoVerify = true });

                if (mediaFail)
                {
                    services.RemoveAll<IMediaUrlResolver>();
                    services.AddSingleton<IMediaUrlResolver>(new FailingMediaUrlResolver());
                }
            });
        });
    }
    private sealed class FailingMediaUrlResolver : IMediaUrlResolver
    {
        public string? ResolvePublicUrl(Guid? mediaId) => null;
        public Task<Result<MediaReadUrlDto>> ResolvePrivateAccessUrlAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure<MediaReadUrlDto>(AuthErrors.Media.ServiceUnavailable));
    }
}
