using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dprental.PaymentService.Common.Utils;

public static class PayloadFingerprinter
{
    public static string ComputeCreateFingerprint(
        Guid rentalOrderId,
        string orderCode,
        Guid renterId,
        DateTimeOffset paymentDeadline,
        IEnumerable<(Guid RentalItemId, Guid SupplierId, decimal RentalAmount, decimal DepositAmount)>? items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderCode);

        StringBuilder sb = new();
        sb.Append("CREATE:");
        sb.Append(rentalOrderId.ToString("D")).Append('|');
        sb.Append(orderCode.Trim()).Append('|');
        sb.Append(renterId.ToString("D")).Append('|');
        sb.Append("VND|");
        sb.Append(paymentDeadline.ToUniversalTime().ToString("O")).Append('|');

        if (items is not null)
        {
            var sorted = items
                .OrderBy(i => i.RentalItemId)
                .ToList();

            foreach (var item in sorted)
            {
                sb.Append(item.RentalItemId.ToString("D")).Append(':')
                  .Append(item.SupplierId.ToString("D")).Append(':')
                  .Append(decimal.Truncate(item.RentalAmount).ToString("0", CultureInfo.InvariantCulture)).Append(':')
                  .Append(decimal.Truncate(item.DepositAmount).ToString("0", CultureInfo.InvariantCulture)).Append(';');
            }
        }

        return ComputeSha256Hex(sb.ToString());
    }

    public static string ComputeFingerprint(
        Guid rentalOrderId,
        string orderCode,
        Guid renterId,
        DateTimeOffset paymentDeadline,
        IEnumerable<(Guid RentalItemId, Guid SupplierId, decimal RentalAmount, decimal DepositAmount)>? items)
        => ComputeCreateFingerprint(rentalOrderId, orderCode, renterId, paymentDeadline, items);

    public static string ComputeCancelFingerprint(
        Guid rentalOrderId,
        string invoiceRef,
        string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceRef);
        ArgumentNullException.ThrowIfNull(reason);

        StringBuilder sb = new();
        sb.Append("CANCEL:");
        sb.Append(rentalOrderId.ToString("D")).Append('|');
        sb.Append(invoiceRef.Trim()).Append('|');
        sb.Append(reason.Trim());

        return ComputeSha256Hex(sb.ToString());
    }

    public static string ComputeFingerprint(
        Guid rentalOrderId,
        string invoiceRef,
        string reason)
        => ComputeCancelFingerprint(rentalOrderId, invoiceRef, reason);

    private static string ComputeSha256Hex(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
