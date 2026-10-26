using Dprental.AnalyticsService.Api.Security;
using Dprental.AnalyticsService.Application.Dashboard;
using Dprental.AnalyticsService.Application.Dashboard.Revenue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AnalyticsService.Api.Controllers;

/// <summary>Thống kê quản trị từ read model của Rental và Payment.</summary>
[ApiController]
[Authorize(Policy = AnalyticsJwtAuthentication.AdminPolicy)]
[Route("api/v1/analytics")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class AnalyticsDashboardController(
    DashboardQueryService dashboard,
    OrderTrendQueryService orderTrend,
    RevenueAnalyticsQueryService revenue) : ControllerBase
{
    /// <summary>Tổng quan đơn hàng, tài chính và lượt thuê. Active là số đang hoạt động tại thời điểm gọi.</summary>
    /// <remarks>GMV và cọc lấy từ item của đơn đã thanh toán. CompletedRentalValue là tiền thuê của item hoàn thành.
    /// PlatformRevenue, DeductionAmount và SupplierPayable lấy trực tiếp từ settlement của Payment;
    /// RefundAmount là tiền đã hoàn thực tế, PayoutCompleted là tiền đã chuyển NCC.
    /// Các chỉ số kỳ dùng ngày Việt Nam; Active là ảnh chụp hiện tại.</remarks>
    [HttpGet("overview")]
    [ProducesResponseType(typeof(OverviewResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OverviewResponse>> Overview(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        if (!TryRange(from, to, out var range)) return InvalidRange();
        return Ok(await dashboard.OverviewAsync(range!, ct));
    }

    /// <summary>Doanh thu nền tảng từ phí đã chốt, theo ngày Việt Nam; tuần bắt đầu thứ Hai.</summary>
    /// <remarks>Không bao gồm tiền cọc hoặc toàn bộ tiền thuê. Tổng và bucket chỉ tính settlement có CompletedAt trong from..to theo giờ Việt Nam. Bucket trống có giá trị 0.</remarks>
    [HttpGet("revenue")]
    [ProducesResponseType(typeof(RevenueAnalyticsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RevenueAnalyticsResponse>> Revenue(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string granularity = "day", CancellationToken ct = default)
    {
        if (!TryRange(from, to, out var range)) return InvalidRange();
        if (!RevenueGranularityParser.TryParse(granularity, out var unit))
        {
            return Problem(statusCode: 400, title: "INVALID_GRANULARITY", detail: "granularity phải là day, week hoặc month.");
        }
        return Ok(await revenue.GetAsync(range!.From, range.To, unit, ct));
    }

    /// <summary>Số đơn tạo, trả tiền và kết thúc theo ngày Việt Nam; Active là snapshot hiện tại.</summary>
    /// <remarks>Created/Paid/Completed/Cancelled/Expired dùng mốc nghiệp vụ tương ứng từ Rental. Active không bị giới hạn bởi from/to.</remarks>
    [HttpGet("orders")]
    [ProducesResponseType(typeof(OrdersResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OrdersResponse>> Orders(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        if (!TryRange(from, to, out var range)) return InvalidRange();
        return Ok(await orderTrend.GetAsync(range!, ct));
    }

    /// <summary>Top loại thiết bị, listing và hãng theo số lượt thuê đã thanh toán; không tính cọc vào GMV.</summary>
    /// <remarks>Xếp theo số item giảm dần, hòa thì theo số lượng đã bàn giao giảm dần, sau đó Id tăng dần. Thiếu snapshot loại/hãng được gom vào Không xác định.</remarks>
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
    /// <remarks>Accepted/Rejected/Timeout/CompletedRentals và RentalValue lấy từ Rental. SupplierPayable và PayoutCompleted lấy trực tiếp từ Payment, không tự tính lại phí.</remarks>
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
