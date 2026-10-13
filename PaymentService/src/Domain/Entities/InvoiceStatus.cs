namespace Dprental.PaymentService.Domain.Entities;

public static class InvoiceStatus
{
    public const string Pending = "PENDING";
    public const string Paid = "PAID";
    public const string Expired = "EXPIRED";
    public const string Cancelled = "CANCELLED";

    public static readonly string[] All = [Pending, Paid, Expired, Cancelled];
}
