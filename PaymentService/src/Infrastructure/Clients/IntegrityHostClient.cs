#pragma warning disable CA1848
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Infrastructure.Clients;

public sealed class IntegrityHostClient : ITransactionIntegrityChecker
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<IntegrityVerificationOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IntegrityHostClient> _logger;
    private readonly ConcurrentDictionary<string, ECDsa> _publicKeyCache = new(StringComparer.Ordinal);

    public IntegrityHostClient(
        HttpClient httpClient,
        IOptionsMonitor<IntegrityVerificationOptions> options,
        ILogger<IntegrityHostClient> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<TransactionVerificationResponse>> VerifyAsync(
        TransactionVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var config = _options.CurrentValue;
        int budgetMs = config.ServerBudgetMilliseconds > 0 ? config.ServerBudgetMilliseconds : 900;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(budgetMs));

        var now = _timeProvider.GetUtcNow();

        try
        {
            using var httpResponse = await _httpClient.PostAsJsonAsync(
                "/internal/v1/integrity/verify",
                request,
                cts.Token);

            if (httpResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return Result.Success(new TransactionVerificationResponse(
                    new TransactionIntegrityResult(
                        Status: IntegrityStatuses.NotFound,
                        Version: 0,
                        ConfirmationSource: null,
                        ConfirmedAt: null,
                        KeyId: null,
                        Signature: null,
                        Proof: null,
                        CallerNonce: request.CallerNonce),
                    new TransactionUsabilityResult(
                        IsUsable: false,
                        Reason: "NOT_FOUND",
                        ActualAmount: 0m,
                        ActualCurrency: request.ExpectedCurrency ?? "VND",
                        ActualStatus: "NOT_FOUND")));
            }

            if (!httpResponse.IsSuccessStatusCode)
            {
                string errorBody = await httpResponse.Content.ReadAsStringAsync(cts.Token);
                return Error.Failure("IntegrityHost.Error", string.IsNullOrWhiteSpace(errorBody) ? $"IntegrityHost returned {(int)httpResponse.StatusCode}" : errorBody);
            }

            var verificationResponse = await httpResponse.Content.ReadFromJsonAsync<TransactionVerificationResponse>(cts.Token);
            if (verificationResponse is null)
            {
                return Error.Failure("IntegrityHost.EmptyResponse", "IntegrityHost returned empty verification response.");
            }

            // Client-side verification of the signed VerificationReceipt
            var receipt = verificationResponse.Receipt;
            if (receipt is not null)
            {
                if (now > receipt.ExpiresAt)
                {
                    return Error.Conflict("IntegrityHost.ReceiptExpired", "Verification receipt đã hết hạn hiệu lực (5s).");
                }

                if (!string.Equals(receipt.CallerNonce, request.CallerNonce, StringComparison.Ordinal))
                {
                    return Error.Conflict("IntegrityHost.NonceMismatch", "CallerNonce trong receipt không khớp với request.");
                }

                string expectedRequestHash = CanonicalJournalHasher.ComputeRequestHash(
                    request.PaymentRef,
                    request.Purpose,
                    request.CallerNonce,
                    request.ExpectedAmount,
                    request.ExpectedCurrency,
                    request.ExpectedStatus,
                    request.InvoiceRef,
                    request.ExpectedOrderId,
                    request.ExpectedRenterId,
                    request.RentalItemId,
                    request.SupplierId,
                    request.RequirementRef);

                if (!string.Equals(receipt.RequestHash, expectedRequestHash, StringComparison.OrdinalIgnoreCase))
                {
                    return Error.Conflict("IntegrityHost.RequestHashMismatch", "RequestHash trong receipt không khớp với request thực tế.");
                }

                var publicKey = GetOrLoadPublicKey(receipt.KeyId, config);
                if (publicKey is null)
                {
                    return Error.Conflict("IntegrityHost.UnknownKey", $"Không tìm thấy public key tương ứng với keyId '{receipt.KeyId}'.");
                }

                string receiptMessage = EcdsaAttestation.BuildVerificationReceiptMessage(
                    receipt.KeyId,
                    receipt.RequestHash,
                    receipt.CallerNonce,
                    receipt.IntegrityStatus,
                    receipt.IsUsable,
                    receipt.Reason,
                    receipt.IssuedAt,
                    receipt.ExpiresAt);

                bool sigValid = EcdsaAttestation.Verify(publicKey, receiptMessage, receipt.Signature);
                if (!sigValid)
                {
                    return Error.Conflict("IntegrityHost.InvalidReceiptSignature", "Chữ ký verification receipt không hợp lệ.");
                }
            }

            return Result.Success(verificationResponse);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Error.Failure("Payment.Verification.Timeout", "Quá thời gian xử lý kiểm chứng trong ngân sách 900ms.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Lỗi kết nối tới IntegrityHost.");
            return Error.Failure("IntegrityHost.Unavailable", "IntegrityHost không khả dụng.");
        }
    }

    private ECDsa? GetOrLoadPublicKey(string keyId, IntegrityVerificationOptions config)
    {
        if (_publicKeyCache.TryGetValue(keyId, out var cached))
        {
            return cached;
        }

        if (!config.PublicKeys.TryGetValue(keyId, out var base64Key) || string.IsNullOrWhiteSpace(base64Key))
        {
            return null;
        }

        try
        {
            var ecdsa = EcdsaAttestation.ImportPublicKeyBase64(base64Key);
            _publicKeyCache[keyId] = ecdsa;
            return ecdsa;
        }
        catch
        {
            return null;
        }
    }
}
