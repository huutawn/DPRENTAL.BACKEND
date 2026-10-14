using System.Globalization;
using System.Text.RegularExpressions;
using Dprental.RentalService.Application.DTOs.SupplierStatistics;
using Dprental.RentalService.Application.IServices.SupplierStatistics;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.SupplierStatistics;
/// <summary>
/// Thực thi truy vấn thống kê xác nhận của supplier từ Rental history cho Admin.
/// </summary>
public sealed partial class SupplierStatisticsService : ISupplierStatisticsService
{
    private readonly IRentalOrderRepository _rentalOrderRepository;

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex StrictIso8601Regex();

    private static readonly string[] AllowedFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.f'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fzzz",
        "yyyy-MM-dd'T'HH:mm:ss.ffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.fffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.ffffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.fffffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.ffffffzzz",
        "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz"
    ];

    public SupplierStatisticsService(IRentalOrderRepository rentalOrderRepository)
    {
        _rentalOrderRepository = rentalOrderRepository ?? throw new ArgumentNullException(nameof(rentalOrderRepository));
    }

    public async Task<Result<SupplierConfirmationStatisticsResponse>> GetConfirmationStatisticsAsync(
        SupplierConfirmationStatisticsRequest request,
        IRentalCallerContext callerContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callerContext);

        // 1. Kiểm tra quyền truy cập (Admin human)
        var authResult = RentalAuthorizationPolicy.AuthorizeAdminStatisticsRead(callerContext);
        if (authResult.IsFailure)
        {
            return authResult.Error;
        }

        // 2. Validate SupplierId
        if (request.SupplierId == Guid.Empty)
        {
            return Error.Validation("INVALID_SUPPLIER_ID", "Supplier ID không hợp lệ.");
        }

        // 3. Validate và parse thời gian
        var timeRangeResult = TryParseAndValidateUtcRange(request.FromUtc, request.ToUtc);
        if (timeRangeResult.IsFailure)
        {
            return timeRangeResult.Error;
        }

        var (fromUtc, toUtc) = timeRangeResult.Value;

        // 4. Query dữ liệu lịch sử từ repository
        var stats = await _rentalOrderRepository.GetSupplierConfirmationStatisticsAsync(
            request.SupplierId,
            fromUtc,
            toUtc,
            cancellationToken);

        // 5. Trả response an toàn cho Admin
        return new SupplierConfirmationStatisticsResponse(
            request.SupplierId,
            fromUtc,
            toUtc,
            stats.AcceptedCount,
            stats.RejectedCount,
            stats.ConfirmationTimeoutCount,
            stats.CancelAfterAcceptCount);
    }

    private static Result<(DateTimeOffset FromUtc, DateTimeOffset ToUtc)> TryParseAndValidateUtcRange(
        string? fromRaw,
        string? toRaw)
    {
        if (string.IsNullOrWhiteSpace(fromRaw) || string.IsNullOrWhiteSpace(toRaw))
        {
            return Error.Validation("MISSING_DATE_RANGE", "fromUtc và toUtc là các tham số bắt buộc.");
        }

        if (!TryParseStrictIso8601(fromRaw, out var fromUtc))
        {
            return Error.Validation("INVALID_DATETIME_FORMAT", "fromUtc bắt buộc định dạng ISO 8601 hợp lệ kèm timezone (Z hoặc ±HH:mm).");
        }

        if (!TryParseStrictIso8601(toRaw, out var toUtc))
        {
            return Error.Validation("INVALID_DATETIME_FORMAT", "toUtc bắt buộc định dạng ISO 8601 hợp lệ kèm timezone (Z hoặc ±HH:mm).");
        }

        if (fromUtc >= toUtc)
        {
            return Error.Validation("INVALID_TIME_RANGE", "fromUtc phải nhỏ hơn toUtc.");
        }

        return (fromUtc, toUtc);
    }

    private static bool TryParseStrictIso8601(string input, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string trimmed = input.Trim();
        string normalized = NormalizeIsoString(trimmed);

        if (!StrictIso8601Regex().IsMatch(normalized))
        {
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                normalized,
                AllowedFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return false;
        }

        result = parsed.ToUniversalTime();
        return true;
    }

    private static string NormalizeIsoString(string input)
    {
        if (input.Length <= 10) return input;
        var chars = input.ToCharArray();
        if (chars[10] == 't' || chars[10] == 'T')
        {
            chars[10] = 'T';
        }
        if (chars[^1] == 'z' || chars[^1] == 'Z')
        {
            chars[^1] = 'Z';
        }
        return new string(chars);
    }
}
