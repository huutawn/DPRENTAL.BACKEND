using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.DTOs.SupplierStatistics;
using Dprental.RentalService.Application.IServices.SupplierStatistics;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

/// <summary>
/// Quản trị số liệu thống kê xác nhận của Nhà cung cấp (Supplier Confirmation Statistics).
/// </summary>
/// <remarks>
/// Cung cấp endpoint dành riêng cho Quản trị viên (Admin) để theo dõi và đánh giá hiệu suất phản hồi đơn thuê của Supplier
/// (số lượng chấp nhận, từ chối, quá hạn xác nhận, hủy sau khi chấp nhận) dựa trên lịch sử chuyển trạng thái trong RentalService.
/// </remarks>
[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Authorize]
[Route("admin/suppliers")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class AdminSupplierStatisticsController : ControllerBase
{
    private readonly ISupplierStatisticsService _statisticsService;
    private readonly IRentalCallerContext _callerContext;

    public AdminSupplierStatisticsController(
        ISupplierStatisticsService statisticsService,
        IRentalCallerContext callerContext)
    {
        _statisticsService = statisticsService ?? throw new ArgumentNullException(nameof(statisticsService));
        _callerContext = callerContext ?? throw new ArgumentNullException(nameof(callerContext));
    }

    /// <summary>
    /// Thống kê số lượng xác nhận của Supplier theo khoảng thời gian.
    /// </summary>
    /// <remarks>
    /// Trả về số liệu thống kê các quyết định của Supplier trong khoảng thời gian chỉ định:
    /// - <c>AcceptedCount</c>: Số lượng mục thuê Supplier đã chấp nhận (chuyển sang AWAITING_HANDOVER, IN_RENTAL, COMPLETED, v.v.).
    /// - <c>RejectedCount</c>: Số lượng mục thuê Supplier đã chủ động từ chối (REJECTED).
    /// - <c>ConfirmationTimeoutCount</c>: Số lượng mục thuê bị hệ thống tự động hủy do Supplier quá hạn phản hồi (EXPIRED).
    /// - <c>CancelAfterAcceptCount</c>: Số lượng mục thuê bị hủy sau khi Supplier đã chấp nhận.
    /// <para>Đếm từng RentalItem theo lịch sử chuyển trạng thái, không suy từ trạng thái hiện tại; phản hồi chỉ chứa số lượng, không có tỷ lệ.</para>
    /// <para>Khoảng lọc [fromUtc, toUtc): gồm đầu, loại cuối. Cả hai tham số bắt buộc, fromUtc phải nhỏ hơn toUtc.
    /// ISO-8601 phải có Z hoặc offset ±HH:mm (dấu + cần URL-encode thành %2B); kết quả chuẩn hóa về UTC.
    /// Không có lịch sử phù hợp trả 200 với các số đếm bằng 0. Chỉ Admin cá nhân, từ chối Service token.</para>
    /// </remarks>
    /// <param name="supplierId">Mã định danh duy nhất của nhà cung cấp (SupplierId dạng GUID).</param>
    /// <param name="fromUtc">Thời điểm bắt đầu khoảng thống kê theo chuẩn ISO-8601 UTC (bắt buộc, ví dụ: 2026-01-01T00:00:00Z).</param>
    /// <param name="toUtc">Thời điểm kết thúc khoảng thống kê theo chuẩn ISO-8601 UTC (bắt buộc, ví dụ: 2026-02-01T00:00:00Z).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Lấy dữ liệu thống kê thành công.</response>
    /// <response code="400">Mã Supplier ID không hợp lệ hoặc khoảng thời gian fromUtc/toUtc sai định dạng/ngược thứ tự.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Admin.</response>
    /// <returns>Dữ liệu thống kê số lượng đơn xác nhận, từ chối và quá hạn của Supplier.</returns>
    [HttpGet("{supplierId}/confirmation-statistics")]
    [ProducesResponseType(typeof(SupplierConfirmationStatisticsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetConfirmationStatistics(
        [FromRoute] string supplierId,
        [FromQuery] string? fromUtc,
        [FromQuery] string? toUtc,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(supplierId, out var parsedSupplierId) || parsedSupplierId == Guid.Empty)
        {
            return this.ToProblem(Error.Validation("INVALID_SUPPLIER_ID", "Supplier ID không hợp lệ."));
        }

        var request = new SupplierConfirmationStatisticsRequest(parsedSupplierId, fromUtc, toUtc);
        var result = await _statisticsService.GetConfirmationStatisticsAsync(request, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }
}
