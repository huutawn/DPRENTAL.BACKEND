#pragma warning disable CA1848, CA1873
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Api.Clients;

public sealed class HttpPaymentRentalClient : IPaymentRentalClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IServiceTokenProvider _tokenProvider;
    private readonly ILogger<HttpPaymentRentalClient> _logger;
    private readonly string _audience;

    public HttpPaymentRentalClient(
        HttpClient httpClient,
        IServiceTokenProvider tokenProvider,
        IConfiguration configuration,
        ILogger<HttpPaymentRentalClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        string? audience = configuration["Services:Payment:Audience"] ?? configuration["Authentication:PaymentAudience"];
        if (string.IsNullOrWhiteSpace(audience))
        {
            audience = "payment-api"; // standard default or required config
        }
        _audience = audience;

        string baseUrl = configuration["Services:Payment:BaseUrl"] ?? "http://payment-service:8080";
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        _httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(
        CreateInvoiceRequest request,
        CancellationToken ct = default)
    {
        return await SendJsonAsync<CreateInvoiceRequest, CreateInvoiceResponse>(
            HttpMethod.Post,
            PaymentContractSpec.ProposedRoutes.CreateInvoice,
            request,
            PaymentContractSpec.ProposedScopes.WriteInvoice,
            ct);
    }

    public async Task<Result<CancelInvoiceResponse>> CancelInvoiceAsync(
        CancelInvoiceRequest request,
        CancellationToken ct = default)
    {
        string route = PaymentContractSpec.ProposedRoutes.CancelInvoice(request.InvoiceRef);
        return await SendJsonAsync<CancelInvoiceRequest, CancelInvoiceResponse>(
            HttpMethod.Post,
            route,
            request,
            PaymentContractSpec.ProposedScopes.WriteInvoice,
            ct);
    }

    public async Task<Result<RequestRefundResponse>> RequestRefundAsync(
        RequestRefundRequest request,
        CancellationToken ct = default)
    {
        return await SendJsonAsync<RequestRefundRequest, RequestRefundResponse>(
            HttpMethod.Post,
            PaymentContractSpec.ProposedRoutes.RequestRefund,
            request,
            PaymentContractSpec.ProposedScopes.WriteRefund,
            ct);
    }

    public async Task<Result<SettleRentalResponse>> SettleRentalAsync(
        SettleRentalRequest request,
        CancellationToken ct = default)
    {
        return await SendJsonAsync<SettleRentalRequest, SettleRentalResponse>(
            HttpMethod.Post,
            PaymentContractSpec.ProposedRoutes.SettleRental,
            request,
            PaymentContractSpec.ProposedScopes.WriteSettlement,
            ct);
    }

    private async Task<Result<TResponse>> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string route,
        TRequest requestBody,
        string scope,
        CancellationToken ct)
    {
        // 1. Obtain service token
        var tokenResult = await _tokenProvider.GetServiceTokenAsync(_audience, scope, ct);
        if (tokenResult.IsFailure)
        {
            _logger.LogError("Không thể lấy token cho PaymentService (Audience: {Audience}, Scope: {Scope}): {Error}",
                _audience, scope, tokenResult.Error.Message);
            return tokenResult.Error;
        }

        try
        {
            using var httpRequest = new HttpRequestMessage(method, route);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.Value);

            string json = JsonSerializer.Serialize(requestBody, JsonOptions);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(10));

            using var httpResponse = await _httpClient.SendAsync(httpRequest, linkedCts.Token);
            string responseJson = await httpResponse.Content.ReadAsStringAsync(ct);

            if (httpResponse.IsSuccessStatusCode)
            {
                var data = JsonSerializer.Deserialize<TResponse>(responseJson, JsonOptions);
                if (data != null)
                {
                    return Result.Success(data);
                }
                return Error.Failure("PAYMENT_DESERIALIZATION_FAILED", "Không thể deserialize phản hồi từ PaymentService.");
            }

            string errorCode = $"Payment.Http{(int)httpResponse.StatusCode}";
            string errorMessage = $"PaymentService trả về lỗi {(int)httpResponse.StatusCode}.";

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
                // Raw non-JSON error
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
            _logger.LogWarning("Gọi PaymentService route {Route} bị quá hạn 10s", route);
            return Error.ServiceUnavailable("Payment.Timeout", "PaymentService không phản hồi kịp thời (timeout 10s).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi kết nối tới PaymentService route {Route}", route);
            return Error.ServiceUnavailable("Payment.Unavailable", "Không thể kết nối tới PaymentService.");
        }
    }
}
