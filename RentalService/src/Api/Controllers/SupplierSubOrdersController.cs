using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.IServices.Orders;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.IServices.SupplierDecision;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

/// <summary>
/// Nhà cung cấp (Supplier) xem, nhận hoặc từ chối các đơn con của mình.
/// </summary>
/// <remarks>
/// Mỗi đơn hàng được chia thành đơn con theo nhà cung cấp. Nhà cung cấp chỉ thấy đơn con có thiết bị của mình,
/// sau khi người thuê đã thanh toán đúng hạn, và nhận hoặc từ chối cả đơn con chứ không từng thiết bị.
/// Nhà cung cấp được định danh qua claim sub của token JWT.
/// </remarks>
[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Authorize]
[Route("supplier/sub-orders")]
public sealed class SupplierSubOrdersController(
    ISupplierSubOrderQueryService subOrders,
    ISupplierDecisionService decisions,
    IRentalCallerContext callerContext) : ControllerBase
{
    /// <summary>
    /// Danh sách đơn con của nhà cung cấp.
    /// </summary>
    /// <remarks>
    /// - Chỉ gồm đơn con của người gọi thuộc đơn đã thanh toán đúng hạn, sắp xếp mới nhất trước.
    /// - Tiền và số dòng chỉ tính phần thiết bị của người gọi; trạng thái đơn con tính từ các dòng.
    /// - page nhỏ hơn 1 được đổi thành 1; pageSize được giới hạn trong 1–100. Không có đơn con vẫn trả 200 với items rỗng.
    /// </remarks>
    /// <param name="page">Số thứ tự trang (mặc định 1).</param>
    /// <param name="pageSize">Số đơn con mỗi trang (mặc định 20, tối đa 100).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Lấy danh sách đơn con thành công.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người gọi không phải tài khoản cá nhân (ví dụ service token).</response>
    /// <returns>Một trang đơn con của nhà cung cấp.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<SupplierSubOrderSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMySubOrders(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        Result<PagedResponse<SupplierSubOrderSummaryResponse>> result =
            await subOrders.GetMySubOrdersAsync(page, pageSize, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Chi tiết một đơn con của nhà cung cấp.
    /// </summary>
    /// <remarks>
    /// - Chỉ gồm thiết bị của người gọi, kèm mã đơn cha và thông tin nhận máy của người thuê để giao máy.
    /// - Không có thiết bị của nhà cung cấp khác, tổng tiền cả đơn hay tài khoản nhận hoàn tiền của người thuê.
    /// - Ngày thuê trả ra gồm ngày đầu và ngày dùng cuối (có tính).
    /// - Đơn con không tồn tại, của nhà cung cấp khác hoặc thuộc đơn chưa thanh toán đều trả 404 <c>SubOrder.NotFound</c>.
    /// </remarks>
    /// <param name="subOrderId">Mã định danh đơn con.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Lấy chi tiết đơn con thành công.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người gọi không phải tài khoản cá nhân (ví dụ service token).</response>
    /// <response code="404">Không tìm thấy đơn con của người gọi (SubOrder.NotFound).</response>
    /// <returns>Đơn con kèm thiết bị của nhà cung cấp.</returns>
    [HttpGet("{subOrderId:guid}")]
    [ProducesResponseType(typeof(SupplierSubOrderDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMySubOrder(
        [FromRoute] Guid subOrderId,
        CancellationToken cancellationToken = default)
    {
        Result<SupplierSubOrderDetailResponse> result =
            await subOrders.GetMySubOrderAsync(subOrderId, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Supplier nhận cả đơn con.
    /// </summary>
    /// <remarks>
    /// Supplier nhận hoặc từ chối cả đơn con, không quyết định riêng từng thiết bị. Quy trình khi nhận:
    /// - Chỉ supplier của đơn con mới thao tác được; đơn con của supplier khác trả 404 <c>SUB_ORDER_NOT_FOUND</c>.
    /// - Kiểm tra mọi mục thuê trước khi gọi InventoryService: đơn đã thanh toán, mục thuê đang ở <c>PENDING_SUPPLIER_CONFIRMATION</c>,
    ///   chưa quá hạn xác nhận (mọi mục thuê của đơn con dùng chung hạn sớm nhất) và hold đã được gia hạn. Một mục không đạt thì không xác nhận mục nào.
    /// - Xác nhận phân bổ (Confirm Allocation) tại InventoryService cho từng mục thuê rồi chuyển mục đó sang <c>AWAITING_HANDOVER</c>,
    ///   ghi lịch sử chuyển trạng thái và phát sự kiện Outbox <c>RentalItemConfirmed</c> cho từng mục.
    /// - Nếu InventoryService lỗi giữa chừng, các mục đã xác nhận được giữ nguyên; gửi lại cùng Idempotency-Key để xác nhận nốt các mục còn lại.
    /// - Không có request body. Gửi lại cùng khóa sau khi thành công trả lại kết quả đã lưu; khóa khác sau khi đã quyết định trả 409 <c>ITEM_ALREADY_DECIDED</c>.
    /// - status là chuỗi PascalCase <c>AwaitingHandover</c>; items chứa quyết định của từng mục thuê.
    /// </remarks>
    /// <param name="subOrderId">Mã định danh đơn con cần nhận.</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp gửi qua header HTTP (bắt buộc, tối đa 64 ký tự).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Nhận đơn con thành công, mọi mục thuê chuyển sang AwaitingHandover.</response>
    /// <response code="400">Thiếu hoặc sai định dạng header Idempotency-Key.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="404">Không tìm thấy đơn con của người gọi (SUB_ORDER_NOT_FOUND).</response>
    /// <response code="409">Đơn chưa thanh toán, đơn con đã được quyết định, đang có thao tác nhận khác, hold chưa được gia hạn hoặc xung đột khóa Idempotency-Key.</response>
    /// <response code="410">Đơn con đã quá hạn xác nhận (SUPPLIER_DEADLINE_EXPIRED).</response>
    /// <response code="422">InventoryService trả kết quả phân bổ không khớp với đơn hàng.</response>
    /// <response code="503">InventoryService tạm thời không khả dụng hoặc đang chờ thử lại; gửi lại cùng Idempotency-Key.</response>
    /// <returns>Quyết định nhận cho cả đơn con.</returns>
    [HttpPost("{subOrderId:guid}/accept")]
    [ProducesResponseType(typeof(SubOrderDecisionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> AcceptSubOrder(
        [FromRoute] Guid subOrderId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return this.ToProblem(Error.Validation("MISSING_IDEMPOTENCY_KEY", "Header Idempotency-Key là bắt buộc."));
        }

        Result<SubOrderDecisionResponse> result =
            await decisions.AcceptSubOrderAsync(subOrderId, idempotencyKey, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Supplier từ chối cả đơn con.
    /// </summary>
    /// <remarks>
    /// Quy trình khi từ chối:
    /// - Chỉ supplier của đơn con mới thao tác được; đơn con của supplier khác trả 404 <c>SUB_ORDER_NOT_FOUND</c>.
    /// - Mọi mục thuê của đơn con phải đang ở <c>PENDING_SUPPLIER_CONFIRMATION</c> và chưa quá hạn xác nhận.
    /// - Mọi mục thuê chuyển sang <c>REJECTED</c> trong cùng một lần lưu; phát sự kiện Outbox <c>RentalItemRejected</c> cho từng mục.
    /// - Lên lịch giải phóng phân bổ tại InventoryService và hoàn 100% tiền thuê và cọc của từng mục qua PaymentService; 200 không có nghĩa tiền đã hoàn.
    /// - Đơn con của supplier khác trong cùng đơn hàng không bị ảnh hưởng.
    /// - Cùng khóa và nội dung trả lại kết quả đã lưu; khóa đã dùng với nội dung khác trả 409.
    /// - rejectReason bắt buộc, tối đa 500 ký tự; optionalNote tùy chọn, tối đa 2000 ký tự. status trả về là <c>Rejected</c>.
    /// <para>Ví dụ body: {"rejectReason":"Máy xúc đang bảo trì, không giao kịp","optionalNote":null}.</para>
    /// </remarks>
    /// <param name="subOrderId">Mã định danh đơn con cần từ chối.</param>
    /// <param name="request">Lý do từ chối (bắt buộc) và ghi chú tùy chọn.</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp gửi qua header HTTP (bắt buộc, tối đa 64 ký tự).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Từ chối đơn con thành công, mọi mục thuê chuyển sang Rejected.</response>
    /// <response code="400">Thiếu header Idempotency-Key, thiếu lý do từ chối hoặc dữ liệu không hợp lệ.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="404">Không tìm thấy đơn con của người gọi (SUB_ORDER_NOT_FOUND).</response>
    /// <response code="409">Đơn chưa thanh toán, đơn con đã được quyết định, đang có thao tác nhận hoặc xung đột khóa Idempotency-Key.</response>
    /// <response code="410">Đơn con đã quá hạn xác nhận (SUPPLIER_DEADLINE_EXPIRED).</response>
    /// <returns>Quyết định từ chối cho cả đơn con.</returns>
    [HttpPost("{subOrderId:guid}/reject")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(SubOrderDecisionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    public async Task<IActionResult> RejectSubOrder(
        [FromRoute] Guid subOrderId,
        [FromBody] SupplierRejectRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return this.ToProblem(Error.Validation("MISSING_IDEMPOTENCY_KEY", "Header Idempotency-Key là bắt buộc."));
        }

        Result<SubOrderDecisionResponse> result =
            await decisions.RejectSubOrderAsync(subOrderId, request, idempotencyKey, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }
}
