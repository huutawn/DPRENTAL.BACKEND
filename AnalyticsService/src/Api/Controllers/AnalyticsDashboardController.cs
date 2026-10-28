using Dprental.AnalyticsService.Api.Security;
using Dprental.AnalyticsService.Application.Dashboard;
using Dprental.AnalyticsService.Application.Dashboard.Revenue;
using Dprental.AnalyticsService.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AnalyticsService.Api.Controllers;

/// <summary>Thống kê quản trị và nhà cung cấp từ read model của Rental và Payment.</summary>
[ApiController]
[Authorize]
[Route("api/v1/analytics")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class AnalyticsDashboardController(
    DashboardQueryService dashboard,
    OrderTrendQueryService orderTrend,
    RevenueAnalyticsQueryService revenue) : ControllerBase
{
    /// <summary>Tổng quan KPI theo ngày Việt Nam.</summary>
    /// <remarks>Active là số đơn còn hoạt động tại cuối ngày to. PendingRefund là null cho đến khi có event nghĩa vụ hoàn tiền. GMV không gồm tiền cọc; phí nền tảng chỉ lấy từ settlement Payment.</remarks>
    [Authorize(Policy = AnalyticsJwtAuthentication.AdminPolicy)]
    [HttpGet("overview")]
    [ProducesResponseType(typeof(OverviewResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OverviewResponse>> Overview(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        if (from is null && to is null)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
            to = today;
            from = today.AddDays(-29);
        }
        if (!TryRange(from, to, out var range)) return InvalidRange();
        return Ok(await dashboard.OverviewAsync(range!, null, ct));
    }

    /// <summary>GMV, phí nền tảng, tiền hoàn và tiền chi nhà cung cấp theo ngày, tuần hoặc tháng Việt Nam.</summary>
    /// <remarks>GMV dùng ngày thanh toán, phí dùng ngày settlement, hoàn tiền và payout dùng ngày hoàn tất. Tiền cọc không thuộc GMV hoặc phí nền tảng. Tuần bắt đầu thứ Hai; bucket trống bằng 0.</remarks>
    [Authorize(Policy = AnalyticsJwtAuthentication.AdminPolicy)]
    [HttpGet("revenue")]
    [ProducesResponseType(typeof(RevenueAnalyticsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RevenueAnalyticsResponse>> Revenue(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string granularity = "day", CancellationToken ct = default)
    {
        if (from is null && to is null)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
            to = today;
            from = today.AddDays(-29);
        }
        if (!TryRange(from, to, out var range)) return InvalidRange();
        if (!RevenueGranularityParser.TryParse(granularity, out var unit))
        {
            return Problem(statusCode: 400, title: "INVALID_GRANULARITY", detail: "granularity phải là day, week hoặc month.");
        }
        return Ok(await revenue.GetAsync(range!.From, range.To, unit, null, ct));
    }

    /// <summary>Số đơn tạo, trả tiền và kết thúc theo ngày Việt Nam.</summary>
    /// <remarks>Created/Paid/Completed/Cancelled/Expired dùng mốc nghiệp vụ từ Rental. Active là số đơn đã thanh toán và chưa kết thúc tại cuối ngày to.</remarks>
    [Authorize(Policy = AnalyticsJwtAuthentication.AdminPolicy)]
    [HttpGet("orders")]
    [ProducesResponseType(typeof(OrdersResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OrdersResponse>> Orders(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        if (!TryRange(from, to, out var range)) return InvalidRange();
        return Ok(await orderTrend.GetAsync(range!, ct));
    }

    /// <summary>Top loại thiết bị, listing và hãng theo số lượt thuê đã thanh toán; không tính cọc vào GMV.</summary>
    /// <remarks>Tập item được chọn theo ngày thanh toán. Quantity và CompletedCount chỉ tính hoạt động trước cuối ngày to. Xếp theo số item giảm dần, hòa thì theo số lượng đã bàn giao rồi Id. Thiếu snapshot loại/hãng được gom vào Không xác định.</remarks>
    [Authorize(Policy = AnalyticsJwtAuthentication.AdminPolicy)]
    [HttpGet("equipment")]
    [ProducesResponseType(typeof(EquipmentResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<EquipmentResponse>> Equipment(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        if (!TryRange(from, to, out var range)) return InvalidRange();
        if (limit is < 1 or > 100)
        {
            return Problem(statusCode: 400, title: "INVALID_LIMIT", detail: "limit phải từ 1 đến 100.");
        }
        return Ok(await dashboard.EquipmentAsync(range!, limit, ct));
    }

    /// <summary>Hiệu suất và số tiền phải trả, đã chi theo từng nhà cung cấp; có phân trang.</summary>
    /// <remarks>TotalRequests dùng ngày tạo đơn. Accepted/Rejected/Timeout/CompletedRentals dùng mốc item; RentalValue dùng ngày thanh toán. SupplierPayable và PayoutCompleted lấy trực tiếp từ Payment.</remarks>
    [Authorize(Policy = AnalyticsJwtAuthentication.AdminPolicy)]
    [HttpGet("suppliers")]
    [ProducesResponseType(typeof(SuppliersResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SuppliersResponse>> Suppliers(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        if (!TryRange(from, to, out var range)) return InvalidRange();
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue)
        {
            return Problem(statusCode: 400, title: "INVALID_PAGINATION", detail: "page >= 1, pageSize từ 1 đến 100.");
        }
        return Ok(await dashboard.SuppliersAsync(range!, page, pageSize, ct));
    }

    /// <summary>Tổng quan KPI dành riêng cho nhà cung cấp đang đăng nhập.</summary>
    /// <remarks>Dữ liệu phân rã theo các item thuộc NCC trong snapshot Rental, dùng mốc thanh toán Payment. Lượt thuê tính theo item. Mặc định 30 ngày gần nhất.</remarks>
    [Authorize(Policy = AnalyticsJwtAuthentication.UserPolicy)]
    [HttpGet("me/overview")]
    [ProducesResponseType(typeof(OverviewResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OverviewResponse>> MyOverview(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        if (!User.TryGetSubjectUserId(out var supplierId))
        {
            return Unauthorized();
        }
        if (from is null && to is null)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
            to = today;
            from = today.AddDays(-29);
        }
        if (!TryRange(from, to, out var range)) return InvalidRange();
        return Ok(await dashboard.OverviewAsync(range!, supplierId, ct));
    }

    /// <summary>Biểu đồ tiền thuê và tiền đã nhận của nhà cung cấp đang đăng nhập theo ngày, tuần hoặc tháng Việt Nam.</summary>
    /// <remarks>Tiền thuê (GrossRentalValue) và tiền đã nhận (SupplierPayout) theo mốc nghiệp vụ. Mặc định 30 ngày gần nhất.</remarks>
    [Authorize(Policy = AnalyticsJwtAuthentication.UserPolicy)]
    [HttpGet("me/revenue")]
    [ProducesResponseType(typeof(RevenueAnalyticsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RevenueAnalyticsResponse>> MyRevenue(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string granularity = "day", CancellationToken ct = default)
    {
        if (!User.TryGetSubjectUserId(out var supplierId))
        {
            return Unauthorized();
        }
        if (from is null && to is null)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
            to = today;
            from = today.AddDays(-29);
        }
        if (!TryRange(from, to, out var range)) return InvalidRange();
        if (!RevenueGranularityParser.TryParse(granularity, out var unit))
        {
            return Problem(statusCode: 400, title: "INVALID_GRANULARITY", detail: "granularity phải là day, week hoặc month.");
        }
        return Ok(await revenue.GetAsync(range!.From, range.To, unit, supplierId, ct));
    }

    private static bool TryRange(DateOnly? from, DateOnly? to, out DashboardDateRange? range)
    {
        range = null;
        if (from is null || to is null || to < from || to == DateOnly.MaxValue ||
            to.Value.DayNumber - from.Value.DayNumber >= 366) return false;
        range = DashboardDateRange.Create(from.Value, to.Value);
        return true;
    }

    private ObjectResult InvalidRange() => Problem(statusCode: 400, title: "INVALID_DATE_RANGE",
        detail: "from và to phải là ngày hợp lệ, cùng khoảng không quá 366 ngày (bao gồm hai đầu).");
}
