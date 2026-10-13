using System.Security.Cryptography;

namespace Dprental.PaymentService.Domain.Utils;

/// <summary>
/// Sinh mã thanh toán chuẩn SePay/VietQR theo mẫu: DPR {10 ký tự alphabet không chứa 0/1/I/O}.
/// </summary>
public static class PaymentCodeGenerator
{
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    public const int CodeLength = 10;
    public const string Prefix = "DPR ";

    public static string Generate()
    {
        char[] chars = new char[CodeLength];
        for (int i = 0; i < CodeLength; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return $"{Prefix}{new string(chars)}";
    }

    public static bool IsValid(string? paymentCode)
    {
        if (string.IsNullOrWhiteSpace(paymentCode) || !paymentCode.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string codePart = paymentCode[Prefix.Length..];
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
