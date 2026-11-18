using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Application.Services.Settlement;

/// <summary>Admin read-only dispute queries.</summary>
public sealed partial class DeductionResponseService
{
    // Chá»‰ cho admin Ä‘á»c há»“ sÆ¡ tranh cháº¥p, ghÃ©p Ä‘á» xuáº¥t vÃ  báº±ng chá»©ng tá»«ng unit Ä‘Ã£ lÆ°u khi bÃ n giao/tráº£.
    public async Task<Result<DisputeDetailDto>> GetDisputeDetailAsync(
        Guid itemId, IRentalCallerContext caller, CancellationToken cancellationToken = default)
    {
        // Kiá»ƒm tra danh tÃ­nh/quyá»n cá»§a caller trÆ°á»›c khi tráº£ dá»¯ liá»‡u hoáº·c Ã¡p dá»¥ng quyáº¿t Ä‘á»‹nh trong pháº¡m vi tÃ i nguyÃªn.
        var auth = RentalAuthorizationPolicy.AuthorizeDisputeQuery(caller);
        // Tráº£ lá»—i cá»§a bÆ°á»›c vá»«a thá»±c hiá»‡n; khÃ´ng cháº¡y pháº§n xá»­ lÃ½ phÃ­a sau khi bÆ°á»›c tiÃªn quyáº¿t chÆ°a thÃ nh cÃ´ng.
        if (auth.IsFailure) return auth.Error;
        var order = await orders.GetByItemIdWithDetailsAsync(itemId, cancellationToken);
        var item = order?.Items.FirstOrDefault(i => i.Id == itemId);
        var proposal = item?.DeductionProposal;
        var dispute = proposal?.Dispute;
        // Kiá»ƒm tra dá»¯ liá»‡u tÃ¹y chá»n hoáº·c káº¿t quáº£ tra cá»©u trÆ°á»›c khi dÃ¹ng; xá»­ lÃ½ riÃªng trÆ°á»ng há»£p khÃ´ng cÃ³ giÃ¡ trá»‹.
        if (order is null || item is null || proposal is null || dispute is null)
            return RentalErrors.DisputeNotFound;
        var units = item.UnitAssignments.Select(u => new DisputeUnitEvidenceDto(
            u.Id, u.AssetId, u.Status.ToString(), u.HandoverCondition?.ToString(),
            u.HandoverNote, u.HandoverMediaIds, u.ReturnCondition?.ToString(),
            u.ReturnNote, u.ReturnMediaIds, u.DeductionProposalId == proposal.Id,
            u.DeductionReason, u.DeductionMediaIds)
        {
            RenterReturnMediaIds = item.ReturnRequests
                .SelectMany(r => r.Units)
                .Where(ru => ru.RentalUnitAssignmentId == u.Id)
                .SelectMany(ru => ru.MediaIds)
                .Distinct()
                .ToArray()
        }).ToArray();
        return new DisputeDetailDto(item.Id, order.Id, order.OrderCode, item.SupplierId, order.RenterId,
            item.DepositAmount, proposal.TotalProposedAmount, proposal.DamageDescription,
            proposal.SubmittedAt, proposal.Status.ToString(), dispute.Reason, dispute.MediaIds,
            proposal.RespondedAt, units)
        {
            ClaimedDamageAmount = proposal.ClaimedDamageAmount,
            AgreedDeductionAmount = item.FinalDeductionAmount,
            CurrentSupplierOfferAmount = dispute.SupplierOfferAmount ?? proposal.TotalProposedAmount,
            DepositRefundAmount = item.FinalDeductionAmount is { } final
                ? item.DepositAmount - final : null,
            FinalizationReason = dispute.ResolutionReason
        };
    }

    // PhÃ¢n trang ID tranh cháº¥p rá»“i Ä‘á»c dá»¯ liá»‡u chi tiáº¿t cá»§a trang Ä‘á»ƒ dá»±ng hÃ ng chá» admin; khÃ´ng tá»± xá»­ lÃ½ tranh cháº¥p khi Ä‘á»c.
    public async Task<Result<IReadOnlyList<DisputeSummaryDto>>> GetOpenDisputesAsync(
        IRentalCallerContext caller, int skip, int take, CancellationToken cancellationToken = default)
    {
        // Kiá»ƒm tra danh tÃ­nh/quyá»n cá»§a caller trÆ°á»›c khi tráº£ dá»¯ liá»‡u hoáº·c Ã¡p dá»¥ng quyáº¿t Ä‘á»‹nh trong pháº¡m vi tÃ i nguyÃªn.
        var auth = RentalAuthorizationPolicy.AuthorizeDisputeQuery(caller);
        // Tráº£ lá»—i cá»§a bÆ°á»›c vá»«a thá»±c hiá»‡n; khÃ´ng cháº¡y pháº§n xá»­ lÃ½ phÃ­a sau khi bÆ°á»›c tiÃªn quyáº¿t chÆ°a thÃ nh cÃ´ng.
        if (auth.IsFailure) return auth.Error;
        // Dá»«ng nhÃ¡nh khÃ´ng há»£p lá»‡ vá»›i mÃ£ INVALID_PAGINATION; caller nháº­n lá»—i theo contract thay vÃ¬ tiáº¿p tá»¥c thay Ä‘á»•i dá»¯ liá»‡u.
        if (skip < 0 || take is < 1 or > 100)
            return Error.Validation("INVALID_PAGINATION", "skip must be nonnegative; take must be 1 through 100.");
        var ids = await orders.GetDisputedItemIdsAsync(skip, take, cancellationToken);
        var rows = new List<DisputeSummaryDto>(ids.Count);
        // Duyá»‡t táº­p dá»¯ liá»‡u cá»§a bÆ°á»›c hiá»‡n táº¡i Ä‘á»ƒ tÃ­ch lÅ©y káº¿t quáº£, giá»¯ nguyÃªn pháº¡m vi vÃ  thá»© tá»± mÃ  caller Ä‘Ã£ chá»n.
        foreach (var id in ids)
        {
            // Äá»c dá»¯ liá»‡u/Ä‘á»‹nh danh tá»« repository Ä‘á»ƒ xÃ¡c Ä‘á»‹nh Ä‘á»‘i tÆ°á»£ng vÃ  tráº¡ng thÃ¡i cáº§n xá»­ lÃ½; kiá»ƒm tra khÃ´ng tá»“n táº¡i á»Ÿ nhÃ¡nh káº¿ tiáº¿p.
            var order = await orders.GetByItemIdWithDetailsAsync(id, cancellationToken);
            var item = order?.Items.FirstOrDefault(i => i.Id == id);
            var proposal = item?.DeductionProposal;
            // Kiá»ƒm tra dá»¯ liá»‡u tÃ¹y chá»n hoáº·c káº¿t quáº£ tra cá»©u trÆ°á»›c khi dÃ¹ng; xá»­ lÃ½ riÃªng trÆ°á»ng há»£p khÃ´ng cÃ³ giÃ¡ trá»‹.
            if (order is null || item is null || proposal?.Dispute is null) continue;
            rows.Add(new DisputeSummaryDto(item.Id, order.Id, order.OrderCode,
                proposal.TotalProposedAmount, item.DepositAmount, proposal.RespondedAt)
            {
                ClaimedDamageAmount = proposal.ClaimedDamageAmount,
                AgreedDeductionAmount = item.FinalDeductionAmount,
                CurrentSupplierOfferAmount = proposal.Dispute.SupplierOfferAmount ?? proposal.TotalProposedAmount
            });
        }
        return rows;
    }
}
