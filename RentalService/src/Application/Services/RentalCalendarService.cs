using System.Globalization;
using System.Text.RegularExpressions;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Calendar;
using Dprental.RentalService.Application.IServices;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Application.Services;

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

    private readonly RentalServiceDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public RentalCalendarService(
        RentalServiceDbContext dbContext,
        TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<RenterCalendarResponse>> GetRenterCalendarAsync(
        string? month,
        int page,
        int pageSize,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("Auth.Unauthorized", "Yêu cầu xác thực danh tính.");
        }

        if (caller.IsService || !caller.UserId.HasValue || caller.UserId.Value == Guid.Empty)
        {
            return Error.Forbidden("Auth.Forbidden", "Chỉ người dùng cá nhân mới có quyền truy cập lịch thuê cá nhân.");
        }

        Guid renterId = caller.UserId.Value;

        int year;
        int monthNum;

        if (string.IsNullOrWhiteSpace(month))
        {
            DateOnly defaultMonthDate = RentalInventoryDateAdapter.ToBusinessDate(_timeProvider.GetUtcNow());
            year = defaultMonthDate.Year;
            monthNum = defaultMonthDate.Month;
        }
        else
        {
            var match = MonthRegex.Match(month.Trim());
            if (!match.Success)
            {
                return Error.Validation("INVALID_MONTH_FORMAT", "Định dạng tháng không hợp lệ. Vui lòng sử dụng định dạng YYYY-MM.");
            }

            year = int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
            monthNum = int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture);

            if (year < 1 || year > 9999 || monthNum < 1 || monthNum > 12)
            {
                return Error.Validation("INVALID_MONTH_FORMAT", "Giá trị tháng hoặc năm không hợp lệ.");
            }
        }

        int daysInMonth = DateTime.DaysInMonth(year, monthNum);
        DateOnly monthStart = new(year, monthNum, 1);
        DateOnly monthEnd = new(year, monthNum, daysInMonth);

        // Query trực tiếp dữ liệu Rental với ownership predicate (RenterId == renterId)
        var query = from order in _dbContext.RentalOrders.AsNoTracking()
                    where order.RenterId == renterId
                    from item in order.Items
                    where TargetStatuses.Contains(item.Status)
                          && item.ExpectedStartDate <= monthEnd
                          && item.ExpectedEndDate > monthStart
                    select new
                    {
                        OrderId = order.Id,
                        OrderCode = order.OrderCode,
                        RentalItemId = item.Id,
                        EquipmentListingId = item.EquipmentListingId,
                        ListingTitle = item.ListingTitleSnapshot,
                        Quantity = item.Quantity,
                        ExpectedStartDate = item.ExpectedStartDate,
                        ExpectedEndDate = item.ExpectedEndDate,
                        Status = item.Status,
                        CreatedAt = order.CreatedAt
                    };

        var matchingItems = await query.ToListAsync(cancellationToken);

        // Thống kê days: đủ ngày trong tháng, rentedQuantity tính trước pagination
        var days = new List<RenterCalendarDayResponse>(daysInMonth);
        for (int day = 1; day <= daysInMonth; day++)
        {
            DateOnly d = new(year, monthNum, day);
            int dayRentedQty = matchingItems
                .Where(i => i.ExpectedStartDate <= d && i.ExpectedEndDate > d)
                .Sum(i => i.Quantity);

            days.Add(new RenterCalendarDayResponse(d, dayRentedQty));
        }

        // Pagination trên entries
        int normalizedPage = Math.Max(1, page);
        int normalizedPageSize = Math.Clamp(pageSize, 1, 100);
        int totalCount = matchingItems.Count;
        int totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling((double)totalCount / normalizedPageSize);

        var pagedItems = matchingItems
            .OrderBy(i => i.ExpectedStartDate)
            .ThenBy(i => i.RentalItemId)
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToList();

        var entries = new List<RenterCalendarEntryResponse>(pagedItems.Count);
        foreach (var item in pagedItems)
        {
            var dateResult = RentalInventoryDateAdapter.ToInventoryDates(item.ExpectedStartDate, item.ExpectedEndDate);
            DateOnly startDate = dateResult.IsSuccess ? dateResult.Value.StartDate : item.ExpectedStartDate;
            DateOnly endDate = dateResult.IsSuccess ? dateResult.Value.EndDate : item.ExpectedEndDate;

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
