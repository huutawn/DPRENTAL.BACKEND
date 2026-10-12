using Dprental.RentalService.Api.Contracts.Cart;
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
    /// - Đối chiếu phiên bản giá client đang giữ (<paramref name="clientPriceVersions"/>) để phát hiện biến động giá (<c>PriceChanged</c>).
    /// - Kiểm tra số lượng thiết bị rảnh trong suốt khoảng thời gian thuê yêu cầu.
    /// - Trả về trạng thái chi tiết của từng dòng sản phẩm trong giỏ (Available, PriceChanged, InsufficientQuantity, v.v.).
    /// </remarks>
    /// <param name="clientPriceVersions">
    /// Bảng ánh xạ phiên bản giá client đang lưu trữ dạng Dictionary&lt;Guid, string&gt; (Key: EquipmentListingId, Value: priceVersion) để đối chiếu thay đổi giá.
    /// </param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Lấy giỏ hàng thành công, trả về danh sách mục và trạng thái kiểm tra.</response>
    /// <response code="400">Yêu cầu không hợp lệ hoặc dữ liệu truy vấn sai quy cách.</response>
    /// <response code="401">Người dùng chưa được xác thực (thiếu hoặc sai token JWT).</response>
    /// <response code="403">Người dùng không có quyền truy cập giỏ hàng (không thỏa mãn policy Renter).</response>
    /// <returns>Dữ liệu giỏ hàng kèm trạng thái kiểm tra từng mục thuê.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(RentalCartReadModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get([FromQuery] Dictionary<Guid, string>? clientPriceVersions, CancellationToken cancellationToken)
    {
        Result<RentalCartReadModel> result = await getRentalCart.ExecuteAsync(
            currentRenter.RenterId, clientPriceVersions, cancellationToken);

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
    /// - Ngày bắt đầu (<c>ExpectedStartDate</c>) phải trong tương lai (sau ngày hiện tại).
    /// - Ngày kết thúc (<c>ExpectedEndDate</c>) phải lớn hơn hoặc bằng ngày bắt đầu và nằm trong giới hạn tối đa cho phép.
    /// </remarks>
    /// <param name="request">Thông tin thiết bị, số lượng và khoảng thời gian dự kiến thuê.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Thêm mục vào giỏ hàng thành công, trả về mã định danh của mục giỏ hàng (<c>CartItemId</c>).</response>
    /// <response code="400">Dữ liệu yêu cầu không hợp lệ (số lượng &lt;= 0, ngày thuê ở quá khứ hoặc khoảng ngày sai lệch).</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Renter.</response>
    /// <response code="404">Không tìm thấy bài niêm yết thiết bị (EquipmentListing) tương ứng.</response>
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
    /// - Vi phạm quy tắc trên sẽ nhận mã lỗi 400 Bad Request kèm mã lỗi <c>Cart.InvalidUpdate</c>.
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
        bool hasAnyDate = request.ExpectedStartDate is not null || request.ExpectedEndDate is not null;
        bool hasBothDates = request.ExpectedStartDate is not null && request.ExpectedEndDate is not null;
        bool hasQuantity = request.Quantity is not null;

        if ((hasAnyDate && !hasBothDates) || hasQuantity == hasBothDates)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Cart.InvalidUpdate",
                Detail = "Cung cấp Quantity hoặc cả ExpectedStartDate và ExpectedEndDate; mỗi PATCH chỉ cập nhật một loại thông tin."
            });
        }

        if (hasQuantity)
        {
            var qtyResult = await cartUseCases.UpdateCartItemQuantityAsync(
                currentRenter.RenterId, id, request.Quantity.GetValueOrDefault(), cancellationToken);

            if (qtyResult.IsFailure) return this.ToProblem(qtyResult.Error);
        }
        else
        {
            var datesResult = await cartUseCases.UpdateCartItemDatesAsync(
                currentRenter.RenterId, id, request.ExpectedStartDate.GetValueOrDefault(), request.ExpectedEndDate.GetValueOrDefault(), cancellationToken);

            if (datesResult.IsFailure) return this.ToProblem(datesResult.Error);
        }

        return NoContent();
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
    /// Xóa tất cả các mục thiết bị đang có trong giỏ hàng của người thuê hiện tại.
    /// </remarks>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="204">Làm trống giỏ hàng thành công.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Renter.</response>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Clear(CancellationToken cancellationToken)
    {
        Result result = await cartUseCases.ClearCartAsync(
            currentRenter.RenterId, cancellationToken);

        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }
}
