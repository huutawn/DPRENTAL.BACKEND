#pragma warning disable CA1848, CA1873
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Clients;

public sealed class HttpInventoryRentalClient : IInventoryRentalClient
{
    private const string InventoryAudience = "inventory-api";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IServiceTokenProvider _tokenProvider;
    private readonly ILogger<HttpInventoryRentalClient> _logger;
    private readonly string _baseUrl;

    public HttpInventoryRentalClient(
        HttpClient httpClient,
        IServiceTokenProvider tokenProvider,
        IConfiguration configuration,
        ILogger<HttpInventoryRentalClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        string configuredBase = configuration["Services:Inventory:BaseUrl"] ?? "http://inventory-service:8080";
        _baseUrl = configuredBase.TrimEnd('/');
        if (!_baseUrl.EndsWith("/api/v1/inventory", StringComparison.OrdinalIgnoreCase))
        {
            _baseUrl += "/api/v1/inventory";
        }

        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(
        ListingAvailabilityCheckRequest request,
        CancellationToken ct = default)
    {
        string route = InventoryContractSpec.Routes.CheckAvailability(request.ListingId);
        return await SendAsync<ListingAvailabilityCheckRequest, ListingAvailabilityCheckResponse>(
            HttpMethod.Post,
            route,
            request,
            InventoryContractSpec.Scopes.ReadAvailability,
            ct);
    }

    public async Task<Result<ListingRentalSnapshotsResponse>> GetListingRentalSnapshotsAsync(
        ListingRentalSnapshotsRequest request,
        CancellationToken ct = default)
    {
        string scope = request.IncludeWarehouse
            ? $"{InventoryContractSpec.Scopes.ReadAvailability} {InventoryContractSpec.Scopes.ReadWarehouse}"
            : InventoryContractSpec.Scopes.ReadAvailability;

        return await SendAsync<ListingRentalSnapshotsRequest, ListingRentalSnapshotsResponse>(
            HttpMethod.Post,
            InventoryContractSpec.Routes.ListingRentalSnapshots,
            request,
            scope,
            ct);
    }

    public async Task<Result<CreateHoldResponse>> CreateHoldAsync(
        CreateHoldRequest request,
        CancellationToken ct = default)
    {
        string route = InventoryContractSpec.Routes.CreateHold(request.ListingId);
        return await SendAsync<CreateHoldRequest, CreateHoldResponse>(
            HttpMethod.Post,
            route,
            request,
            InventoryContractSpec.Scopes.WriteAllocation,
            ct);
    }

    public async Task<Result<ExtendHoldResponse>> ExtendHoldAsync(
        string allocationGroupId,
        ExtendHoldRequest request,
        CancellationToken ct = default)
    {
        string route = InventoryContractSpec.Routes.ExtendHold(allocationGroupId);
        return await SendAsync<ExtendHoldRequest, ExtendHoldResponse>(
            HttpMethod.Post,
            route,
            request,
            InventoryContractSpec.Scopes.WriteAllocation,
            ct);
    }

    public async Task<Result<ConfirmAllocationResponse>> ConfirmAllocationAsync(
        string allocationGroupId,
        ConfirmAllocationRequest request,
        CancellationToken ct = default)
    {
        string route = InventoryContractSpec.Routes.ConfirmAllocation(allocationGroupId);
        return await SendAsync<ConfirmAllocationRequest, ConfirmAllocationResponse>(
            HttpMethod.Post,
            route,
            request,
            InventoryContractSpec.Scopes.WriteAllocation,
            ct);
    }

    public async Task<Result<ReleaseAllocationResponse>> ReleaseAllocationAsync(
        string allocationGroupId,
        ReleaseAllocationRequest request,
        CancellationToken ct = default)
    {
        string route = InventoryContractSpec.Routes.ReleaseAllocation(allocationGroupId);
        return await SendAsync<ReleaseAllocationRequest, ReleaseAllocationResponse>(
            HttpMethod.Post,
            route,
            request,
            InventoryContractSpec.Scopes.WriteAllocation,
            ct);
    }

    public async Task<Result<CancelAllocationResponse>> CancelAllocationAsync(
        string allocationGroupId,
        CancelAllocationRequest request,
        CancellationToken ct = default)
    {
        string route = InventoryContractSpec.Routes.CancelAllocation(allocationGroupId);
        return await SendAsync<CancelAllocationRequest, CancelAllocationResponse>(
            HttpMethod.Post,
            route,
            request,
            InventoryContractSpec.Scopes.WriteAllocation,
            ct);
    }

    public async Task<Result<MarkAssetsOverdueResponse>> MarkAssetsOverdueAsync(
        string allocationGroupId,
        MarkAssetsOverdueRequest request,
        CancellationToken ct = default)
    {
        string route = InventoryContractSpec.Routes.MarkOverdue(allocationGroupId);
        return await SendAsync<MarkAssetsOverdueRequest, MarkAssetsOverdueResponse>(
            HttpMethod.Post,
            route,
            request,
            InventoryContractSpec.Scopes.WriteAllocation,
            ct);
    }

    public async Task<Result<MarkAssetsReturnedResponse>> MarkAssetsReturnedAsync(
        string allocationGroupId,
        MarkAssetsReturnedRequest request,
        CancellationToken ct = default)
    {
        string route = InventoryContractSpec.Routes.MarkReturned(allocationGroupId);
        return await SendAsync<MarkAssetsReturnedRequest, MarkAssetsReturnedResponse>(
            HttpMethod.Post,
            route,
            request,
            InventoryContractSpec.Scopes.WriteAllocation,
            ct);
    }

    public async Task<Result<BlockDamagedAssetsResponse>> BlockDamagedAssetsAsync(
        string allocationGroupId,
        BlockDamagedAssetsRequest request,
        CancellationToken ct = default)
    {
        string route = InventoryContractSpec.Routes.BlockDamaged(allocationGroupId);
        return await SendAsync<BlockDamagedAssetsRequest, BlockDamagedAssetsResponse>(
            HttpMethod.Post,
            route,
            request,
            InventoryContractSpec.Scopes.WriteAllocation,
            ct);
    }

    private async Task<Result<TResponse>> SendAsync<TRequest, TResponse>(
        HttpMethod method,
        string relativeRoute,
        TRequest? requestBody,
        string scope,
        CancellationToken ct)
    {
        // 1. Obtain token with audience & scope
        var tokenResult = await _tokenProvider.GetServiceTokenAsync(InventoryAudience, scope, ct);
        if (tokenResult.IsFailure)
        {
            _logger.LogError("Không thể lấy token cho InventoryService (Audience: {Audience}, Scope: {Scope}): {Error}",
                InventoryAudience, scope, tokenResult.Error.Message);
            return tokenResult.Error;
        }

        string fullUrl = $"{_baseUrl}{relativeRoute}";

        try
        {
            using var httpRequest = new HttpRequestMessage(method, fullUrl);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.Value);

            if (requestBody != null)
            {
                string json = JsonSerializer.Serialize(requestBody, JsonOptions);
                httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(10));

            using var httpResponse = await _httpClient.SendAsync(httpRequest, linkedCts.Token);
            string responseJson = await httpResponse.Content.ReadAsStringAsync(linkedCts.Token);
            if (httpResponse.IsSuccessStatusCode)
            {
                var data = JsonSerializer.Deserialize<TResponse>(responseJson, JsonOptions);
                if (data != null)
                {
                    return Result.Success(data);
                }
                return Error.Failure("INVENTORY_DESERIALIZATION_FAILED", "Không thể deserialize phản hồi từ InventoryService.");
            }

            string errorCode = $"Inventory.Http{(int)httpResponse.StatusCode}";
            string errorMessage = $"InventoryService trả về trạng thái {httpResponse.StatusCode}.";

            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("title", out var titleProp))
                {
                    errorCode = titleProp.GetString() ?? errorCode;
                }
                else if (doc.RootElement.TryGetProperty("code", out var codeProp))
                {
                    errorCode = codeProp.GetString() ?? errorCode;
                }

                if (doc.RootElement.TryGetProperty("detail", out var detailProp))
                {
                    errorMessage = detailProp.GetString() ?? errorMessage;
                }
                else if (doc.RootElement.TryGetProperty("message", out var msgProp))
                {
                    errorMessage = msgProp.GetString() ?? errorMessage;
                }
            }
            catch
            {
                // Raw response
            }

            if (httpResponse.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                return Error.Conflict(errorCode, errorMessage);
            }
            if (httpResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Error.NotFound(errorCode, errorMessage);
            }
            if (httpResponse.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                return Error.Validation(errorCode, errorMessage);
            }

            return Error.Failure(errorCode, errorMessage);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Gọi InventoryService url {Url} bị quá hạn 10s", fullUrl);
            return Error.ServiceUnavailable("Inventory.Timeout", "InventoryService không phản hồi kịp thời (timeout 10s).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi kết nối tới InventoryService url {Url}", fullUrl);
            return Error.ServiceUnavailable("Inventory.Unavailable", "Không thể kết nối tới InventoryService.");
        }
    }
}
