using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Dprental.PaymentService.Application.DTOs.Events;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Application.Utils;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Utils;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Utils;
using Dprental.PaymentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Dprental.PaymentService.Infrastructure.Services;

[SuppressMessage("Usage", "CA1848:Use the LoggerMessage delegates", Justification = "Service log messages.")]
public sealed class InvoiceService : IInvoiceService
{
    private readonly PaymentServiceDbContext _dbContext;
    private readonly IOptions<PaymentMerchantOptions> _merchantOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InvoiceService> _logger;

    /// <summary>
    /// Hook phục vụ kiểm thử race condition có điều phối giữa bước đọc AsNoTracking và bước khóa hàng trong GetInvoiceAsync.
    /// </summary>
    public Func<CancellationToken, Task>? OnBeforeAcquireGetLockForTest { get; set; }

    public InvoiceService(
        PaymentServiceDbContext dbContext,
        IOptions<PaymentMerchantOptions> merchantOptions,
        TimeProvider? timeProvider = null,
        ILogger<InvoiceService>? logger = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _merchantOptions = merchantOptions ?? throw new ArgumentNullException(nameof(merchantOptions));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<InvoiceService>.Instance;
    }

    public async Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(
        CreateInvoiceRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validationResult = request.Validate();
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        string fingerprint = PayloadFingerprinter.ComputeFingerprint(request);
        var utcNow = _timeProvider.GetUtcNow().ToUniversalTime();
        var utcDeadline = request.PaymentDeadline.ToUniversalTime();

        // 1. Check idempotency receipt first (before validating deadline against current time)
        var existingReceipt = await _dbContext.OperationReceipts
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.OperationKey == request.OperationKey, ct);

