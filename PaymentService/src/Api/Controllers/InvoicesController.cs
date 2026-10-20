using Dprental.PaymentService.Api.Filters;
using Dprental.PaymentService.Api.Security;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Security.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Api.Controllers;

[ApiController]
[Route("api/v1/invoices")]
public sealed class InvoicesController : ControllerBase
{
    private const string InvoiceWriteScope = "payment.invoice.write";

    private readonly IInvoiceCreationService _creationService;
    private readonly IInvoiceLifecycleService _lifecycleService;
    private readonly PaymentJwtOptions _jwtOptions;

    public InvoicesController(
        IInvoiceCreationService creationService,
        IInvoiceLifecycleService lifecycleService,
        IOptions<PaymentJwtOptions> jwtOptions)
    {
        _creationService = creationService ?? throw new ArgumentNullException(nameof(creationService));
        _lifecycleService = lifecycleService ?? throw new ArgumentNullException(nameof(lifecycleService));
        _jwtOptions = jwtOptions?.Value ?? throw new ArgumentNullException(nameof(jwtOptions));
    }

    /// <summary>
    /// Tạo mới hoặc replay hóa đơn cho RentalOrder theo Payment Contract v1.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(typeof(CreateInvoiceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(CreateInvoiceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateInvoice(
        [FromBody] CreateInvoiceRequest request,
        CancellationToken cancellationToken)
    {

        var result = await _creationService.CreateInvoiceAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        if (result.Value.IsReplay)
        {
            return Ok(result.Value);
        }

        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>
    /// Hủy hóa đơn chưa thanh toán theo Payment Contract v1.
    /// </summary>
    [HttpPost("{invoiceRef}/cancel")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(CancelInvoiceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelInvoice(
        [FromRoute] string invoiceRef,
        [FromBody] CancelInvoiceRequest request,
        CancellationToken cancellationToken)
    {

        var result = await _lifecycleService.CancelInvoiceAsync(invoiceRef, request, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Đọc thông tin thanh toán và QR chuyển khoản của hóa đơn dành cho Renter.
    /// </summary>
    [HttpGet("{invoiceRef}")]
    [Authorize(Policy = "InvoiceRenterPolicy")]
    [ProducesResponseType(typeof(GetInvoiceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInvoice(
        [FromRoute] string invoiceRef,
        CancellationToken cancellationToken)
    {
        if (!User.HasClientAudience(_jwtOptions.ClientAudience) ||
            !User.TryGetSubjectUserId(out Guid renterId))
        {
            return Forbid();
        }

        var result = await _lifecycleService.GetInvoiceAsync(invoiceRef, renterId, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }
}
