using Dprental.PaymentService.Api.Filters;
using Dprental.PaymentService.Api.Security;
using Dprental.PaymentService.Application.DTOs.SePay;
using Dprental.PaymentService.Application.IServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.PaymentService.Api.Controllers;

[ApiController]
[Route("api/v1/payment/webhooks")]
[Authorize(AuthenticationSchemes = SePayAuthenticationDefaults.AuthenticationScheme)]
public sealed class PaymentWebhooksController : ControllerBase
{
    private readonly IInvoicePaymentProcessor _processor;

    public PaymentWebhooksController(IInvoicePaymentProcessor processor)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
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
        var result = await _processor.ProcessAsync(payload, cancellationToken);

        return result.IsFailure
            ? this.ToProblem(result.Error)
            : Ok(new { success = true });
    }
}
