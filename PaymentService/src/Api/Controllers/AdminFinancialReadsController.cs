using Dprental.PaymentService.Api.Filters;
using Dprental.PaymentService.Api.Security;
using Dprental.PaymentService.Application.DTOs.Operations;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.IServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.PaymentService.Api.Controllers;

[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = "FinancialReadAdminPolicy")]
public sealed class AdminFinancialReadsController(
    IFinancialReadService financialRead, ITransactionResolutionService transactionResolution) : ControllerBase
{
    /// <summary>Hàng đợi đối soát; truyền một kind cho mỗi lần phân trang.</summary>
    [HttpGet("payments/review")]
    [ProducesResponseType(typeof(FinancialPage<FinancialReviewItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Review([FromQuery] string? kind, [FromQuery] FinancialListQuery query, CancellationToken ct)
    {
        var result = await financialRead.ListReviewAsync(kind, query, ct);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Danh sách yêu cầu hoàn cọc thủ công; mặc định chỉ hiện khoản đang chờ.</summary>
    [HttpGet("manual-refunds")]
    [ProducesResponseType(typeof(FinancialPage<ManualRefundListItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ManualRefunds([FromQuery] FinancialListQuery query, CancellationToken ct)
    {
        var result = await financialRead.ListManualRefundsAsync(query, ct);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Đối soát giao dịch chưa ghép được với hóa đơn còn trong hạn.</summary>
    [HttpPost("payments/unmatched/{transactionId:guid}/resolve")]
    [ProducesResponseType(typeof(ResolveUnmatchedTransactionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResolveUnmatched(
        Guid transactionId, [FromBody] ResolveUnmatchedTransactionRequest request, CancellationToken ct)
    {
        if (!User.TryGetSubjectUserId(out var adminId) || adminId == Guid.Empty) return Forbid();
        var result = await transactionResolution.ResolveAsync(transactionId, adminId, request, ct);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
