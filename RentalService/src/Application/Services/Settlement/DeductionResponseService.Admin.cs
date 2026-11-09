using Dprental.RentalService.Application.Hubs;
#pragma warning disable CA1848
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Settlement;

/// <remarks>
/// Phần Admin truy vấn và chốt tranh chấp khấu trừ của cùng DeductionResponseService.
/// Quyết định Admin là đầu vào nghiệp vụ cho quyết toán; không đồng nghĩa Payment đã chuyển tiền.
/// </remarks>
public sealed partial class DeductionResponseService
{
    // Admin chốt tranh chấp sau kiểm tra replay, tiền cọc và Media; ghi quyết định cuối cùng tại Rental, chưa chuyển tiền.
    public async Task<Result<ResolveDisputeResponse>> ResolveDisputeAsync(
        Guid itemId, IRentalCallerContext caller, ResolveDisputeRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default)
    {
        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        var auth = RentalAuthorizationPolicy.AuthorizeDisputeResolution(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (auth.IsFailure) return auth.Error;
        // Từ chối key rỗng hoặc vượt giới hạn lưu trữ để mỗi thao tác có thể được nhận diện khi client gửi lại.
        if (request is null || string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
            return Error.Validation("INVALID_REQUEST", "Request and Idempotency-Key (maximum 64 characters) are required.");
        Guid? orderId = await orders.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (!orderId.HasValue) return RentalErrors.ItemNotFound;
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);
        var order = await orders.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        var item = order?.Items.FirstOrDefault(i => i.Id == itemId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null || item is null) return RentalErrors.ItemNotFound;
        var mediaIds = (request.ResolutionMediaIds ?? []).ToArray();
        string payload = JsonSerializer.Serialize(new
        {
            ItemId = itemId,
            AdminId = caller.UserId,
            request.FinalDeductionAmount,
            Note = request.DecisionNote?.Trim(),
            MediaIds = mediaIds.OrderBy(id => id)
        });
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        string key = idempotencyKey.Trim();
        var existing = order.OperationReceipts.FirstOrDefault(r => r.OperationId == key);
        // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
        if (existing is not null)
        {
            // Đối chiếu action, phạm vi và fingerprint; cùng key nhưng khác yêu cầu phải báo conflict thay vì dùng kết quả cũ.
            if (existing.Action != "AdminResolveDispute" || existing.TargetItemId != itemId ||
                existing.Fingerprint != fingerprint)
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "This key was used for another request.");
            var saved = string.IsNullOrWhiteSpace(existing.ApiResponseJson)
                ? null : JsonSerializer.Deserialize<ResolveDisputeResponse>(existing.ApiResponseJson);
            return saved is null ? Error.Conflict("RESOLUTION_IN_PROGRESS", "Resolution is not complete.") : saved;
        }
        var proposalForValidation = item.DeductionProposal;
        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (proposalForValidation?.Dispute is null ||
            proposalForValidation.Status != Dprental.RentalService.Common.Enums.DeductionProposalStatus.Disputed)
            return Error.Conflict("NO_OPEN_DISPUTE", "No open dispute exists for this rental item.");
        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (request.FinalDeductionAmount < 0 ||
            request.FinalDeductionAmount > proposalForValidation.TotalProposedAmount ||
            request.FinalDeductionAmount > item.DepositAmount ||
            decimal.Round(request.FinalDeductionAmount, 2) != request.FinalDeductionAmount ||
            string.IsNullOrWhiteSpace(request.DecisionNote) || request.DecisionNote.Trim().Length > 2000 ||
            mediaIds.Length > 10 || mediaIds.Contains(Guid.Empty) || mediaIds.Distinct().Count() != mediaIds.Length)
            return Error.Validation("INVALID_DISPUTE_RESOLUTION", "Final amount, decision note or evidence is invalid.");
        var evidence = await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(
            mediaIds, caller.UserId!.Value, mediaClient, ct: cancellationToken);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (evidence.IsFailure) return evidence.Error;
        DateTimeOffset now = timeProvider.GetUtcNow();
        var resolved = item.ResolveDeductionDispute(caller.UserId.Value, request.FinalDeductionAmount,
            request.DecisionNote, mediaIds, now, key);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (resolved.IsFailure) return resolved.Error;
        var proposal = item.DeductionProposal!;
        string decision = request.FinalDeductionAmount == proposal.TotalProposedAmount ? "UpheldInFull"
            : request.FinalDeductionAmount == 0 ? "Rejected" : "Partial";
        var response = new ResolveDisputeResponse(proposal.Id, item.Id,
            request.FinalDeductionAmount, decision, now, item.Status.ToString());
        order.AddOperationReceipt(new RentalOperationReceipt(Guid.NewGuid(), order.Id, key,
            "AdminResolveDispute", fingerprint, true, null, null, now, targetItemId: itemId,
            payloadJson: payload, apiResponseJson: JsonSerializer.Serialize(response)));
        RentalOrderStatusPolicy.Apply(order, now, key);
        order.RecordReadModelUpdate(now);
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var eventPayload = new RentalDeductionDisputeResolvedPayload(
                proposal.Dispute!.Id, proposal.Id, order.Id, item.Id, order.RenterId,
                item.SupplierId, decision, request.FinalDeductionAmount, now, order.Version);
            var enqueued = await rentalEvents.EnqueueDomainEventAsync(
                KafkaContractSpec.EventTypes.RentalDeductionDisputeResolved,
                proposal.Dispute.Id.ToString("D"), order.Id.ToString("D"), now,
                eventPayload, cancellationToken: cancellationToken);
            if (enqueued.IsFailure) return enqueued.Error;

            await orders.UpdateAsync(order, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
        if (notifier != null)
        {
            try
            {
            // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
            await notifier.NotifyRenterOrderChangedAsync(
                order.RenterId,
                new RenterOrderChangedNotification(
                    Guid.NewGuid(),
                    order.Id,
                    order.Version,
                    OrderChangeTypes.SettlementChanged,
                    now),
                cancellationToken);

            // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
            var subOrder = order.SubOrders.FirstOrDefault(s => s.SupplierId == item.SupplierId);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (subOrder != null)
            {
                // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                await notifier.NotifySupplierSubOrderChangedAsync(
                    item.SupplierId,
                    new SupplierSubOrderChangedNotification(
                        Guid.NewGuid(),
                        order.Id,
                        subOrder.Id,
                        order.Version,
                        OrderChangeTypes.SettlementChanged,
                        now),
                    cancellationToken);
            }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SignalR notification failed after dispute resolution was committed for item {ItemId}", itemId);
            }
        }
        return response;
    }

