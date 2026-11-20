using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Application.IServices.Evidence;
using Dprental.RentalService.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

/// <summary>
/// Đọc ảnh bằng chứng riêng tư của một mục thuê. Người dùng không có quyền gọi MediaService trực tiếp,
/// nên lấy link đọc tạm thời qua Rental sau khi Rental kiểm tra quyền xem.
/// </summary>
[ApiController]
[Authorize]
[Route("items/{itemId:guid}/evidence")]
public sealed class RentalEvidenceController(
    IRentalEvidenceAccessService evidence,
    IRentalCallerContext caller) : ControllerBase
{
    /// <summary>
    /// Người thuê, nhà cung cấp của mục thuê hoặc Admin lấy link đọc tạm thời của một ảnh bằng chứng.
    /// Link hết hạn theo expiresAtUtc; hết hạn thì gọi lại để lấy link mới.
    /// MediaId không thuộc mục thuê, mục thuê không tồn tại hay người xem không liên quan đều trả 404 Media.EvidenceNotFound.
    /// </summary>
    /// <param name="itemId">Mã mục thuê (RentalItem).</param>
    /// <param name="mediaId">Mã ảnh lấy từ HandoverMediaIds, ReturnMediaIds, MediaIds của yêu cầu trả, khấu trừ hoặc tranh chấp.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    [HttpGet("{mediaId:guid}/access")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)] // link ký có hạn, không cho cache
    [ProducesResponseType(typeof(MediaAccessDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateReadAccess(Guid itemId, Guid mediaId, CancellationToken cancellationToken)
    {
        var result = await evidence.CreateReadAccessAsync(itemId, mediaId, caller, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
