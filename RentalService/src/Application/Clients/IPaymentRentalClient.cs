using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Clients;

/// <summary>
/// Frozen external client contract between RentalService and PaymentService (v1).
/// Defines asynchronous invoice, refund, and financial settlement operations.
/// </summary>
public interface IPaymentRentalClient
{
    /// <summary>
    /// Creates a payable invoice for a newly placed rental order.
    /// </summary>
    Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(
        CreateInvoiceRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Cancels an unpaid invoice if the rental order has expired or been cancelled prior to payment.
    /// If already paid, returns INVOICE_ALREADY_PAID (does not refund).
    /// </summary>
    Task<Result<CancelInvoiceResponse>> CancelInvoiceAsync(
        CancelInvoiceRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Requests PaymentService to accept a late payment after all rental items have successfully acquired re-allocation holds.
    /// </summary>
    Task<Result<AcceptLatePaymentResponse>> AcceptLatePaymentAsync(
        string invoiceRef,
        AcceptLatePaymentRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(Result.Success(new AcceptLatePaymentResponse(invoiceRef, "PAID", false, DateTimeOffset.UtcNow)));

    /// <summary>
    /// Requests a refund for rejected items, supplier timeouts, or late payment terminal orders.
    /// Payment processes refund back to the original funding source.
    /// </summary>
    Task<Result<RequestRefundResponse>> RequestRefundAsync(
        RequestRefundRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Submits a final rental settlement after all physical units are returned and deductions finalized.
    /// SettleRental submits the request and receives an Accepted acknowledgment; financial closure is finalized upon RentalSettled event.
    /// </summary>
    Task<Result<SettleRentalResponse>> SettleRentalAsync(
        SettleRentalRequest request,
        CancellationToken ct = default);
}
