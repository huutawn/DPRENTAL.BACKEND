using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Application.IServices;

public interface ISettlementService
{
    Task<Result<SettleRentalResponse>> SettleAsync(SettleRentalRequest request, CancellationToken ct);
}