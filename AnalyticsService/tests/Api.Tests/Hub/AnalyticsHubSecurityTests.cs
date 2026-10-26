using System.Net;
using System.Security.Claims;
using Dprental.AnalyticsService.Api.Tests.Projections;
using Dprental.AnalyticsService.Application.Kafka.Contracts.Rental;
using Dprental.AnalyticsService.Application.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Hub;

public sealed class AnalyticsHubSecurityTests
{
    private const string ClientAudience = "dprental-clients";
    private static readonly Guid UserId = Guid.Parse("30000000-0000-0000-0000-00000000000a");

    [Fact]
    public void RealUserWithClientAudience_IsHubClient()
    {
        Assert.True(User().IsHubClient(ClientAudience));
    }

    [Fact]
    public void ServiceToken_IsNotHubClient_NorAdmin()
    {
        ClaimsPrincipal service = User(new Claim("u", "svc"), new Claim("r", "adm"));

        Assert.False(service.IsHubClient(ClientAudience));
        Assert.False(service.IsPlatformAdmin()); // token service không bao giờ được coi là admin
    }

    [Fact]
    public void WrongAudience_IsNotHubClient()
    {
        Assert.False(User(audience: "payment-api").IsHubClient(ClientAudience));
    }

    [Fact]
    public void MissingOrInvalidSub_IsNotHubClient()
    {
        ClaimsIdentity identity = new([new Claim("aud", ClientAudience), new Claim("sub", "not-a-guid")], "Bearer");

        Assert.False(new ClaimsPrincipal(identity).IsHubClient(ClientAudience));
    }

    [Theory]
    [InlineData("adm", true)]
    [InlineData("admin_platform", true)]
    [InlineData("supplier", false)]
    public void AdminRole_IsDetectedFromRoleClaim(string role, bool expected)
    {
        Assert.Equal(expected, User(new Claim("r", role)).IsPlatformAdmin());
    }

    [Fact]
    public async Task HubNegotiate_WithoutToken_IsUnauthorized()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting(
                "ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync("/hubs/analytics/negotiate?negotiateVersion=1", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ChangeSet_CollectsDistinctSuppliersTouchedByProjector()
    {
        using ProjectionHarness h = new();
        Guid supplierA = Guid.NewGuid();
        Guid supplierB = Guid.NewGuid();

        await h.ApplyOkAsync(() => h.Orders.ApplyAsync(new RentalOrderCreatedPayload
        {
            RentalOrderId = Guid.NewGuid(),
            OrderCode = "RNT-1",
            RenterId = Guid.NewGuid(),
            Currency = "VND",
            TotalRentalAmount = 3m,
            TotalDepositAmount = 0m,
            CreatedAt = DateTimeOffset.UtcNow,
            Items = [Item(supplierA), Item(supplierA), Item(supplierB)],
            OrderVersion = 1
        }));

        Assert.Equal(2, h.Changes.SupplierIds.Count);
        Assert.Contains(supplierA, h.Changes.SupplierIds);
        Assert.Contains(supplierB, h.Changes.SupplierIds);
    }

    private static RentalOrderCreatedItemPayload Item(Guid supplierId) => new()
    {
        RentalItemId = Guid.NewGuid(),
        EquipmentListingId = Guid.NewGuid(),
        SupplierId = supplierId,
        Quantity = 1,
        ExpectedStartDate = new DateOnly(2026, 11, 12),
        ExpectedEndDate = new DateOnly(2026, 11, 13),
        RentalAmount = 1m,
        DepositAmount = 0m
    };

    private static ClaimsPrincipal User(params Claim[] extra) => User(ClientAudience, extra);

    private static ClaimsPrincipal User(string audience, params Claim[] extra)
    {
        ClaimsIdentity identity = new(
            [new Claim("aud", audience), new Claim("sub", UserId.ToString()), .. extra],
            authenticationType: "Bearer");
        return new ClaimsPrincipal(identity);
    }
}