    // Chỉ cho admin đọc hồ sơ tranh chấp, ghép đề xuất và bằng chứng từng unit đã lưu khi bàn giao/trả.
    public async Task<Result<DisputeDetailDto>> GetDisputeDetailAsync(
        Guid itemId, IRentalCallerContext caller, CancellationToken cancellationToken = default)
    {
        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        var auth = RentalAuthorizationPolicy.AuthorizeDisputeResolution(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (auth.IsFailure) return auth.Error;
        var order = await orders.GetByItemIdWithDetailsAsync(itemId, cancellationToken);
        var item = order?.Items.FirstOrDefault(i => i.Id == itemId);
        var proposal = item?.DeductionProposal;
        var dispute = proposal?.Dispute;
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null || item is null || proposal is null || dispute is null)
            return RentalErrors.DisputeNotFound;
        var units = item.UnitAssignments.Select(u => new DisputeUnitEvidenceDto(
            u.Id, u.AssetId, u.Status.ToString(), u.HandoverCondition?.ToString(),
            u.HandoverNote, u.HandoverMediaIds, u.ReturnCondition?.ToString(),
            u.ReturnNote, u.ReturnMediaIds, u.DeductionProposalId == proposal.Id,
            u.DeductionReason, u.DeductionMediaIds)).ToArray();
        return new DisputeDetailDto(item.Id, order.Id, order.OrderCode, item.SupplierId, order.RenterId,
            item.DepositAmount, proposal.TotalProposedAmount, proposal.DamageDescription,
            proposal.SubmittedAt, proposal.Status.ToString(), dispute.Reason, dispute.MediaIds,
            proposal.RespondedAt, units);
    }

    // Phân trang ID tranh chấp rồi đọc dữ liệu chi tiết của trang để dựng hàng chờ admin; không tự xử lý tranh chấp khi đọc.
    public async Task<Result<IReadOnlyList<DisputeSummaryDto>>> GetOpenDisputesAsync(
        IRentalCallerContext caller, int skip, int take, CancellationToken cancellationToken = default)
    {
        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        var auth = RentalAuthorizationPolicy.AuthorizeDisputeResolution(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (auth.IsFailure) return auth.Error;
        // Dừng nhánh không hợp lệ với mã INVALID_PAGINATION; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (skip < 0 || take is < 1 or > 100)
            return Error.Validation("INVALID_PAGINATION", "skip must be nonnegative; take must be 1 through 100.");
        var ids = await orders.GetDisputedItemIdsAsync(skip, take, cancellationToken);
        var rows = new List<DisputeSummaryDto>(ids.Count);
        // Duyệt tập dữ liệu của bước hiện tại để tích lũy kết quả, giữ nguyên phạm vi và thứ tự mà caller đã chọn.
        foreach (var id in ids)
        {
            // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
            var order = await orders.GetByItemIdWithDetailsAsync(id, cancellationToken);
            var item = order?.Items.FirstOrDefault(i => i.Id == id);
            var proposal = item?.DeductionProposal;
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (order is null || item is null || proposal?.Dispute is null) continue;
            rows.Add(new DisputeSummaryDto(item.Id, order.Id, order.OrderCode,
                proposal.TotalProposedAmount, item.DepositAmount, proposal.RespondedAt));
        }
        return rows;
    }
}
