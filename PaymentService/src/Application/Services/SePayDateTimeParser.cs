using System.Globalization;

namespace Dprental.PaymentService.Application.Services;

internal static class SePayDateTimeParser
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public static bool TryParseVietnamTransactionDate(string? input, out DateTimeOffset utcDate)
    {
        utcDate = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string trimmed = input.Trim();

        // 1. Exact SePay format: "yyyy-MM-dd HH:mm:ss"
        if (DateTime.TryParseExact(trimmed, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtExact))
        {
            utcDate = new DateTimeOffset(dtExact, VietnamOffset).ToUniversalTime();
            return true;
        }

        // 2. ISO 8601 formats
        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
        {
            if (dto.Offset == TimeSpan.Zero && !trimmed.EndsWith('Z') && !trimmed.EndsWith("+00:00", StringComparison.Ordinal) && !trimmed.Contains('+'))
            {
                // Unspecified timezone -> treat as Vietnam time (UTC+7)
                utcDate = new DateTimeOffset(dto.DateTime, VietnamOffset).ToUniversalTime();
            }
            else
            {
                utcDate = dto.ToUniversalTime();
            }
            return true;
        }

        // 3. Fallback general DateTime parse
        if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtFallback))
        {
            utcDate = new DateTimeOffset(dtFallback, VietnamOffset).ToUniversalTime();
            return true;
        }

        return false;
    }
}
