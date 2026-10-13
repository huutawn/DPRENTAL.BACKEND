using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.SupplierDecision;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;
/// <summary>
/// Quản lý quyết định của Nhà cung cấp (Supplier) đối với từng mục đơn thuê (RentalItem).
/// </summary>
/// <remarks>
/// Sau khi đơn hàng thanh toán thành công (trạng thái PENDING_SUPPLIER_CONFIRMATION),
/// Supplier sở hữu thiết bị có thể Chấp nhận (Accept) hoặc Từ chối (Reject) từng mục thuê.
/// Các thao tác yêu cầu gửi kèm header <c>Idempotency-Key</c> để đảm bảo tính an toàn chống trùng lặp.
/// </remarks>

[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Authorize]
[Route("items")]
public sealed class RentalItemsController : ControllerBase
{
    private readonly ISupplierDecisionService _decisionService;
    private readonly IRentalCallerContext _callerContext;

    public RentalItemsController(
        ISupplierDecisionService decisionService,
        IRentalCallerContext callerContext)
    {
        _decisionService = decisionService ?? throw new ArgumentNullException(nameof(decisionService));
        _callerContext = callerContext ?? throw new ArgumentNullException(nameof(callerContext));
    }

    /// <summary>
    /// Supplier chấp nhận yêu cầu thuê cho một mục thuê (RentalItem).
    /// </summary>
    /// <remarks>
    /// Quy trình nghiệp vụ khi Supplier chấp nhận:
    /// - Kiểm tra quyền sở hữu thiết bị: chỉ Supplier sở hữu mục thuê mới có quyền chấp nhận.
    /// - Kiểm tra trạng thái: mục thuê phải đang ở trạng thái <c>PENDING_SUPPLIER_CONFIRMATION</c> và chưa hết hạn xác nhận.
    /// - Chuyển trạng thái mục thuê sang <c>AWAITING_HANDOVER</c> (Chờ bàn giao thiết bị).
    /// - Xác nhận phân bổ giữ chỗ thiết bị (Confirm Allocation) tại InventoryService.
    /// - Ghi nhận lịch sử chuyển trạng thái và phát sinh sự kiện Outbox <c>RentalItemConfirmed</c>.
    /// - Không có request body. Hold phải được gia hạn thành công; chỉ chuyển trạng thái sau xác nhận Inventory hợp lệ.
    /// - Gửi lại cùng khóa và nội dung sau thành công trả lại phản hồi đã lưu; khóa khác hoặc nội dung khác có thể trả 409.
    /// - status trong response là chuỗi PascalCase <c>AwaitingHandover</c>; DecisionAt là timestamp có múi giờ.
    /// </remarks>
    /// <param name="itemId">Mã định danh duy nhất của mục thuê (RentalItemId) cần chấp nhận.</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp gửi qua header HTTP (bắt buộc, tối đa 64 ký tự).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Chấp nhận mục thuê thành công, trả về thông tin quyết định và trạng thái mới.</response>
    /// <response code="400">Thiếu hoặc sai định dạng header Idempotency-Key.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không phải là Supplier sở hữu mục thuê này.</response>
    /// <response code="404">Không tìm thấy mục thuê với mã ID đã cho.</response>
    /// <response code="409">Xung đột trạng thái mục thuê hoặc xung đột khóa Idempotency-Key.</response>
    /// <response code="410">Yêu cầu thuê đã quá hạn xác nhận (EXPIRED).</response>
    /// <response code="422">Không thể xử lý nghiệp vụ do vi phạm điều kiện chuyển trạng thái.</response>
    /// <response code="503">Dịch vụ Inventory tạm thời không khả dụng khi xác nhận phân bổ.</response>
    /// <returns>Dữ liệu phản hồi quyết định chấp nhận mục thuê của Supplier.</returns>
    [HttpPost("{itemId:guid}/accept")]
    [ProducesResponseType(typeof(SupplierDecisionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> AcceptItem(
        [FromRoute] Guid itemId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return this.ToProblem(Error.Validation("MISSING_IDEMPOTENCY_KEY", "Header Idempotency-Key là bắt buộc."));
        }

        var result = await _decisionService.AcceptItemAsync(itemId, idempotencyKey, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Supplier từ chối yêu cầu thuê cho một mục thuê (RentalItem).
    /// </summary>
    /// <remarks>
    /// Quy trình nghiệp vụ khi Supplier từ chối:
    /// - Kiểm tra quyền sở hữu thiết bị: chỉ Supplier sở hữu mục thuê mới có quyền từ chối.
    /// - Kiểm tra trạng thái: mục thuê phải đang ở trạng thái <c>PENDING_SUPPLIER_CONFIRMATION</c>.
    /// - Chuyển trạng thái mục thuê sang <c>REJECTED</c>.
    /// - Lên lịch giải phóng phân bổ tại InventoryService; thao tác chạy bất đồng bộ.
    /// - Lên lịch hoàn trả 100% tiền thuê và cọc cho mục này qua PaymentService sau khi giải phóng thành công; 200 không có nghĩa tiền đã hoàn.
    /// - Ghi nhận lịch sử chuyển trạng thái và phát sinh sự kiện Outbox <c>RentalItemRejected</c>.
    /// - Cùng khóa và nội dung trả lại phản hồi đã lưu; khóa đã dùng với nội dung khác trả 409. Các mục thuê khác không bị từ chối.
    /// - rejectReason bắt buộc, tối đa 500 ký tự; optionalNote tùy chọn, tối đa 2000 ký tự. status trả về là <c>Rejected</c>.
    /// <para>Ví dụ body: {"rejectReason":"Không thể bàn giao đúng lịch","optionalNote":null}.</para>
    /// </remarks>
    /// <param name="itemId">Mã định danh duy nhất của mục thuê (RentalItemId) cần từ chối.</param>
    /// <param name="request">Thông tin lý do từ chối (bắt buộc) và ghi chú bổ sung tùy chọn.</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp gửi qua header HTTP (bắt buộc, tối đa 64 ký tự).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Từ chối mục thuê thành công, trả về thông tin quyết định và trạng thái mới.</response>
    /// <response code="400">Thiếu header Idempotency-Key, thiếu lý do từ chối hoặc dữ liệu không hợp lệ.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không phải là Supplier sở hữu mục thuê này.</response>
    /// <response code="404">Không tìm thấy mục thuê với mã ID đã cho.</response>
    /// <response code="409">Xung đột trạng thái mục thuê hoặc xung đột khóa Idempotency-Key.</response>
    /// <response code="410">Yêu cầu thuê đã quá hạn xác nhận (EXPIRED).</response>
    /// <returns>Dữ liệu phản hồi quyết định từ chối mục thuê của Supplier.</returns>
    [HttpPost("{itemId:guid}/reject")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(SupplierDecisionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    public async Task<IActionResult> RejectItem(
        [FromRoute] Guid itemId,
        [FromBody] SupplierRejectRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return this.ToProblem(Error.Validation("MISSING_IDEMPOTENCY_KEY", "Header Idempotency-Key là bắt buộc."));
        }

        var result = await _decisionService.RejectItemAsync(itemId, request, idempotencyKey, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }
}
