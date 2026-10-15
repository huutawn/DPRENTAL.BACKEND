using System.Text.Json.Serialization;
using Dprental.PaymentService.Api.Security;
using Dprental.PaymentService.Application.DTOs.Bank;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dprental.PaymentService.Api.Controllers;

public sealed record SePayWebhookPayload(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("gateway")] string? Gateway,
    [property: JsonPropertyName("transactionDate")] string? TransactionDate,
    [property: JsonPropertyName("accountNumber")] string? AccountNumber,
    [property: JsonPropertyName("subAccount")] string? SubAccount,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("transferType")] string? TransferType,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("transferAmount")] decimal? TransferAmount,
    [property: JsonPropertyName("accumulated")] decimal? Accumulated,
    [property: JsonPropertyName("referenceCode")] string? ReferenceCode);

[ApiController]
[Route("api/v1/payment/webhooks")]
[Authorize(AuthenticationSchemes = SePayAuthenticationDefaults.AuthenticationScheme)]
public sealed class PaymentWebhooksController : ControllerBase
{
    private readonly IInvoicePaymentProcessor _processor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PaymentWebhooksController> _logger;

    public PaymentWebhooksController(
        IInvoicePaymentProcessor processor,
        TimeProvider? timeProvider = null,
        ILogger<PaymentWebhooksController>? logger = null)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<PaymentWebhooksController>.Instance;
    }

    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ReceiveWebhook(
        [FromBody] SePayWebhookPayload? payload,
        CancellationToken cancellationToken)
    {
        if (payload is null)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid Payload",
                Detail = "Webhook payload cannot be null."
            });
        }

        if (!payload.Id.HasValue || payload.Id.Value <= 0)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid Id",
                Detail = "SePay transaction id must be a positive integer."
            });
        }

        if (!payload.TransferAmount.HasValue || payload.TransferAmount.Value <= 0 ||
            decimal.Floor(payload.TransferAmount.Value) != payload.TransferAmount.Value)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid TransferAmount",
                Detail = "Transfer amount must be a positive whole integer."
            });
        }

        if (string.IsNullOrWhiteSpace(payload.Gateway))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid Gateway",
                Detail = "Gateway cannot be empty."
            });
        }

        if (string.IsNullOrWhiteSpace(payload.AccountNumber))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid AccountNumber",
                Detail = "AccountNumber cannot be empty."
            });
        }

        if (string.IsNullOrWhiteSpace(payload.TransferType))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid TransferType",
                Detail = "TransferType cannot be empty."
            });
        }

        if (!SePayDateTimeParser.TryParseVietnamTransactionDate(payload.TransactionDate, out var utcTransactionDate))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid TransactionDate",
                Detail = "TransactionDate is required and must be a valid datetime."
            });
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow().ToUniversalTime();

        var verified = new VerifiedBankTransaction(
            Id: payload.Id.Value,
            Gateway: payload.Gateway.Trim(),
            TransactionDate: utcTransactionDate,
            AccountNumber: payload.AccountNumber.Trim(),
            SubAccount: payload.SubAccount?.Trim(),
            Code: payload.Code,
            Content: payload.Content,
            TransferType: payload.TransferType.Trim().ToLowerInvariant(),
            Description: payload.Description,
            TransferAmount: payload.TransferAmount.Value,
            Accumulated: payload.Accumulated,
            ReferenceCode: payload.ReferenceCode?.Trim(),
            ProcessedAt: utcNow);

        var result = await _processor.ProcessAsync(verified, cancellationToken);
        if (result.IsFailure)
        {
            if (result.Error.Type == ErrorType.Conflict)
            {
                return Conflict(new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = result.Error.Code,
                    Detail = result.Error.Message
                });
            }

            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = result.Error.Code,
                Detail = result.Error.Message
            });
        }

        return Ok(new { success = true });
    }
}