        if (existingReceipt is not null)
        {
            if (string.Equals(existingReceipt.OperationType, "CreateInvoice", StringComparison.Ordinal)
                && string.Equals(existingReceipt.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
            {
                var parsed = JsonSerializer.Deserialize<CreateInvoiceResponse>(existingReceipt.ResultJson)!;
                await EnsureExpiredIfPastDeadlineAsync(parsed.InvoiceRef, utcNow, ct);
                return Result.Success(new CreateInvoiceResponse(
                    parsed.InvoiceRef,
                    parsed.TotalAmount,
                    parsed.Currency,
                    parsed.PaymentDeadline.ToUniversalTime(),
                    IsReplay: true));
            }

            return Error.Conflict("DUPLICATE_OPERATION_KEY", "OperationKey đã tồn tại với payload hoặc nghiệp vụ khác.");
        }

        // 2. Validate payment deadline against current time
        if (utcDeadline <= utcNow)
        {
            return Error.Validation("INVALID_PAYMENT_DEADLINE", "Hạn thanh toán phải lớn hơn thời điểm tạo mới.");
        }

        // 3. Ensure one invoice per rental order
        bool orderExists = await _dbContext.Invoices
            .AsNoTracking()
            .AnyAsync(i => i.RentalOrderId == request.RentalOrderId, ct);
        if (orderExists)
        {
            var racedReceipt = await _dbContext.OperationReceipts
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.OperationKey == request.OperationKey, ct);

            if (racedReceipt is not null)
            {
                if (string.Equals(racedReceipt.OperationType, "CreateInvoice", StringComparison.Ordinal)
                    && string.Equals(racedReceipt.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    var parsed = JsonSerializer.Deserialize<CreateInvoiceResponse>(racedReceipt.ResultJson)!;
                    await EnsureExpiredIfPastDeadlineAsync(parsed.InvoiceRef, utcNow, ct);
                    return Result.Success(new CreateInvoiceResponse(
                        parsed.InvoiceRef,
                        parsed.TotalAmount,
                        parsed.Currency,
                        parsed.PaymentDeadline.ToUniversalTime(),
                        IsReplay: true));
                }

                return Error.Conflict("DUPLICATE_OPERATION_KEY", "OperationKey đã tồn tại với payload hoặc nghiệp vụ khác.");
            }

            return Error.Conflict("INVOICE_ALREADY_EXISTS", "Đơn hàng đã tồn tại hóa đơn.");
        }

        var merchant = _merchantOptions.Value;

        // 4. Collision retry loop for PaymentCode (up to 5 attempts)
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            string paymentCode = PaymentCodeGenerator.Generate();

            var domainItems = new List<InvoiceItem>();
            foreach (var itemReq in request.Items)
            {
                var itemRes = InvoiceItem.Create(
                    itemReq.RentalItemId,
                    itemReq.SupplierId,
                    itemReq.RentalAmount,
                    itemReq.DepositAmount);

                if (itemRes.IsFailure) return itemRes.Error;
                domainItems.Add(itemRes.Value);
            }

            var invoiceRes = Invoice.Create(
                request.RentalOrderId,
                request.OrderCode,
                request.RenterId,
                request.Currency,
                utcDeadline,
                domainItems,
                paymentCode,
                merchant.Bank,
                merchant.Account,
                utcNow);

            if (invoiceRes.IsFailure) return invoiceRes.Error;
            var invoice = invoiceRes.Value;

            OutboxMessage? outbox = null;
            if (invoice.TotalAmount == 0m)
            {
                Guid messageId = Guid.NewGuid();
                var eventPayload = new InvoicePaidPayload(
                    invoice.RentalOrderId,
                    invoice.RenterId,
                    invoice.InvoiceRef,
                    invoice.PaymentRef!,
                    0m,
                    invoice.Currency,
                    invoice.PaidAt ?? utcNow);

                var envelope = new PaymentEventEnvelope<InvoicePaidPayload>(
                    messageId,
                    "InvoicePaid",
                    1,
                    "payment-service",
                    invoice.RentalOrderId.ToString("D"),
                    utcNow,
                    messageId.ToString("D"),
                    null,
                    eventPayload);

                outbox = OutboxMessage.Create(
                    envelope.MessageId,
                    envelope.MessageType,
                    "RentalOrder",
                    envelope.AggregateId,
                    JsonSerializer.Serialize(envelope),
                    utcNow);
            }

            var response = new CreateInvoiceResponse(
                invoice.InvoiceRef,
                invoice.TotalAmount,
                invoice.Currency,
                invoice.PaymentDeadline,
                IsReplay: false);

            var receipt = OperationReceipt.Create(
                request.OperationKey,
                "CreateInvoice",
                fingerprint,
                JsonSerializer.Serialize(response),
                invoice.InvoiceRef,
                invoice.RentalOrderId,
                utcNow);

            await using var tx = await _dbContext.Database.BeginTransactionAsync(ct);
            try
            {
                _dbContext.Invoices.Add(invoice);
                if (outbox is not null)
                {
                    _dbContext.OutboxMessages.Add(outbox);
                }
                _dbContext.OperationReceipts.Add(receipt);

                await _dbContext.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return Result.Success(response);
            }
            catch (DbUpdateException ex)
            {
                await tx.RollbackAsync(ct);
                _dbContext.ChangeTracker.Clear();

                if (IsUniqueViolation(ex, "IX_operation_receipts_OperationKey") ||
                    IsUniqueViolation(ex, "IX_invoices_RentalOrderId"))
                {
                    var raced = await _dbContext.OperationReceipts
                        .AsNoTracking()
                        .FirstOrDefaultAsync(r => r.OperationKey == request.OperationKey, ct);

                    if (raced is null)
                    {
                        await Task.Delay(50, ct);
                        raced = await _dbContext.OperationReceipts
                            .AsNoTracking()
                            .FirstOrDefaultAsync(r => r.OperationKey == request.OperationKey, ct);
                    }

                    if (raced is not null)
                    {
                        if (string.Equals(raced.OperationType, "CreateInvoice", StringComparison.Ordinal)
                            && string.Equals(raced.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
                        {
                            var parsed = JsonSerializer.Deserialize<CreateInvoiceResponse>(raced.ResultJson)!;
                            await EnsureExpiredIfPastDeadlineAsync(parsed.InvoiceRef, utcNow, ct);
                            return Result.Success(new CreateInvoiceResponse(
                                parsed.InvoiceRef,
                                parsed.TotalAmount,
                                parsed.Currency,
                                parsed.PaymentDeadline.ToUniversalTime(),
                                IsReplay: true));
                        }

                        return Error.Conflict("DUPLICATE_OPERATION_KEY", "OperationKey đã tồn tại với payload hoặc nghiệp vụ khác.");
                    }

                    if (IsUniqueViolation(ex, "IX_invoices_RentalOrderId") ||
                        await _dbContext.Invoices.AsNoTracking().AnyAsync(i => i.RentalOrderId == request.RentalOrderId, ct))
                    {
                        return Error.Conflict("INVOICE_ALREADY_EXISTS", "Đơn hàng đã tồn tại hóa đơn.");
                    }
                }

                if (IsUniqueViolation(ex, "IX_invoices_PaymentCode"))
                {
                    _logger.LogWarning("PaymentCode collision on attempt {Attempt}/{MaxAttempts}; retrying...", attempt, maxAttempts);
                    continue;
                }

                throw;
            }
        }

        return Error.Failure("PAYMENT_CODE_COLLISION_EXHAUSTED", "Không thể sinh mã thanh toán duy nhất sau 5 lần thử.");
    }

    public async Task<Result<CancelInvoiceResponse>> CancelInvoiceAsync(
        string? routeInvoiceRef,
        CancelInvoiceRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(routeInvoiceRef?.Trim(), request.InvoiceRef?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("Payment.InvoiceRefMismatch", "InvoiceRef trong route và body không khớp.");
        }

        var validationResult = request.Validate();
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        string fingerprint = PayloadFingerprinter.ComputeFingerprint(request);

        // 1. Check idempotency receipt first
        var existingReceipt = await _dbContext.OperationReceipts
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.OperationKey == request.OperationKey, ct);

        if (existingReceipt is not null)
        {
            if (string.Equals(existingReceipt.OperationType, "CancelInvoice", StringComparison.Ordinal)
                && string.Equals(existingReceipt.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
            {
                var parsed = JsonSerializer.Deserialize<CancelInvoiceResponse>(existingReceipt.ResultJson)!;
                return Result.Success(new CancelInvoiceResponse(
                    parsed.InvoiceRef,
                    parsed.Cancelled,
                    parsed.CancelledAt.ToUniversalTime(),
                    IsReplay: true));
            }

            return Error.Conflict("DUPLICATE_OPERATION_KEY", "OperationKey đã tồn tại với payload hoặc nghiệp vụ khác.");
        }

        // 2. Execute cancellation inside row-locked transaction
        await using var tx = await _dbContext.Database.BeginTransactionAsync(ct);
        try
        {
            var invoice = await FindInvoiceWithRowLockAsync(request.InvoiceRef, ct);
            if (invoice is null)
            {
                await tx.RollbackAsync(ct);
                return Error.NotFound("INVOICE_NOT_FOUND", "Không tìm thấy hóa đơn cần hủy.");
            }

            if (invoice.RentalOrderId != request.RentalOrderId)
            {
                await tx.RollbackAsync(ct);
                return Error.NotFound("INVOICE_NOT_FOUND", "Hóa đơn không thuộc RentalOrderId tương ứng.");
            }

            var utcNow = _timeProvider.GetUtcNow().ToUniversalTime();

            // Check if deadline already passed for pending invoice
            if (invoice.Status == InvoiceStatus.Pending && utcNow >= invoice.PaymentDeadline)
            {
                invoice.Expire(utcNow);
            }

            CancelInvoiceResponse response;
            if (invoice.Status == InvoiceStatus.Paid)
            {
                await tx.RollbackAsync(ct);
                return Error.Conflict("INVOICE_ALREADY_PAID", "Hóa đơn đã được thanh toán, không thể hủy.");
            }
            else if (invoice.Status == InvoiceStatus.Cancelled)
            {
                response = new CancelInvoiceResponse(
                    invoice.InvoiceRef,
                    true,
                    (invoice.CancelledAt ?? utcNow).ToUniversalTime(),
                    IsReplay: false);
            }
            else if (invoice.Status == InvoiceStatus.Expired)
            {
                // Acknowledge closure with cancelled: true while retaining EXPIRED status
                response = new CancelInvoiceResponse(
                    invoice.InvoiceRef,
                    true,
                    utcNow,
                    IsReplay: false);
            }
            else
            {
                invoice.Cancel(request.Reason, utcNow);
                response = new CancelInvoiceResponse(
                    invoice.InvoiceRef,
                    true,
                    utcNow,
                    IsReplay: false);
            }

            var receipt = OperationReceipt.Create(
                request.OperationKey,
                "CancelInvoice",
                fingerprint,
                JsonSerializer.Serialize(response),
                invoice.InvoiceRef,
                invoice.RentalOrderId,
                utcNow);

            _dbContext.OperationReceipts.Add(receipt);
            await _dbContext.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Result.Success(response);
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(ct);
            _dbContext.ChangeTracker.Clear();

            if (IsUniqueViolation(ex, "IX_operation_receipts_OperationKey"))
            {
                var raced = await _dbContext.OperationReceipts
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.OperationKey == request.OperationKey, ct);

                if (raced is not null
                    && string.Equals(raced.OperationType, "CancelInvoice", StringComparison.Ordinal)
                    && string.Equals(raced.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    var parsed = JsonSerializer.Deserialize<CancelInvoiceResponse>(raced.ResultJson)!;
                    return Result.Success(new CancelInvoiceResponse(
                        parsed.InvoiceRef,
                        parsed.Cancelled,
                        parsed.CancelledAt.ToUniversalTime(),
                        IsReplay: true));
                }

                return Error.Conflict("DUPLICATE_OPERATION_KEY", "OperationKey đã tồn tại với payload hoặc nghiệp vụ khác.");
            }

            throw;
        }
    }

    public async Task<Result<GetInvoiceResponse>> GetInvoiceAsync(
        string invoiceRef,
        Guid callerRenterId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(invoiceRef))
        {
            return Error.NotFound("INVOICE_NOT_FOUND", "Không tìm thấy hóa đơn.");
        }

        var invoice = await _dbContext.Invoices
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.InvoiceRef == invoiceRef, ct);

        if (invoice is null || invoice.RenterId != callerRenterId)
        {
            return Error.NotFound("INVOICE_NOT_FOUND", "Không tìm thấy hóa đơn.");
        }

        var utcNow = _timeProvider.GetUtcNow().ToUniversalTime();

        // Expire on read if deadline has passed
        if (invoice.Status == InvoiceStatus.Pending && utcNow >= invoice.PaymentDeadline)
        {
            if (OnBeforeAcquireGetLockForTest is not null)
            {
                await OnBeforeAcquireGetLockForTest(ct);
            }

            await using var tx = await _dbContext.Database.BeginTransactionAsync(ct);
            var locked = await FindInvoiceWithRowLockAsync(invoiceRef, ct);
            if (locked is not null)
            {
                if (locked.Status == InvoiceStatus.Pending && utcNow >= locked.PaymentDeadline)
                {
                    locked.Expire(utcNow);
                    await _dbContext.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                }
                else
                {
                    await tx.RollbackAsync(ct);
                }

                invoice = locked;
            }
            else
            {
                await tx.RollbackAsync(ct);
            }
        }

        string? qrUrl = null;
        if (invoice.Status == InvoiceStatus.Pending && invoice.TotalAmount > 0m)
        {
            qrUrl = VietQrUrlGenerator.Generate(
                invoice.MerchantBank,
                invoice.MerchantAccount,
                invoice.TotalAmount,
                invoice.PaymentCode);
        }

        var response = new GetInvoiceResponse(
            invoice.InvoiceRef,
            invoice.TotalAmount,
            invoice.Currency,
            qrUrl,
            invoice.MerchantBank,
            invoice.MerchantAccount,
            invoice.PaymentCode,
            invoice.PaymentDeadline,
            invoice.Status);

        return Result.Success(response);
    }

    public async Task<Result> RecordPaymentReceivedAsync(
        string invoiceRef,
        string paymentRef,
        decimal amount,
        string currency,
        DateTimeOffset paidAt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(invoiceRef) || string.IsNullOrWhiteSpace(paymentRef))
        {
            return Error.Validation("Payment.InvalidInput", "InvoiceRef và PaymentRef không được rỗng.");
        }

        var amountCheck = VndAmountValidator.ValidateVndAmount(amount, "Số tiền thanh toán");
        if (amountCheck.IsFailure)
        {
            return amountCheck.Error;
        }

        string normalizedInvoiceRef = invoiceRef.Trim();
        string normalizedPaymentRef = paymentRef.Trim();
        DateTimeOffset utcPaidAt = paidAt.ToUniversalTime();

        await using var tx = await _dbContext.Database.BeginTransactionAsync(ct);
        try
        {
            var invoice = await FindInvoiceWithRowLockAsync(normalizedInvoiceRef, ct);
            if (invoice is null)
            {
                await tx.RollbackAsync(ct);
                return Error.NotFound("INVOICE_NOT_FOUND", "Không tìm thấy hóa đơn.");
            }

            // 1. Keep PAID replay check before any time/deadline validation
            if (invoice.Status == InvoiceStatus.Paid)
            {
                var replayResult = invoice.MarkPaid(normalizedPaymentRef, amount, currency, utcPaidAt);
                await tx.RollbackAsync(ct);
                return replayResult;
            }

            // 2. Check duplicate PaymentRef on other invoices
            bool duplicatePaymentRef = await _dbContext.Invoices
                .AsNoTracking()
                .AnyAsync(i => i.PaymentRef == normalizedPaymentRef && i.InvoiceRef != normalizedInvoiceRef, ct);

            if (duplicatePaymentRef)
            {
                await tx.RollbackAsync(ct);
                return Error.Conflict("DUPLICATE_PAYMENT_REF", "PaymentRef đã tồn tại trên một hóa đơn khác.");
            }

            // 3. If invoice is PENDING and current time is already at/past deadline, persist EXPIRED and do not emit InvoicePaid
            DateTimeOffset utcNow = _timeProvider.GetUtcNow().ToUniversalTime();
            if (invoice.Status == InvoiceStatus.Pending && utcNow >= invoice.PaymentDeadline)
            {
                invoice.Expire(utcNow);
                await _dbContext.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return Error.Conflict("INVOICE_EXPIRED", "Hóa đơn đã hết hạn, không thể thanh toán.");
            }

            bool wasPending = invoice.Status == InvoiceStatus.Pending;

            var markResult = invoice.MarkPaid(normalizedPaymentRef, amount, currency, utcPaidAt);
            if (markResult.IsFailure)
            {
                await tx.RollbackAsync(ct);
                return markResult;
            }

            if (wasPending)
            {
                Guid messageId = Guid.NewGuid();
                var eventPayload = new InvoicePaidPayload(
                    invoice.RentalOrderId,
                    invoice.RenterId,
                    invoice.InvoiceRef,
                    invoice.PaymentRef!,
                    (decimal)decimal.ToInt64(invoice.TotalAmount),
                    invoice.Currency,
                    invoice.PaidAt ?? utcPaidAt);

                var envelope = new PaymentEventEnvelope<InvoicePaidPayload>(
                    messageId,
                    "InvoicePaid",
                    1,
                    "payment-service",
                    invoice.RentalOrderId.ToString("D"),
                    utcPaidAt,
                    messageId.ToString("D"),
                    null,
                    eventPayload);

                var outbox = OutboxMessage.Create(
                    envelope.MessageId,
                    envelope.MessageType,
                    "RentalOrder",
                    envelope.AggregateId,
                    JsonSerializer.Serialize(envelope),
                    utcPaidAt);

                _dbContext.OutboxMessages.Add(outbox);
            }

            await _dbContext.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Result.Success();
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(ct);
            _dbContext.ChangeTracker.Clear();

            if (IsUniqueViolation(ex, "IX_invoices_PaymentRef"))
            {
                return Error.Conflict("DUPLICATE_PAYMENT_REF", "PaymentRef đã tồn tại trên một hóa đơn khác.");
            }

            throw;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<int> ExpirePendingInvoicesBatchAsync(
        int batchSize = 100,
        CancellationToken ct = default)
    {
        var utcNow = _timeProvider.GetUtcNow().ToUniversalTime();

        var candidateIds = await _dbContext.Invoices
            .AsNoTracking()
            .Where(i => i.Status == InvoiceStatus.Pending && i.PaymentDeadline <= utcNow)
            .Take(batchSize)
            .Select(i => i.Id)
            .ToListAsync(ct);

        if (candidateIds.Count == 0)
        {
            return 0;
        }

        int expiredCount = 0;
        await using var tx = await _dbContext.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var id in candidateIds)
            {
                Invoice? locked = null;
                if (_dbContext.Database.IsNpgsql())
                {
                    locked = await _dbContext.Invoices
                        .FromSqlInterpolated($"SELECT * FROM invoices WHERE \"Id\" = {id} FOR UPDATE SKIP LOCKED")
                        .FirstOrDefaultAsync(ct);
                }
                else
                {
                    locked = await _dbContext.Invoices
                        .FirstOrDefaultAsync(i => i.Id == id, ct);
                }

                if (locked is not null)
                {
                    await _dbContext.Entry(locked).ReloadAsync(ct);
                    if (locked.Status == InvoiceStatus.Pending && locked.PaymentDeadline <= utcNow)
                    {
                        locked.Expire(utcNow);
                        expiredCount++;
                    }
                }
            }

            await _dbContext.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return expiredCount;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task EnsureExpiredIfPastDeadlineAsync(string invoiceRef, DateTimeOffset utcNow, CancellationToken ct)
    {
        var snapshot = await _dbContext.Invoices
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.InvoiceRef == invoiceRef, ct);

        if (snapshot is not null && snapshot.Status == InvoiceStatus.Pending && utcNow >= snapshot.PaymentDeadline)
        {
            await using var tx = await _dbContext.Database.BeginTransactionAsync(ct);
            var locked = await FindInvoiceWithRowLockAsync(invoiceRef, ct);
            if (locked is not null && locked.Status == InvoiceStatus.Pending && utcNow >= locked.PaymentDeadline)
            {
                locked.Expire(utcNow);
                await _dbContext.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            else
            {
                await tx.RollbackAsync(ct);
            }
        }
    }

    private async Task<Invoice?> FindInvoiceWithRowLockAsync(string? invoiceRef, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(invoiceRef))
        {
            return null;
        }

        Invoice? locked;
        if (_dbContext.Database.IsNpgsql())
        {
            locked = await _dbContext.Invoices
                .FromSqlInterpolated($"SELECT * FROM invoices WHERE \"InvoiceRef\" = {invoiceRef} FOR UPDATE")
                .FirstOrDefaultAsync(ct);
        }
        else
        {
            locked = await _dbContext.Invoices
                .FirstOrDefaultAsync(i => i.InvoiceRef == invoiceRef, ct);
        }

        if (locked is not null)
        {
            await _dbContext.Entry(locked).ReloadAsync(ct);
        }

        return locked;
    }

    private static bool IsUniqueViolation(DbUpdateException ex, string constraintName)
    {
        if (ex.InnerException is PostgresException pgEx
            && pgEx.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return string.IsNullOrEmpty(constraintName) ||
                   string.Equals(pgEx.ConstraintName, constraintName, StringComparison.OrdinalIgnoreCase);
        }

        string msg = ex.InnerException?.Message ?? ex.Message;
        return msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }
}
