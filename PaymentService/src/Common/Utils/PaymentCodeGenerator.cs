using System.Security.Cryptography;

namespace Dprental.PaymentService.Common.Utils;

/// <summary>
/// Sinh mã chuyển khoản chuẩn SePay/VietQR theo mẫu: {tiền tố} {10 ký tự alphabet không chứa 0/1/I/O}.
/// DPR cho tiền khách trả vào; DPO (payout supplier) và DRF (hoàn tiền renter) cho tiền admin chuyển ra.
/// </summary>
public static class PaymentCodeGenerator
{
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    public const int CodeLength = 10;
    public const string Prefix = "DPR ";
    public const string PayoutPrefix = "DPO ";
    public const string RefundPrefix = "DRF ";

    public static string Generate(string prefix = Prefix)
    {
        char[] chars = new char[CodeLength];
        for (int i = 0; i < CodeLength; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return $"{prefix}{new string(chars)}";
    }

    public static bool IsValid(string? paymentCode, string prefix = Prefix)
    {
        if (string.IsNullOrWhiteSpace(paymentCode) || !paymentCode.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string codePart = paymentCode[prefix.Length..];
        if (codePart.Length != CodeLength)
        {
            return false;
        }

        foreach (char c in codePart)
        {
            if (!Alphabet.Contains(c))
            {
                return false;
            }
        }

        return true;
    }
}
