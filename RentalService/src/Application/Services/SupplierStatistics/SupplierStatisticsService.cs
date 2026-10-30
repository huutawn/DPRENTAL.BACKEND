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

    // Sinh regex yêu cầu timestamp đầy đủ với timezone rõ ràng, tránh parser chấp nhận ngày/giờ mơ hồ.
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

    // Khởi tạo các dependency và cấu hình dùng cho service; kiểm tra dependency bắt buộc ngay khi dựng đối tượng.
    public SupplierStatisticsService(IRentalOrderRepository rentalOrderRepository)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        _rentalOrderRepository = rentalOrderRepository ?? throw new ArgumentNullException(nameof(rentalOrderRepository));
    }

    // Kiểm tra quyền đọc thống kê admin, supplier và khoảng UTC trước khi lấy bốn chỉ số từ repository.
    public async Task<Result<SupplierConfirmationStatisticsResponse>> GetConfirmationStatisticsAsync(
        SupplierConfirmationStatisticsRequest request,
        IRentalCallerContext callerContext,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callerContext);

        // 1. Kiểm tra quyền truy cập (Admin human)
        var authResult = RentalAuthorizationPolicy.AuthorizeAdminStatisticsRead(callerContext);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
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
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (timeRangeResult.IsFailure)
        {
            return timeRangeResult.Error;
        }

        // Dùng cặp mốc đã parse/validate cho truy vấn thống kê; không parse lại theo locale khác ở repository.
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

    // Yêu cầu đủ hai mốc ISO có timezone và from nhỏ hơn to, sau đó trả cặp UTC cho truy vấn.
    private static Result<(DateTimeOffset FromUtc, DateTimeOffset ToUtc)> TryParseAndValidateUtcRange(
        string? fromRaw,
        string? toRaw)
    {
        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (string.IsNullOrWhiteSpace(fromRaw) || string.IsNullOrWhiteSpace(toRaw))
        {
            return Error.Validation("MISSING_DATE_RANGE", "fromUtc và toUtc là các tham số bắt buộc.");
        }

        // Từ chối dữ liệu không parse được theo định dạng yêu cầu, không tự sửa thành một giá trị hợp lệ giả định.
        if (!TryParseStrictIso8601(fromRaw, out var fromUtc))
        {
            return Error.Validation("INVALID_DATETIME_FORMAT", "fromUtc bắt buộc định dạng ISO 8601 hợp lệ kèm timezone (Z hoặc ±HH:mm).");
        }

        // Từ chối dữ liệu không parse được theo định dạng yêu cầu, không tự sửa thành một giá trị hợp lệ giả định.
        if (!TryParseStrictIso8601(toRaw, out var toUtc))
        {
            return Error.Validation("INVALID_DATETIME_FORMAT", "toUtc bắt buộc định dạng ISO 8601 hợp lệ kèm timezone (Z hoặc ±HH:mm).");
        }

        // Dừng nhánh không hợp lệ với mã INVALID_TIME_RANGE; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (fromUtc >= toUtc)
        {
            return Error.Validation("INVALID_TIME_RANGE", "fromUtc phải nhỏ hơn toUtc.");
        }

        return (fromUtc, toUtc);
    }

    // Chuẩn hóa ký tự phân cách rồi parse đúng các format cho phép; đổi UTC để không phụ thuộc timezone server.
    private static bool TryParseStrictIso8601(string input, out DateTimeOffset result)
    {
        // Khởi tạo đầu ra parse chưa hợp lệ; chỉ gán giá trị có thể dùng khi toàn bộ bước parse đã thành công.
        result = default;
        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (string.IsNullOrWhiteSpace(input)) return false;

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string trimmed = input.Trim();
        string normalized = NormalizeIsoString(trimmed);

        // Điều kiện này quyết định có thể tiếp tục hay phải trả false theo contract của use case.
        if (!StrictIso8601Regex().IsMatch(normalized))
        {
            return false;
        }

        // Từ chối dữ liệu không parse được theo định dạng yêu cầu, không tự sửa thành một giá trị hợp lệ giả định.
        if (!DateTimeOffset.TryParseExact(
                normalized,
                AllowedFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return false;
        }

        // Chuẩn hóa thời điểm đã parse về UTC, tránh so sánh phụ thuộc offset hoặc timezone của server.
        result = parsed.ToUniversalTime();
        return true;
    }

    // Chuẩn hóa chữ T/Z mà không tự thêm timezone hoặc sửa nội dung thời điểm người dùng gửi.
    private static string NormalizeIsoString(string input)
    {
        // Điều kiện này quyết định có thể tiếp tục hay phải trả input theo contract của use case.
        if (input.Length <= 10) return input;
        var chars = input.ToCharArray();
        // Chuẩn hóa chữ T/Z theo ISO nhưng giữ nguyên ngày, giờ và offset gốc; không tự thêm timezone còn thiếu.
        if (chars[10] == 't' || chars[10] == 'T')
        {
            // Chuẩn hóa chữ T/Z theo ISO nhưng giữ nguyên ngày, giờ và offset gốc; không tự thêm timezone còn thiếu.
            chars[10] = 'T';
        }
        // Chuẩn hóa chữ T/Z theo ISO nhưng giữ nguyên ngày, giờ và offset gốc; không tự thêm timezone còn thiếu.
        if (chars[^1] == 'z' || chars[^1] == 'Z')
        {
            // Chuẩn hóa chữ T/Z theo ISO nhưng giữ nguyên ngày, giờ và offset gốc; không tự thêm timezone còn thiếu.
            chars[^1] = 'Z';
        }
        return new string(chars);
    }
}
