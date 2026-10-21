namespace Dprental.PaymentService.Domain.Entities;

public static class InvoiceCancellationOrigins
{
    public const string Timeout = "TIMEOUT";
    public const string CustomerRequested = "CUSTOMER_REQUESTED";
    public const string Unknown = "UNKNOWN";

    public static bool IsValid(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;
        string upper = origin.Trim().ToUpperInvariant();
        return upper is Timeout or CustomerRequested;
    }

    public static string Normalize(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return Unknown;
        }

        string upper = origin.Trim().ToUpperInvariant();
        return upper switch
        {
            Timeout => Timeout,
            CustomerRequested => CustomerRequested,
            _ => Unknown
        };
    }
}
