using System.Globalization;
using System.Text.RegularExpressions;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Calendar;
using Dprental.RentalService.Application.IServices.Calendar;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Calendar;

/// <summary>
/// Dịch vụ xử lý truy vấn lịch thuê cá nhân của renter (/api/v1/rental/me/calendar).
/// </summary>
public sealed class RentalCalendarService : IRentalCalendarService
{
    private static readonly Regex MonthRegex = new(@"^(\d{4})-(0[1-9]|1[0-2])$", RegexOptions.Compiled);
    private static readonly RentalItemStatus[] TargetStatuses =
    [
        RentalItemStatus.AwaitingHandover,
        RentalItemStatus.InRental,
        RentalItemStatus.PendingSettlement,
        RentalItemStatus.Completed
    ];

    private readonly IRentalOrderRepository _orderRepository;
    private readonly TimeProvider _timeProvider;

    // Khởi tạo các dependency và cấu hình dùng cho service; kiểm tra dependency bắt buộc ngay khi dựng đối tượng.
    public RentalCalendarService(
        IRentalOrderRepository orderRepository,
        TimeProvider? timeProvider = null)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // Xác thực người thuê, xác định tháng theo giờ nghiệp vụ, tổng hợp số máy theo ngày rồi phân trang các mục thuê.
    public async Task<Result<RenterCalendarResponse>> GetRenterCalendarAsync(
        string? month,
        int page,
        int pageSize,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(caller);

        // Kiểm tra danh tính và loại caller trước khi dùng user ID để xác định phạm vi dữ liệu được phép truy cập.
        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("Auth.Unauthorized", "Yêu cầu xác thực danh tính.");
        }

        // Kiểm tra danh tính và loại caller trước khi dùng user ID để xác định phạm vi dữ liệu được phép truy cập.
        if (caller.IsService || !caller.UserId.HasValue || caller.UserId.Value == Guid.Empty)
        {
            return Error.Forbidden("Auth.Forbidden", "Chỉ người dùng cá nhân mới có quyền truy cập lịch thuê cá nhân.");
        }

        // Giữ định danh/vai trò đã xác định cho thao tác để audit và các bước tiếp theo dùng cùng chủ thể.
        Guid renterId = caller.UserId.Value;

        // Đọc năm/tháng từ tham số đã kiểm tra định dạng; không dùng timezone mặc định của máy chủ để đoán tháng.
        int year;
        int monthNum;

        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (string.IsNullOrWhiteSpace(month))
        {
            // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
            DateOnly defaultMonthDate = RentalInventoryDateAdapter.ToBusinessDate(_timeProvider.GetUtcNow());
            year = defaultMonthDate.Year;
            monthNum = defaultMonthDate.Month;
        }
        else
        {
            // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
            var match = MonthRegex.Match(month.Trim());
            // Từ chối dữ liệu không parse được theo định dạng yêu cầu, không tự sửa thành một giá trị hợp lệ giả định.
            if (!match.Success)
            {
                return Error.Validation("INVALID_MONTH_FORMAT", "Định dạng tháng không hợp lệ. Vui lòng sử dụng định dạng YYYY-MM.");
            }

            // Đọc năm/tháng từ tham số đã kiểm tra định dạng; không dùng timezone mặc định của máy chủ để đoán tháng.
            year = int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
            monthNum = int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture);

            // Dừng nhánh không hợp lệ với mã INVALID_MONTH_FORMAT; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
            if (year < 1 || year > 9999 || monthNum < 1 || monthNum > 12)
            {
                return Error.Validation("INVALID_MONTH_FORMAT", "Giá trị tháng hoặc năm không hợp lệ.");
            }
        }

        // Tính khoảng ngày hoặc số tiền từ dữ liệu của bước hiện tại; giữ công thức/chính sách đang được use case sử dụng.
        int daysInMonth = DateTime.DaysInMonth(year, monthNum);
        DateOnly monthStart = new(year, monthNum, 1);
        DateOnly monthEnd = new(year, monthNum, daysInMonth);

        // Query dữ liệu Rental thông qua repository contract
        var matchingItems = await _orderRepository.GetRenterCalendarItemsAsync(
            renterId,
            monthStart,
            monthEnd,
            TargetStatuses,
            cancellationToken);

        // Thống kê days: đủ ngày trong tháng, rentedQuantity tính trước pagination
        var days = new List<RenterCalendarDayResponse>(daysInMonth);
        // Duyệt tập dữ liệu của bước hiện tại để tích lũy kết quả, giữ nguyên phạm vi và thứ tự mà caller đã chọn.
        for (int day = 1; day <= daysInMonth; day++)
        {
            // Dựng từng ngày của tháng để cộng số lượng thuê giao với ngày đó theo quy ước khoảng thuê của truy vấn.
            DateOnly d = new(year, monthNum, day);
            int dayRentedQty = matchingItems
                .Where(i => i.ExpectedStartDate <= d && i.ExpectedEndDate > d)
                .Sum(i => i.Quantity);

            // Dựng response/thông báo từ dữ liệu vừa xử lý; không tự thay đổi trạng thái nghiệp vụ trong bước ánh xạ.
            days.Add(new RenterCalendarDayResponse(d, dayRentedQty));
        }

        // Pagination trên entries
        int normalizedPage = Math.Max(1, page);
        int normalizedPageSize = Math.Clamp(pageSize, 1, 100);
        int totalCount = matchingItems.Count;
        int totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling((double)totalCount / normalizedPageSize);

        // Phân trang danh sách item sau khi đã tổng hợp lịch đủ cả tháng; đổi trang không được làm thay đổi tổng số máy của từng ngày.
        var pagedItems = matchingItems
            .OrderBy(i => i.ExpectedStartDate)
            .ThenBy(i => i.RentalItemId)
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToList();

        // Tích lũy response theo các phần tử đã kiểm tra; danh sách này chỉ phục vụ kết quả của lần xử lý hiện tại.
        var entries = new List<RenterCalendarEntryResponse>(pagedItems.Count);
        // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
        foreach (var item in pagedItems)
        {
            // Chuyển mốc ngày qua adapter/policy hiện có để Rental và Inventory dùng đúng quy ước khoảng thuê.
            var dateResult = RentalInventoryDateAdapter.ToInventoryDates(item.ExpectedStartDate, item.ExpectedEndDate);
            DateOnly startDate = dateResult.IsSuccess ? dateResult.Value.StartDate : item.ExpectedStartDate;
            DateOnly endDate = dateResult.IsSuccess ? dateResult.Value.EndDate : item.ExpectedEndDate;

            // Tích lũy response theo các phần tử đã kiểm tra; danh sách này chỉ phục vụ kết quả của lần xử lý hiện tại.
            entries.Add(new RenterCalendarEntryResponse(
                item.OrderId,
                item.OrderCode,
                item.RentalItemId,
                item.EquipmentListingId,
                item.ListingTitle,
                item.Quantity,
                startDate,
                endDate,
                item.Status.ToString()));
        }

        return Result.Success(new RenterCalendarResponse(
            $"{year:D4}-{monthNum:D2}",
            days,
            entries,
            normalizedPage,
            normalizedPageSize,
            totalCount,
            totalPages));
    }
}
