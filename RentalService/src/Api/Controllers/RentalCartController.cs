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
/// <remarks>
/// Cung cấp các thao tác xem giỏ hàng, thêm mục thuê mới, cập nhật số lượng hoặc khoảng ngày thuê,
/// xóa từng mục hoặc làm trống giỏ hàng. Mỗi người thuê chỉ sở hữu duy nhất một giỏ hàng logic,
/// được định danh thông qua token JWT (claim sub / Renter policy).
/// </remarks>
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
    /// </summary>
    /// <remarks>
    /// Endpoint này tự động kiểm tra lại giá (DailyRentalRate, Deposit) và tính khả dụng của thiết bị từ InventoryService:
    /// - Luôn trả về giá mới nhất từ Inventory; checkout sẽ đối chiếu tổng tiền người thuê xác nhận.
    /// - Kiểm tra số lượng thiết bị rảnh trong suốt khoảng thời gian thuê yêu cầu.
    /// - <c>status</c> là chuỗi: AVAILABLE, INSUFFICIENT_QUANTITY, UNAVAILABLE, LISTING_DISABLED, VALIDATION_FAILED hoặc DATE_PASSED.
    /// - Tiền thuê ước tính = đơn giá/ngày × số lượng × số ngày (tính cả ngày đầu và cuối); tiền cọc là giá mỗi đơn vị.
    /// - Chưa có giỏ hàng thì trả 200 với cartId rỗng và items rỗng. Việc đọc không tạo hold hay khóa giá.
    /// </remarks>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Lấy giỏ hàng thành công, trả về danh sách mục và trạng thái kiểm tra.</response>
    /// <response code="401">Người dùng chưa được xác thực (thiếu hoặc sai token JWT).</response>
    /// <response code="403">Người dùng không có quyền truy cập giỏ hàng (không thỏa mãn policy Renter).</response>
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
    /// </summary>
    /// <remarks>
    /// Thêm thiết bị vào giỏ hàng của người thuê hiện tại:
    /// - Số lượng thuê (<c>Quantity</c>) phải lớn hơn 0.
    /// - Ngày bắt đầu (<c>ExpectedStartDate</c>) phải đặt trước ít nhất 3 ngày: hôm nay 14/10 thì sớm nhất là 17/10.
    /// - Ngày gửi theo YYYY-MM-DD, tính theo Asia/Ho_Chi_Minh. Ngày bắt đầu tối đa 6 tháng từ hôm nay.
    /// - Ngày kết thúc không trước ngày bắt đầu; tối đa 365 ngày, tính cả hai đầu.
    /// - Không được thuê listing của chính mình. Cùng listing và cùng cặp ngày sẽ cộng số lượng vào dòng hiện có, trả lại cùng cartItemId.
    /// - Chỉ lưu nhu cầu thuê; không giữ thiết bị hay giá và không đảm bảo khả dụng khi checkout.
    /// <para>Ví dụ body: {"equipmentListingId":"11111111-1111-1111-1111-111111111111","quantity":2,"expectedStartDate":"2026-11-01","expectedEndDate":"2026-11-03"}.</para>
    /// </remarks>
    /// <param name="request">Thông tin thiết bị, số lượng và khoảng thời gian dự kiến thuê.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Thêm mục vào giỏ hàng thành công, trả về mã định danh của mục giỏ hàng (<c>CartItemId</c>).</response>
    /// <response code="400">Dữ liệu yêu cầu không hợp lệ (số lượng &lt;= 0, ngày bắt đầu chưa đủ 3 ngày đặt trước hoặc khoảng ngày sai lệch).</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Renter.</response>
    /// <response code="404">Không tìm thấy giỏ hàng hoặc mã listing rỗng; listing không có thông tin chủ sở hữu trả 400 Cart.OwnerIdRequired.</response>
    /// <response code="409">Xung đột dữ liệu hoặc thiết bị không thể thêm vào giỏ.</response>
    /// <returns>Đối tượng chứa ID của mục giỏ hàng vừa được thêm.</returns>
    [HttpPost("items")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(AddCartItemResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
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
    /// </summary>
    /// <remarks>
    /// Quy tắc cập nhật:
    /// - Mỗi yêu cầu PATCH chỉ được phép cập nhật **hoặc** <c>Quantity</c>, **hoặc** cả hai ngày <c>ExpectedStartDate</c> và <c>ExpectedEndDate</c>.
    /// - Không được gửi đồng thời cả số lượng và ngày, cũng không được chỉ gửi một trong hai ngày.
    /// - Vi phạm quy tắc trên trả 400 <c>Cart.InvalidUpdate</c>. Quantity phải dương; ngày theo YYYY-MM-DD, đặt trước ít nhất 3 ngày,
    /// đặt trước tối đa 6 tháng và tối đa 365 ngày thuê tính cả hai đầu.
    /// - Nếu ngày mới trùng một dòng khác của cùng listing, hệ thống cộng số lượng vào dòng đó và xóa dòng đang sửa.
    /// <para>Ví dụ body: {"quantity":3} hoặc {"expectedStartDate":"2026-11-01","expectedEndDate":"2026-11-03"}.</para>
    /// </remarks>
    /// <param name="id">Mã định danh duy nhất của mục giỏ hàng (CartItemId) cần cập nhật.</param>
    /// <param name="request">Dữ liệu cập nhật (chứa số lượng mới hoặc cặp ngày thuê mới).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="204">Cập nhật mục giỏ hàng thành công, không có nội dung trả về.</response>
    /// <response code="400">Dữ liệu cập nhật không hợp lệ (vi phạm quy tắc cập nhật đơn lẻ hoặc ngày/số lượng sai quy định).</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền truy cập mục giỏ hàng này.</response>
    /// <response code="404">Không tìm thấy mục giỏ hàng với ID đã cho.</response>
    [HttpPatch("items/{id:guid}")]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
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
    /// <remarks>
    /// Thao tác xóa một dòng thiết bị theo mã <paramref name="id"/> (CartItemId) khỏi giỏ hàng của người thuê hiện tại.
    /// </remarks>
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
    /// <remarks>
    /// Xóa tất cả các mục trong giỏ hiện tại. Giỏ đã rỗng trả 204; chưa có giỏ hoạt động trả 404 Cart.NotFound.
    /// </remarks>
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
