using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dprental.PaymentService.Application.DTOs.Invoice;

namespace Dprental.PaymentService.Application.Utils;

public static class PayloadFingerprinter
{
    public static string ComputeFingerprint(CreateInvoiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        StringBuilder sb = new();
        sb.Append("CREATE:");
        sb.Append(request.RentalOrderId.ToString("D")).Append('|');
        sb.Append(request.OrderCode.Trim()).Append('|');
        sb.Append(request.RenterId.ToString("D")).Append('|');
        sb.Append("VND|");
        sb.Append(request.PaymentDeadline.ToUniversalTime().ToString("O")).Append('|');

        if (request.Items is not null)
        {
            var sorted = request.Items
                .Where(i => i is not null)
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

    public static string ComputeFingerprint(CancelInvoiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        StringBuilder sb = new();
        sb.Append("CANCEL:");
        sb.Append(request.RentalOrderId.ToString("D")).Append('|');
        sb.Append(request.InvoiceRef.Trim()).Append('|');
        sb.Append(request.Reason.Trim());

        return ComputeSha256Hex(sb.ToString());
    }

    private static string ComputeSha256Hex(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
