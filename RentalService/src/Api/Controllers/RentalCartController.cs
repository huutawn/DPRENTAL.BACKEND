using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.Services.Cart;
using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Api.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Dprental.RentalService.Api.Controllers;

/// <summary>
/// Quản lý giỏ hàng thuê thiết bị (Rental Cart) của người thuê (Renter).
/// </summary>
[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Authorize(Policy = "Renter")]
[Route("cart")]
[Route("~/rental/cart")]
public sealed class RentalCartController(
    ICurrentRenter currentRenter,
    RentalCartUseCases cartUseCases,
    GetRentalCart getRentalCart) : ControllerBase
{
    /// <summary>
    /// Lấy thông tin chi tiết giỏ hàng hiện tại của người thuê.
    /// Trả về danh sách mục thuê kèm trạng thái nguyên nhân (status) và mức thao tác (interactionStatus):
    /// - ENABLED: Hợp lệ và đủ tồn kho. UI cho phép: Checkout, Đổi số lượng, Đổi ngày, Xóa.
    /// - DISABLED_EDITABLE: Thiếu tồn kho hoặc ngày quá hạn. UI chặn Checkout; cho phép: Đổi số lượng, Đổi ngày, Xóa.
    /// - DISABLED_READ_ONLY: Listing bị khóa/xóa hoặc thuộc chính renter. UI chặn Checkout, Đổi số lượng, Đổi ngày; chỉ cho phép: Xóa.
    /// </summary>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Lấy giỏ hàng thành công, trả về danh sách mục và trạng thái kiểm tra.</response>
    /// <response code="401">Người dùng chưa được xác thực (thiếu hoặc sai token JWT).</response>
    /// <response code="403">Người dùng không có quyền truy cập giỏ hàng (không thỏa mãn policy Renter).</response>
    /// <response code="503">Không thể kết nối đến Inventory để kiểm tra tồn kho (INVENTORY_UNAVAILABLE hoặc INVENTORY_CONTRACT_INVALID).</response>
    /// <returns>Dữ liệu giỏ hàng kèm trạng thái kiểm tra từng mục thuê.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(RentalCartReadModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        Result<RentalCartReadModel> result = await getRentalCart.ExecuteAsync(
            currentRenter.RenterId, cancellationToken);

        return result.IsFailure
            ? this.ToProblem(result.Error)
            : Ok(result.Value);
    }

    /// <summary>
    /// Thêm một mục thiết bị vào giỏ hàng của người thuê.
    /// Nếu trùng bài niêm yết và cùng cặp ngày, số lượng sẽ được gộp và kiểm tra tồn kho theo tổng số lượng cuối cùng.
    /// </summary>
    /// <param name="request">Thông tin thiết bị, số lượng và khoảng thời gian dự kiến thuê.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Thêm mục vào giỏ hàng thành công, trả về mã định danh của mục giỏ hàng (<c>CartItemId</c>).</response>
    /// <response code="400">Dữ liệu yêu cầu không hợp lệ (số lượng &lt;= 0, ngày bắt đầu chưa đủ 3 ngày đặt trước hoặc khoảng ngày sai lệch).</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Renter hoặc thuê thiết bị do chính mình sở hữu (Cart.CannotRentOwnListing).</response>
    /// <response code="404">Không tìm thấy giỏ hàng hoặc mục thuê.</response>
    /// <response code="409">Listing không tồn tại/đã ngừng nhận thuê (Cart.ListingUnavailable), không đủ tồn kho (Cart.InsufficientAvailability), hoặc xung đột đồng thời (CONCURRENCY_CONFLICT).</response>
    /// <response code="503">Không thể kết nối đến Inventory hoặc phản hồi không hợp lệ (INVENTORY_UNAVAILABLE, INVENTORY_CONTRACT_INVALID).</response>
    /// <returns>Đối tượng chứa ID của mục giỏ hàng vừa được thêm.</returns>
    [HttpPost("items")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(AddCartItemResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Add(
        [FromBody] AddCartItemRequest request,
        CancellationToken cancellationToken)
    {
        var result = await cartUseCases.AddCartItemAsync(
            currentRenter.RenterId,
            request.EquipmentListingId,
            request.Quantity,
            request.ExpectedStartDate,
            request.ExpectedEndDate,
            cancellationToken);

        return result.IsFailure
            ? this.ToProblem(result.Error)
            : Ok(new AddCartItemResponse(result.Value.Id));
    }


    /// <summary>
    /// Cập nhật số lượng hoặc khoảng ngày thuê của một mục trong giỏ hàng.
    /// - Đổi số lượng: Kiểm tra tồn kho theo số lượng mới; nếu ngày đã quá hạn đặt trước thì cho phép lưu và bỏ qua kiểm tra tồn kho.
    /// - Đổi ngày: Nếu trùng với một dòng có sẵn cùng listing và cặp ngày mới, hai dòng sẽ được gộp lại và kiểm tra tồn kho theo tổng số lượng.
    /// </summary>
    /// <param name="id">Mã định danh duy nhất của mục giỏ hàng (CartItemId) cần cập nhật.</param>
    /// <param name="request">Dữ liệu cập nhật (chứa số lượng mới hoặc cặp ngày thuê mới).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="204">Cập nhật mục giỏ hàng thành công, không có nội dung trả về.</response>
    /// <response code="400">Dữ liệu cập nhật không hợp lệ (vi phạm quy tắc cập nhật đơn lẻ hoặc ngày/số lượng sai quy định).</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền truy cập mục giỏ hàng này hoặc thuê thiết bị do chính mình sở hữu (Cart.CannotRentOwnListing).</response>
    /// <response code="404">Không tìm thấy mục giỏ hàng với ID đã cho.</response>
    /// <response code="409">Listing không khả dụng (Cart.ListingUnavailable), không đủ tồn kho (Cart.InsufficientAvailability), hoặc xung đột đồng thời (CONCURRENCY_CONFLICT).</response>
    /// <response code="503">Không thể kết nối đến Inventory hoặc phản hồi không hợp lệ (INVENTORY_UNAVAILABLE, INVENTORY_CONTRACT_INVALID).</response>
    [HttpPatch("items/{id:guid}")]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> UpdateItem(
        [FromRoute] Guid id,
        [FromBody] UpdateCartItemRequest request,
        CancellationToken cancellationToken)
    {
        var result = await cartUseCases.UpdateCartItemAsync(
            currentRenter.RenterId, id, request, cancellationToken);

        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    /// <summary>
    /// Xóa một mục cụ thể khỏi giỏ hàng của người thuê.
    /// </summary>
    /// <param name="id">Mã định danh duy nhất của mục giỏ hàng (CartItemId) cần xóa.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="204">Xóa mục khỏi giỏ hàng thành công.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền truy cập giỏ hàng này.</response>
    /// <response code="404">Không tìm thấy mục giỏ hàng tương ứng.</response>
    [HttpDelete("items/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(
        Guid id,
        CancellationToken cancellationToken)
    {
        Result result = await cartUseCases.RemoveCartItemAsync(
            currentRenter.RenterId,
            id,
            cancellationToken);

        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }


    /// <summary>
    /// Làm trống toàn bộ giỏ hàng của người thuê.
    /// </summary>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="204">Làm trống giỏ hàng thành công.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Renter.</response>
    /// <response code="404">Không có giỏ hàng hoạt động.</response>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Clear(CancellationToken cancellationToken)
    {
        Result result = await cartUseCases.ClearCartAsync(
            currentRenter.RenterId, cancellationToken);

        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }
}
