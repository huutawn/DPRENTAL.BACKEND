using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Common;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class AssetHttpIntegrationTests
{
    private sealed class AuthMembershipHandler(Guid organizationId, string expectedToken) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/v1/auth/users/organizations", request.RequestUri!.AbsolutePath);
            Assert.Equal(expectedToken, request.Headers.Authorization?.Parameter);
            Assert.Equal("test-internal-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[] { new { organizationId, roleName = "Owner", type = 1, organizationStatus = 1, membershipStatus = 1 } })
            });
        }
    }

    private sealed class OrganizationMembershipStub : IOrganizationMembershipClient
    {
        public IReadOnlyList<OrganizationMembershipSnapshot> Memberships { get; set; } = [];
        public bool IsUnavailable { get; set; }
        public int Calls { get; private set; }
        public Task<Result<IReadOnlyList<OrganizationMembershipSnapshot>>> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            if (IsUnavailable)
                return Task.FromResult<Result<IReadOnlyList<OrganizationMembershipSnapshot>>>(Error.Unavailable("AUTH_SERVICE_UNAVAILABLE", "Auth unavailable"));
            return Task.FromResult(Result.Success(Memberships));
        }
    }

    [Fact]
    public async Task ConfirmOrganizationRequiresOwnerAndVerifiedSupplierAndUpdatesPublicListing()
    {
        var stub = new OrganizationMembershipStub();
        var (baseApp, security) = CreateFactory();
        await using var factory = baseApp;
        await using var securityFactory = security;
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOrganizationMembershipClient>();
            services.AddSingleton<IOrganizationMembershipClient>(stub);
        }));
        using var anonymous = app.CreateClient();
        using var ownerClient = app.CreateClient();
        using var outsider = app.CreateClient();
        Guid ownerId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, model: "Organization Confirmation", serial: Guid.NewGuid().ToString("N"));
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", security.CreateHumanToken(ownerId));
        outsider.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", security.CreateHumanToken(Guid.NewGuid()));
        string url = $"/api/v1/inventory/equipment-listings/{asset.EquipmentListingId:D}/organization";
        var body = new ConfirmListingOrganizationRequest(organizationId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(url, body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PutAsJsonAsync(url, body)).StatusCode);
        Assert.Equal(0, stub.Calls);
        stub.IsUnavailable = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await ownerClient.PutAsJsonAsync(url, body)).StatusCode);
        await using (var db = CreateDbContext())
        {
            var unchanged = await db.EquipmentListings.AsNoTracking().SingleAsync(x => x.Id == asset.EquipmentListingId);
            Assert.Null(unchanged.OrganizationId);
        }
        stub.IsUnavailable = false;
        stub.Memberships = [new(organizationId, "Member", 1, 1, 1)];
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.PutAsJsonAsync(url, body)).StatusCode);
        stub.Memberships = [new(organizationId, "Owner", 2, 1, 1)];
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.PutAsJsonAsync(url, body)).StatusCode);
        stub.Memberships = [new(organizationId, "Owner", 1, 0, 1)];
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.PutAsJsonAsync(url, body)).StatusCode);
        stub.Memberships = [new(organizationId, "Owner", 1, 1, 1)];
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync(url, body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync(url, body)).StatusCode);
        string listingKey = asset.EquipmentListingId.ToString("D");
        await using (var db = CreateDbContext())
        {
            Assert.Equal(1, await db.OutboxMessages.AsNoTracking().CountAsync(message =>
                message.KafkaKey == listingKey && message.Topic == KafkaTopics.InventoryProjectionEventsV1));
        }
        var publicListing = await anonymous.GetFromJsonAsync<PublicEquipmentListingResponse>(
            $"/api/v1/inventory/public/equipment-listings/{asset.EquipmentListingId:D}");
        Assert.NotNull(publicListing);
        Assert.Equal(organizationId, publicListing.OrganizationId);
        Guid otherOrganizationId = Guid.NewGuid();
        stub.Memberships = [new(otherOrganizationId, "Owner", 1, 1, 1)];
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PutAsJsonAsync(url,
            new ConfirmListingOrganizationRequest(otherOrganizationId))).StatusCode);
    }

    [Fact]
    public async Task ConfirmOrganizationForwardsTheAuthenticatedOwnerTokenToAuth()
    {
        var (baseApp, security) = CreateFactory();
        await using var factory = baseApp;
        await using var securityFactory = security;
        Guid ownerId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        string token = security.CreateHumanToken(ownerId);
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOrganizationMembershipClient>();
            services.AddScoped<IOrganizationMembershipClient>(sp =>
            {
                var httpClient = new HttpClient(new AuthMembershipHandler(organizationId, token)) { BaseAddress = new Uri("http://auth.test/") };
                httpClient.DefaultRequestHeaders.Add("X-Api-Key", "test-internal-key");
                return new OrganizationMembershipClient(httpClient, sp.GetRequiredService<IHttpContextAccessor>());
            });
        }));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var asset = await SeedAssetAsync(ownerId, model: "Organization Auth", serial: Guid.NewGuid().ToString("N"));
        var result = await client.PutAsJsonAsync($"/api/v1/inventory/equipment-listings/{asset.EquipmentListingId:D}/organization",
            new ConfirmListingOrganizationRequest(organizationId));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task ConcurrentConfirmationsDoNotOverwriteTheFirstOrganization()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        var stub = new OrganizationMembershipStub { Memberships = [new(first, "Owner", 1, 1, 1), new(second, "Owner", 1, 1, 1)] };
        var (baseApp, security) = CreateFactory();
        await using var factory = baseApp;
        await using var securityFactory = security;
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOrganizationMembershipClient>();
            services.AddSingleton<IOrganizationMembershipClient>(stub);
        }));
        using var client = app.CreateClient();
        Guid ownerId = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", security.CreateHumanToken(ownerId));
        var asset = await SeedAssetAsync(ownerId, model: "Organization Race", serial: Guid.NewGuid().ToString("N"));
        string url = $"/api/v1/inventory/equipment-listings/{asset.EquipmentListingId:D}/organization";
        var responses = await Task.WhenAll(
            client.PutAsJsonAsync(url, new ConfirmListingOrganizationRequest(first)),
            client.PutAsJsonAsync(url, new ConfirmListingOrganizationRequest(second)));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Guid winner = responses[0].StatusCode == HttpStatusCode.OK ? first : second;
        await using var db = CreateDbContext();
        var saved = await db.EquipmentListings.AsNoTracking().SingleAsync(x => x.Id == asset.EquipmentListingId);
        Assert.Equal(winner, saved.OrganizationId);
    }
}
