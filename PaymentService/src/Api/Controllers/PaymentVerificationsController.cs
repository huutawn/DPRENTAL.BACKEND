using System.Globalization;
using System.Text;
using Dprental.PaymentService.Api.Filters;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Domain.IRepository;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.PaymentService.Api.Controllers;

[ApiController]
[Route("api/v1/payment")]
public sealed class PaymentVerificationsController : ControllerBase
{
    private readonly ITransactionIntegrityChecker _integrityChecker;
    private readonly ITransactionJournalRepository _journalRepository;

    public PaymentVerificationsController(
        ITransactionIntegrityChecker integrityChecker,
        ITransactionJournalRepository journalRepository)
    {
        _integrityChecker = integrityChecker ?? throw new ArgumentNullException(nameof(integrityChecker));
        _journalRepository = journalRepository ?? throw new ArgumentNullException(nameof(journalRepository));
    }

    /// <summary>
    /// Kiểm tra tính toàn vẹn mật mã và điều kiện sử dụng của giao dịch thanh toán.
    /// </summary>
    [HttpPost("verifications")]
    [Authorize(Policy = "TransactionVerifyPolicy")]
    [ProducesResponseType(typeof(TransactionVerificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(TransactionVerificationResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(TransactionVerificationResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Verify(
        [FromBody] TransactionVerificationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _integrityChecker.VerifyAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            if (result.Error.Code == "Payment.Verification.Timeout")
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "Payment.Verification.Timeout",
                    Detail = result.Error.Message
                });
            }

            return this.ToProblem(result.Error);
        }

        var response = result.Value;
        if (response.Integrity.Status == IntegrityStatuses.PendingConfirmation)
        {
            return StatusCode(StatusCodes.Status202Accepted, response);
        }

        if (response.Integrity.Status == IntegrityStatuses.Tampered)
        {
            return StatusCode(StatusCodes.Status409Conflict, response);
        }

        return Ok(response);
    }

    /// <summary>
    /// Tra cứu lịch sử journal phân trang theo cursor cho service hoặc admin.
    /// </summary>
    [HttpGet("history")]
    [Authorize(Policy = "TransactionHistoryPolicy")]
    [ProducesResponseType(typeof(CursorPaginatedHistoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetHistory(
        [FromQuery] string? streamId,
        [FromQuery] string? paymentRef,
        [FromQuery] string? invoiceRef,
        [FromQuery] string? cursor,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        int effectiveLimit = Math.Clamp(limit, 1, 100);

        DateTimeOffset? cursorTimestamp = null;
        Guid? cursorId = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!TryDecodeCursor(cursor, out var decodedTime, out var decodedId))
            {
                return BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Payment.History.InvalidCursor",
                    Detail = "Cursor phân trang không hợp lệ."
                });
            }

            cursorTimestamp = decodedTime;
            cursorId = decodedId;
        }

        var results = await _journalRepository.QueryHistoryAsync(
            streamId,
            paymentRef,
            invoiceRef,
            cursorTimestamp,
            cursorId,
            effectiveLimit + 1,
            cancellationToken);

        bool hasMore = results.Count > effectiveLimit;
        var items = results.Take(effectiveLimit).Select(j => new JournalHistoryItemDto(
            j.Id,
            j.StreamId,
            j.StreamType,
            j.Version,
            j.EventId,
            j.EventType,
            j.Amount,
            j.Currency,
            j.Status,
            j.ConfirmationSource,
            j.Actor,
            j.TimestampUtc,
            j.PaymentRef,
            j.InvoiceRef,
            j.RentalOrderId,
            j.RentalItemId,
            j.IsConfirmed,
            j.ConfirmedAt,
            j.ProofSignature,
            j.SigningKeyId,
            j.Fingerprint,
            j.EntryHash)).ToList();

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
        {
            var last = items[^1];
            nextCursor = EncodeCursor(last.TimestampUtc, last.Id);
        }

        return Ok(new CursorPaginatedHistoryResponse(items, nextCursor, hasMore));
    }

    private static string EncodeCursor(DateTimeOffset timestamp, Guid id)
    {
        string raw = $"{timestamp.ToUniversalTime().Ticks}:{id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    private static bool TryDecodeCursor(string cursor, out DateTimeOffset timestamp, out Guid id)
    {
        timestamp = default;
        id = default;

        try
        {
            byte[] bytes = Convert.FromBase64String(cursor);
            string raw = Encoding.UTF8.GetString(bytes);
            int splitIdx = raw.IndexOf(':');
            if (splitIdx < 1) return false;

            if (!long.TryParse(raw[..splitIdx], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
                return false;

            if (!Guid.TryParse(raw[(splitIdx + 1)..], out id))
                return false;

            timestamp = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
