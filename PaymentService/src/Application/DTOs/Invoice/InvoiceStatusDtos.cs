namespace Dprental.PaymentService.Application.DTOs.Invoice;

public sealed record InvoiceStatusItem(
    Guid RentalItemId, decimal RentalAmount, decimal DepositAmount, decimal TotalAmount, string? SettlementStatus);

public sealed record InvoiceStatusResponse(
    string InvoiceRef, Guid RentalOrderId, string Status,
    DateTimeOffset PaymentDeadline, DateTimeOffset? PaidAt,
    decimal RentalAmount, decimal DepositAmount, decimal TotalAmount,
    IReadOnlyList<InvoiceStatusItem> Items);
