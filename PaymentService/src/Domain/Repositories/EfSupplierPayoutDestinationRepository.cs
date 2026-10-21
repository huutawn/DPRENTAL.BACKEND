using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dprental.PaymentService.Domain.Repositories;

public sealed class EfSupplierPayoutDestinationRepository(
    PaymentServiceDbContext db, TimeProvider clock) : ISupplierPayoutDestinationRepository
{
    private const string OperationType = "RegisterPayoutDestination";

    public async Task<Result<PayoutDestinationRow>> RegisterAsync(
        RegisterPayoutDestinationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var fingerprint = Fingerprint(command);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Replay/conflict xét trước mọi điều kiện trạng thái.
            var prior = await FindByKeyAsync(command.OperationKey, ct);
            if (prior is not null) return ReplayOrConflict(prior, command, fingerprint);

            string destinationRef;
            int version;

            if (command.DestinationRef is null)
            {
                destinationRef = $"DST-{Guid.NewGuid():N}";
                version = 1;
            }
            else
            {
                // Khóa phiên bản mới nhất của reference: các lần đăng ký phiên bản mới xếp hàng ở đây.
                var latest = (await db.SupplierPayoutDestinations
                    .FromSqlInterpolated($"""
                        SELECT * FROM supplier_payout_destinations
                        WHERE "DestinationRef" = {command.DestinationRef}
                        ORDER BY "Version" DESC LIMIT 1 FOR UPDATE
                        """)
                    .ToListAsync(ct)).SingleOrDefault();

                if (latest is null)
                    return Error.NotFound("Payment.PayoutDestinationNotFound",
                        "Không tìm thấy payout destination để tạo phiên bản mới.");
                if (latest.SupplierId != command.SupplierId)
                    return Error.Conflict("Payment.PayoutDestinationSupplierMismatch",
                        "DestinationRef thuộc supplier khác.");

                destinationRef = latest.DestinationRef;
                version = latest.Version + 1;
            }

            // Một tài khoản (theo fingerprint) không được xác minh cho hai supplier khác nhau.
            var usedByOther = await db.SupplierPayoutDestinations.AsNoTracking()
                .AnyAsync(d => d.AccountFingerprint == command.AccountFingerprint && d.SupplierId != command.SupplierId, ct);
            if (usedByOther)
                return Error.Conflict("Payment.PayoutAccountInUse", "Tài khoản này đã được xác minh cho supplier khác.");

            var row = SupplierPayoutDestination.Create(
                command.SupplierId, destinationRef, version, command.BankCode, command.AccountHolderName,
                command.AccountNumberLast4, command.AccountFingerprint, command.VerificationRef,
                command.AdminId, command.OperationKey, fingerprint, command.AuditReason, clock.GetUtcNow());

            db.SupplierPayoutDestinations.Add(row);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Result.Success(Map(row, isReplay: false));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();

            var prior = await FindByKeyAsync(command.OperationKey, ct);
            return prior is not null
                ? ReplayOrConflict(prior, command, fingerprint)
                : Error.Conflict("Payment.PayoutDestinationConflict",
                    "Payout destination xung đột với một phiên bản vừa được tạo; thử lại.");
        }
    }

    public async Task<IReadOnlyList<PayoutDestinationRow>> ListBySupplierAsync(Guid supplierId, CancellationToken ct) =>
        await db.SupplierPayoutDestinations.AsNoTracking()
            .Where(d => d.SupplierId == supplierId)
            .OrderBy(d => d.DestinationRef).ThenByDescending(d => d.Version)
            .Select(d => new PayoutDestinationRow(
                d.Id, d.SupplierId, d.DestinationRef, d.Version, d.BankCode, d.AccountHolderName,
                d.AccountNumberLast4, d.VerificationRef, d.VerifiedAt, d.VerifiedBy, false))
            .ToListAsync(ct);

    private Task<SupplierPayoutDestination?> FindByKeyAsync(string operationKey, CancellationToken ct) =>
        db.SupplierPayoutDestinations.AsNoTracking().SingleOrDefaultAsync(d => d.OperationKey == operationKey, ct);

    private static Result<PayoutDestinationRow> ReplayOrConflict(
        SupplierPayoutDestination prior, RegisterPayoutDestinationCommand command, string fingerprint) =>
        prior.SupplierId != command.SupplierId || prior.PayloadFingerprint != fingerprint
            ? Error.Conflict("Payment.PayoutDestinationPayloadConflict", "Operation key đã dùng với nội dung khác.")
            : Result.Success(Map(prior, isReplay: true));

    private static PayoutDestinationRow Map(SupplierPayoutDestination d, bool isReplay) =>
        new(d.Id, d.SupplierId, d.DestinationRef, d.Version, d.BankCode, d.AccountHolderName,
            d.AccountNumberLast4, d.VerificationRef, d.VerifiedAt, d.VerifiedBy, isReplay);

    // Chỉ dùng fingerprint HMAC của tài khoản, không bao giờ dùng số đầy đủ.
    private static string Fingerprint(RegisterPayoutDestinationCommand command)
    {
        var canonical = JsonSerializer.Serialize(new[]
        {
            OperationType, command.SupplierId.ToString("D"), command.AdminId.ToString("D"), command.DestinationRef ?? "",
            command.BankCode, command.AccountHolderName, command.AccountFingerprint, command.VerificationRef, command.AuditReason
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
