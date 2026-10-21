using System.Security.Cryptography;
using System.Text;
using Dprental.PaymentService.Application.DTOs.Payout;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;

namespace Dprental.PaymentService.Application.Services;

public sealed class SupplierPayoutDestinationService(
    ISupplierPayoutDestinationRepository repository, IConfiguration configuration)
    : ISupplierPayoutDestinationService
{
    public async Task<Result<PayoutDestinationResponse>> RegisterAsync(Guid supplierId, Guid adminId,
        RegisterPayoutDestinationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (supplierId == Guid.Empty || adminId == Guid.Empty ||
            !Clean(request.OperationKey, 64) || !Clean(request.BankCode, 32) ||
            !Clean(request.AccountHolderName, 128) || !Clean(request.VerificationRef, 128) ||
            !Clean(request.AuditReason, 500) ||
            request.DestinationRef is not null && !Clean(request.DestinationRef, 64) ||
            string.IsNullOrWhiteSpace(request.AccountNumber) ||
            request.AccountNumber.Length is < 4 or > 32 || !request.AccountNumber.All(char.IsAsciiDigit))
            return Error.Validation("Payment.PayoutDestinationInvalid", "Thông tin tài khoản, bằng chứng xác minh hoặc audit không hợp lệ.");

        var key = configuration["PayoutDestination:FingerprintKey"];
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
            return Error.Validation("Payment.PayoutDestinationKeyMissing", "Payment chưa cấu hình khóa HMAC cho payout destination.");

        var bankCode = request.BankCode.Trim().ToUpperInvariant();
        var accountNumber = request.AccountNumber.Trim();
        var canonicalAccount = $"{bankCode}:{accountNumber}";
        var accountFingerprint = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(canonicalAccount)))
            .ToLowerInvariant();
        var result = await repository.RegisterAsync(new RegisterPayoutDestinationCommand(
            supplierId, adminId, request.OperationKey.Trim(), request.DestinationRef?.Trim(),
            bankCode, request.AccountHolderName.Trim(), accountNumber[^4..], accountFingerprint,
            request.VerificationRef.Trim(), request.AuditReason.Trim()), ct);
        return result.IsSuccess ? Result.Success(Map(result.Value)) : result.Error;
    }

    public async Task<Result<IReadOnlyList<PayoutDestinationResponse>>> ListAsync(Guid supplierId, CancellationToken ct)
    {
        if (supplierId == Guid.Empty)
            return Error.Validation("Payment.SupplierIdInvalid", "SupplierId không hợp lệ.");
        var rows = await repository.ListBySupplierAsync(supplierId, ct);
        return Result.Success<IReadOnlyList<PayoutDestinationResponse>>(rows.Select(Map).ToArray());
    }

    private static PayoutDestinationResponse Map(PayoutDestinationRow row) => new(
        row.Id, row.SupplierId, row.DestinationRef, row.Version, row.BankCode,
        row.AccountHolderName, $"****{row.AccountNumberLast4}", row.VerificationRef,
        row.VerifiedAt, row.VerifiedBy, row.IsReplay);

    private static bool Clean(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= max && !value.Any(char.IsControl);
}
