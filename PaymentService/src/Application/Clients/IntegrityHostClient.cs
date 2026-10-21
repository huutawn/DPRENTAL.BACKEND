#pragma warning disable CA1848
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Application.Services.Integrity;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Clients;

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

            var receipt = verificationResponse.Receipt;
            if (receipt is null)
            {
                return Error.Failure("IntegrityHost.MissingReceipt", "Phản hồi kiểm chứng từ IntegrityHost thiếu VerificationReceipt.");
            }

            var nowAfterResponse = _timeProvider.GetUtcNow();
            var body = receipt.Body;
            if (body is null)
            {
                return Error.Failure("IntegrityHost.MalformedReceipt", "Receipt thiếu signed body.");
            }

            if (body.Schema != 2)
            {
                return Error.Conflict("IntegrityHost.InvalidSchema", $"Schema receipt '{body.Schema}' không được hỗ trợ (yêu cầu Schema 2).");
            }

            string expectedIssuer = !string.IsNullOrWhiteSpace(config.Issuer) ? config.Issuer : "dprental-integrity-host";
            if (!string.Equals(body.Issuer, expectedIssuer, StringComparison.Ordinal))
            {
                return Error.Conflict("IntegrityHost.IssuerMismatch", $"Issuer '{body.Issuer}' không khớp với cấu hình '{expectedIssuer}'.");
            }

            string expectedAudience = !string.IsNullOrWhiteSpace(config.Audience) ? config.Audience : "payment-api";
            if (!string.Equals(body.Audience, expectedAudience, StringComparison.Ordinal))
            {
                return Error.Conflict("IntegrityHost.AudienceMismatch", $"Audience '{body.Audience}' không khớp với cấu hình '{expectedAudience}'.");
            }

            string expectedEnv = !string.IsNullOrWhiteSpace(config.Environment) ? config.Environment : "production";
            if (!string.Equals(body.Environment, expectedEnv, StringComparison.OrdinalIgnoreCase))
            {
                return Error.Conflict("IntegrityHost.EnvironmentMismatch", $"Environment '{body.Environment}' không khớp với cấu hình '{expectedEnv}'.");
            }

            if (nowAfterResponse > body.ExpiresAt)
            {
                return Error.Conflict("IntegrityHost.ReceiptExpired", "Verification receipt đã hết hạn hiệu lực.");
            }

            if (body.ExpiresAt <= body.IssuedAt)
            {
                return Error.Conflict("IntegrityHost.InvalidReceiptLifetime", "Thời điểm hết hạn của receipt phải sau thời điểm phát hành.");
            }

            if (body.IssuedAt > nowAfterResponse.AddSeconds(1))
            {
                return Error.Conflict("IntegrityHost.FutureReceipt", "Thời điểm phát hành của receipt không được ở tương lai quá 1 giây.");
            }

            if ((body.ExpiresAt - body.IssuedAt) > TimeSpan.FromSeconds(5))
            {
                return Error.Conflict("IntegrityHost.LifetimeExceeded", "Hiệu lực của receipt không được vượt quá 5 giây.");
            }

            if (!string.Equals(body.CallerNonce, request.CallerNonce, StringComparison.Ordinal))
            {
                return Error.Conflict("IntegrityHost.NonceMismatch", "CallerNonce trong receipt không khớp với request.");
            }

            string expectedRequestHash = CanonicalJournalHasher.ComputeRequestHash(
                request.PaymentRef,
                request.Purpose,
                request.CallerNonce,
                request.CallerIdentity,
                request.ExpectedAmount,
                request.ExpectedCurrency,
                request.InvoiceRef,
                request.ExpectedOrderId,
                request.ExpectedRenterId,
                request.RentalItemId,
                request.SupplierId,
                request.RequirementRef,
                request.ExpectedOutboxDigest,
                request.Topic,
                request.MessageKey,
                request.ExpectedStatus,
                request.OutboxMessageId);
            if (!string.Equals(body.RequestHash, expectedRequestHash, StringComparison.OrdinalIgnoreCase))
            {
                return Error.Conflict("IntegrityHost.RequestHashMismatch", "RequestHash trong receipt không khớp với request thực tế.");
            }

            var publicKey = GetOrLoadPublicKey(body.KeyId, config);
            if (publicKey is null)
            {
                return Error.Conflict("IntegrityHost.UnknownKey", $"Không tìm thấy public key tương ứng với keyId '{body.KeyId}'.");
            }

            string claimsDigest = EcdsaAttestation.ComputeClaimsDigest(
                body.EntityClaims?.Select(c => (c.EntityType, c.EntityId, c.Revision, c.Digest)));

            string receiptMessage = EcdsaAttestation.BuildVerificationReceiptMessageV2(
                body.Schema,
                body.Issuer,
                body.Audience,
                body.Environment,
                body.ReceiptId,
                body.KeyId,
                body.RequestHash,
                body.CallerIdentity,
                body.CallerNonce,
                body.Purpose,
                body.IntegrityStatus,
                body.IsUsable,
                body.Reason,
                body.ActualAmount,
                body.ActualCurrency,
                body.ActualStatus,
                claimsDigest,
                body.IssuedAt,
                body.ExpiresAt);

            bool sigValid = EcdsaAttestation.Verify(publicKey, receiptMessage, receipt.Signature);
            if (!sigValid)
            {
                return Error.Conflict("IntegrityHost.InvalidReceiptSignature", "Chữ ký verification receipt không hợp lệ.");
            }

            if (request.Purpose == VerificationPurposes.OutboxPublication && body.IsUsable)
            {
                var proof = verificationResponse.PublicationProof;
                if (proof is null || !EcdsaAttestation.Verify(publicKey, proof.SigningMessage, proof.Signature))
                    return Error.Conflict("IntegrityHost.InvalidPublicationProof", "Missing or invalid publication signature.");
                PublicationProofBody? proofBody;
                try { proofBody = System.Text.Json.JsonSerializer.Deserialize<PublicationProofBody>(proof.Payload, System.Text.Json.JsonSerializerOptions.Web); }
                catch (System.Text.Json.JsonException) { return Error.Conflict("IntegrityHost.InvalidPublicationProof", "Malformed publication proof."); }
                if (proofBody is null || proofBody.Schema != 1 || proofBody.KeyId != body.KeyId ||
                    proofBody.Issuer != config.Issuer || proofBody.Environment != config.Environment || proofBody.Audience != "rental-service" ||
                    proofBody.MessageId != request.OutboxMessageId || proofBody.Topic != request.Topic ||
                    proofBody.MessageKey != request.MessageKey || proofBody.PayloadDigest != request.ExpectedOutboxDigest)
                    return Error.Conflict("IntegrityHost.InvalidPublicationProof", "Publication evidence does not match the requested message.");
            }

            var boundResponse = new TransactionVerificationResponse(
                new TransactionIntegrityResult(
                    Status: body.IntegrityStatus,
                    Version: verificationResponse.Integrity.Version,
                    ConfirmationSource: verificationResponse.Integrity.ConfirmationSource,
                    ConfirmedAt: verificationResponse.Integrity.ConfirmedAt,
                    KeyId: body.KeyId,
                    Signature: verificationResponse.Integrity.Signature,
                    Proof: verificationResponse.Integrity.Proof,
                    CallerNonce: body.CallerNonce),
                new TransactionUsabilityResult(
                    IsUsable: body.IsUsable,
                    Reason: body.Reason,
                    ActualAmount: body.ActualAmount,
                    ActualCurrency: body.ActualCurrency,
                    ActualStatus: body.ActualStatus),
                Receipt: receipt,
                PublicationProof: verificationResponse.PublicationProof);

            return Result.Success(boundResponse);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Error.Failure("Payment.Verification.Timeout", "Quá thời gian xử lý kiểm chứng trong ngân sách 900ms.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP failure contacting IntegrityHost.");
            return Error.Failure("IntegrityHost.Unavailable", $"Không thể kết nối đến IntegrityHost: {ex.Message}");
        }
    }

    private ECDsa? GetOrLoadPublicKey(string keyId, IntegrityVerificationOptions config)
    {
        if (_publicKeyCache.TryGetValue(keyId, out var cached))
        {
            return cached;
        }

        string? keyBase64 = null;
        if (config.PublicKeys != null && config.PublicKeys.TryGetValue(keyId, out var fromDict))
        {
            keyBase64 = fromDict;
        }

        if (string.IsNullOrWhiteSpace(keyBase64))
        {
            return null;
        }

        try
        {
            var ecdsa = EcdsaAttestation.ImportPublicKeyBase64(keyBase64.Trim());
            _publicKeyCache[keyId] = ecdsa;
            return ecdsa;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse public key for KeyId '{KeyId}'.", keyId);
            return null;
        }
    }
}
