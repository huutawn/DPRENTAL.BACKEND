using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class InventoryAssetsControllerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    private sealed class StubCatalogClient : ICatalogServiceClient
    {
        public Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogEquipmentTypeResponse(
                equipmentTypeId, "EQ-FX3", "Sony FX3", null, 1, DateTime.UtcNow, null, null)));

        public Task<Result<CatalogBrandResponse>> GetBrandAsync(Guid brandId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogBrandResponse(
                brandId, "BR-SONY", "Sony", true)));
        public Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
        public Task<Result<CatalogBatchSchemasResponse>> GetEquipmentTypeSchemasBatchAsync(Guid equipmentTypeId, IReadOnlyList<int> versions, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(
                equipmentTypeId,
                1,
                [new CatalogBatchSchemaItemResponse(1, [])])));
    }

    private sealed class StubMediaClient : IMediaServiceClient
    {
        public Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new MediaVerificationInfo(mediaId, "Ready", Guid.NewGuid().ToString("D"))));

        public Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new MediaVisibilityInfo(mediaId, "Public")));
    }

    private static InventorySecurityTestFactory CreateFactory()
    {
        var factory = new InventorySecurityTestFactory();
        return factory;
    }

    [Theory]
    [InlineData("POST", "api/v1/inventory/equipment-listings")]
    [InlineData("GET", "api/v1/inventory/assets")]
    [InlineData("GET", "api/v1/inventory/assets/3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    [InlineData("PATCH", "api/v1/inventory/assets/3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    [InlineData("PATCH", "api/v1/inventory/assets/3fa85f64-5717-4562-b3fc-2c963f66afa6/accepting-bookings")]
    [InlineData("PUT", "api/v1/inventory/equipment-listings/3fa85f64-5717-4562-b3fc-2c963f66afa6/rental-rate")]
    public async Task MissingToken_AllEndpointsReturn401(string method, string url)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PATCH")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ServicePrincipal_CreateAsset_Returns403()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/inventory/equipment-listings");
        var validBody = $"{{\"equipmentTypeId\":\"{Guid.NewGuid():D}\",\"brandId\":\"{Guid.NewGuid():D}\",\"name\":\"Sony FX3\",\"model\":\"FX3\",\"serialNumber\":\"FX3-SN-001\",\"schemaVersion\":1,\"rentalRate\":{{\"dailyRate\":100000,\"currency\":\"VND\"}},\"specValues\":{{}}}}";
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        request.Content = new StringContent(validBody, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task HumanWithoutManagePermission_CreateAsset_Returns403()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var humanToken = factory.CreateHumanToken(Guid.NewGuid(), permissions: []);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/inventory/equipment-listings");
        var validBody = $"{{\"equipmentTypeId\":\"{Guid.NewGuid():D}\",\"brandId\":\"{Guid.NewGuid():D}\",\"name\":\"Sony FX3\",\"model\":\"FX3\",\"serialNumber\":\"FX3-SN-001\",\"schemaVersion\":1,\"rentalRate\":{{\"dailyRate\":100000,\"currency\":\"VND\"}},\"specValues\":{{}}}}";
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", humanToken);
        request.Content = new StringContent(validBody, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("?owner=123")]
    [InlineData("?ownerId=123")]
    [InlineData("?ownerUserId=123")]
    [InlineData("?org=123")]
    [InlineData("?organization=123")]
    [InlineData("?organizationId=123")]
    [InlineData("?extraParam=value")]
    [InlineData("?page=1&pageSize=20&filter=all")]
    public async Task ForbiddenOrUnknownQueryParameters_List_Returns400(string queryString)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = factory.CreateHumanToken(Guid.NewGuid(), permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/inventory/assets{queryString}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_QUERY_PARAMETER", body);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task InvalidPaging_List_Returns400(int page, int pageSize)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = factory.CreateHumanToken(Guid.NewGuid(), permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/inventory/assets?page={page}&pageSize={pageSize}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"ownerUserId\":\"3fa85f64-5717-4562-b3fc-2c963f66afa6\",\"model\":\"FX3\"}")]
    [InlineData("{\"organizationId\":\"3fa85f64-5717-4562-b3fc-2c963f66afa6\",\"model\":\"FX3\"}")]
    [InlineData("{\"unknownField\":\"bad\",\"model\":\"FX3\"}")]
    public async Task SpoofedOrUnknownBodyFields_Create_RejectedWith400(string jsonBody)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = factory.CreateHumanToken(Guid.NewGuid(), permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/inventory/equipment-listings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("{}")] // Empty PATCH
    [InlineData("{\"ownerUserId\":\"3fa85f64-5717-4562-b3fc-2c963f66afa6\"}")] // Forbidden owner
    [InlineData("{\"equipmentTypeId\":\"3fa85f64-5717-4562-b3fc-2c963f66afa6\"}")] // Forbidden equipmentType
    [InlineData("{\"acceptingBookings\":true}")] // Forbidden acceptingBookings in info patch
    [InlineData("{\"model\":null}")] // Null model
    [InlineData("{\"schemaVersion\":null}")] // Null schemaVersion
    [InlineData("{\"specValues\":null}")] // Null specValues
    [InlineData("{\"unknown\":\"foo\"}")] // Unknown field
    [InlineData("{\"model\":\"A\",\"Model\":\"B\"}")] // Duplicate field
    [InlineData("{\"model\":")] // Truncated JSON
    public async Task InvalidPatchRequests_RejectedWith400(string jsonBody)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = factory.CreateHumanToken(Guid.NewGuid(), permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"api/v1/inventory/assets/{Guid.NewGuid():D}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("{}")] // Missing flag
    [InlineData("{\"reason\":\"test\"}")] // Missing flag
    [InlineData("{\"acceptingBookings\":null}")] // Null flag
    [InlineData("{\"acceptingBookings\":\"true\"}")] // String instead of bool
    [InlineData("{\"acceptingBookings\":1}")] // Number instead of bool
    [InlineData("{\"acceptingBookings\":false,\"suspensionReason\":\"old\"}")] // Disallowed alias
    [InlineData("{\"acceptingBookings\":true,\"unexpectedField\":123}")] // Unknown field
    [InlineData("{\"acceptingBookings\":true,\"AcceptingBookings\":false}")] // Duplicate field
    public async Task ToggleAcceptingBookings_InvalidInputs_RejectedWith400(string jsonBody)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = factory.CreateHumanToken(Guid.NewGuid(), permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"api/v1/inventory/assets/{Guid.NewGuid():D}/accepting-bookings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetById_NotFound_Returns404ProblemDetails()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = factory.CreateHumanToken(Guid.NewGuid(), permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/inventory/assets/{Guid.NewGuid():D}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("?page=1&page=2")]
    [InlineData("?q=first&q=second")]
    [InlineData("?sort=created_asc&sort=model_desc")]
    [InlineData("?equipmentTypeId=3fa85f64-5717-4562-b3fc-2c963f66afa6&equipmentTypeId=3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    [InlineData("?filters=[]&filters=[]")]
    public async Task RepeatedQueryParameters_List_Returns400(string queryString)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = factory.CreateHumanToken(Guid.NewGuid(), permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/inventory/assets{queryString}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_QUERY_PARAMETER", body);
    }

    [Fact]
    public async Task ServicePrincipal_List_Returns403()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/inventory/assets");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task HumanWithoutManagePermission_List_Returns403()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var humanToken = factory.CreateHumanToken(Guid.NewGuid(), permissions: []);
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/inventory/assets");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", humanToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_UsingOwnerUserIdInQuery_StillRejectedWith400()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var adminToken = factory.CreateAdminToken(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/inventory/assets?ownerUserId={Guid.NewGuid():D}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_QUERY_PARAMETER", body);
    }

    [Theory]
    [InlineData("?page=bad")]
    [InlineData("?pageSize=abc")]
    [InlineData("?equipmentTypeId=not-a-guid")]
    [InlineData("?page=bad&page=2")]
    [InlineData("?ownerUserId=else&page=bad")]
    [InlineData("?Page=1&page=2")] // casing difference duplicate
    [InlineData("?PAGE=1&page=2")]
    public async Task BindingErrorAndMixedQueryErrors_List_Returns400WithInvalidQueryParameter(string queryString)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = factory.CreateHumanToken(Guid.NewGuid(), permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/inventory/assets{queryString}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_QUERY_PARAMETER", body);
    }
}
