using System.Globalization;

namespace Dprental.PaymentService.Application.Utils;

public static class VietQrUrlGenerator
{
    public static string Generate(string bank, string account, decimal amount, string transferContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bank);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(transferContent);

        string encodedAcc = Uri.EscapeDataString(account);
        string encodedBank = Uri.EscapeDataString(bank);
        string formattedAmount = amount.ToString("0", CultureInfo.InvariantCulture);
        string encodedDes = Uri.EscapeDataString(transferContent);

        return $"https://vietqr.app/img?acc={encodedAcc}&bank={encodedBank}&amount={formattedAmount}&des={encodedDes}&template=compact";
    }
}
