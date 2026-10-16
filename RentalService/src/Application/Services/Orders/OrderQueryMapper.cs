using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Services.Orders;

/// <summary>
/// Phần dùng chung của API xem đơn: kiểm tra người gọi, chuẩn hóa phân trang và đổi entity sang response.
/// </summary>
internal static class OrderQueryMapper
{
    public const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public static Result<Guid> RequirePersonalCaller(IRentalCallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated)
        {
            return OrderQueryErrors.Unauthenticated;
        }

        if (caller.IsService || !caller.UserId.HasValue || caller.UserId.Value == Guid.Empty)
        {
            return OrderQueryErrors.PersonalAccountRequired;
        }

        return caller.UserId.Value;
    }

    /// <summary>page nhỏ hơn 1 được đổi thành 1; pageSize được giới hạn trong 1–100, giống API lịch thuê.</summary>
    public static (int Page, int PageSize) NormalizePaging(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));

    /// <summary>
    /// Đọc trạng thái lọc theo đúng dạng API trả ra (PENDING_PAYMENT), chấp nhận cả PendingPayment.
    /// Rỗng là không lọc; số hoặc tên lạ là lỗi.
    /// </summary>
    public static Result<TEnum?> ParseStatusFilter<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Success<TEnum?>(null);
        }

        string name = value.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        if (!name.All(char.IsAsciiLetter) || !Enum.TryParse(name, ignoreCase: true, out TEnum status))
        {
            return OrderQueryErrors.InvalidStatus;
        }

        return Result.Success<TEnum?>(status);
    }

    public static PagedResponse<T> ToPage<T>(IReadOnlyList<T> items, int page, int pageSize, int totalCount) =>
        new(items, page, pageSize, totalCount, totalCount == 0 ? 0 : (int)Math.Ceiling((double)totalCount / pageSize));

    public static OrderDeliveryResponse ToDelivery(RentalOrder order) => new(
        order.DeliveryMethod,
        order.RecipientName,
        order.RecipientPhone,
        order.DeliveryAddress,
        order.DeliveryNote);

    public static OrderItemResponse ToItem(RentalItem item)
    {
        // Đơn lưu ngày trả máy (không tính); trả ra ngày dùng cuối (có tính) như giỏ hàng.
        Result<(DateOnly StartDate, DateOnly EndDate)> dates =
            RentalInventoryDateAdapter.ToInventoryDates(item.ExpectedStartDate, item.ExpectedEndDate);
        DateOnly endDate = dates.IsSuccess ? dates.Value.EndDate : item.ExpectedEndDate;

        return new OrderItemResponse(
            item.ListingTitleSnapshot,
            item.Quantity,
            item.ExpectedStartDate,
            endDate);
    }

    /// <summary>Các dòng theo thứ tự ngày giao rồi mã dòng, để kết quả ổn định giữa các lần gọi.</summary>
    public static List<OrderItemResponse> ToItems(IEnumerable<RentalItem> items) => items
        .OrderBy(i => i.ExpectedStartDate)
        .ThenBy(i => i.Id)
        .Select(ToItem)
        .ToList();
}
