using Dprental.RentalService.Application.Hubs;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.IServices.Settlement;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Settlement;

/// <remarks>
/// Nhận đề xuất khấu trừ của supplier, kiểm tra quyền và giới hạn tiền cọc; xử lý cả nhánh supplier quá hạn đề xuất.
/// Khi đủ điều kiện quyết toán, lập lịch receipt cho executor gọi Payment; chưa trực tiếp chuyển tiền.
/// </remarks>
public sealed class DeductionService(
    IRentalOrderRepository orders, IOrderLockManager locks, IUnitOfWork unitOfWork,
    IRentalMediaClient media, TimeProvider clock, IOrderHubNotifier? notifier = null) : IDeductionService
{
    // Supplier gửi đề xuất khấu trừ sau kiểm tra sở hữu, replay, hạn và Media; lưu đề xuất/receipt trước khi thông báo.
    public async Task<Result<SubmitDeductionResponse>> SubmitAsync(
        Guid itemId, IRentalCallerContext caller, SubmitDeductionRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default)
    {
        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        Result<Guid> supplier = RentalAuthorizationPolicy.AuthorizeSupplierCaller(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (supplier.IsFailure) return supplier.Error;
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (request is null || request.EvidenceMediaIds is null || request.AffectedAssetIds is null)
            return Error.Validation("INVALID_DEDUCTION_REQUEST", "Deduction request is incomplete.");
        // Từ chối key rỗng hoặc vượt giới hạn lưu trữ để mỗi thao tác có thể được nhận diện khi client gửi lại.
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key is required (maximum 64 characters).");

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        Guid? orderId = await orders.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (orderId is null) return RentalErrors.ItemNotFound;
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);
        RentalOrder? order = await orders.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null) return RentalErrors.ItemNotFound;
        Guid supplierId = supplier.Value;
        Result<RentalItem> owned = RentalAuthorizationPolicy.AuthorizeSupplierItem(
            supplierId, order.Items.FirstOrDefault(i => i.Id == itemId));
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (owned.IsFailure) return owned.Error;
        RentalItem item = owned.Value;

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string key = idempotencyKey.Trim();
        string payload = JsonSerializer.Serialize(new
        {
            ItemId = itemId,
            SupplierId = supplierId,
            request.DeductionAmount,
            DamageDescription = request.DamageDescription?.Trim(),
            EvidenceMediaIds = request.EvidenceMediaIds.OrderBy(x => x),
            AffectedAssetIds = request.AffectedAssetIds.OrderBy(x => x)
        });
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        // Trả kết quả đã lưu trước khi kiểm tra deadline/trạng thái hiện tại: retry hợp lệ
        // không được thất bại chỉ vì thời hạn vừa qua hoặc proposal đã được tạo ở lần đầu.
        RentalOperationReceipt? existing = order.OperationReceipts.FirstOrDefault(r => r.OperationId == key);
        // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
        if (existing is not null)
        {
            // Đối chiếu action, phạm vi và fingerprint; cùng key nhưng khác yêu cầu phải báo conflict thay vì dùng kết quả cũ.
            if (existing.Action != "SubmitDeduction" || existing.TargetItemId != itemId || existing.Fingerprint != fingerprint)
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "This key was used for another request.");
            var saved = string.IsNullOrWhiteSpace(existing.ApiResponseJson)
                ? null : JsonSerializer.Deserialize<SubmitDeductionResponse>(existing.ApiResponseJson);
            return saved is null
                ? Error.Conflict("DEDUCTION_IN_PROGRESS", "Deduction submission is not complete.")
                : saved;
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset now = clock.GetUtcNow();
        var validation = item.ValidateDeductionSubmission(supplierId, request.DeductionAmount,
            request.DamageDescription, request.EvidenceMediaIds, request.AffectedAssetIds, now);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (validation.IsFailure) return validation.Error;
        var evidence = await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(
            request.EvidenceMediaIds, supplierId, media, isRequired: true, ct: cancellationToken);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (evidence.IsFailure) return evidence.Error;

        // Lấy lại thời gian sau xác minh Media; SubmitDeduction kiểm tra theo mốc mới,
        // tránh nhận đề xuất đã quá hạn trong lúc chờ service bên ngoài.
        now = clock.GetUtcNow();
        var submitted = item.SubmitDeduction(supplierId, request.DeductionAmount,
            request.DamageDescription!, request.EvidenceMediaIds, request.AffectedAssetIds, now);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (submitted.IsFailure) return submitted.Error;
        var proposal = submitted.Value;
        var response = new SubmitDeductionResponse(proposal.Id, proposal.TotalProposedAmount,
            proposal.SubmittedAt, proposal.RenterResponseDeadline, item.Id, item.Status.ToString());
        order.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), order.Id, item.Id,
            RentalItemStatus.PendingSettlement.ToString(), RentalItemStatus.PendingSettlement.ToString(),
            "SubmitDeduction", supplierId, ActorRole.Supplier, "Supplier submitted damage deduction proposal",
            now, key));
        order.AddOperationReceipt(new RentalOperationReceipt(Guid.NewGuid(), order.Id, key,
            "SubmitDeduction", fingerprint, true, null, null, now, targetItemId: item.Id,
            payloadJson: payload, apiResponseJson: JsonSerializer.Serialize(response)));
        RentalOrderStatusPolicy.Apply(order, now, key);
        order.RecordReadModelUpdate(now);
        await orders.UpdateAsync(order, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
        if (notifier != null)
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
        return response;
    }

    // Lấy tối đa 50 item ứng viên quá hạn đề xuất và xử lý từng item bằng nhánh kiểm tra dưới khóa.
    public async Task<int> ProcessDeductionTimeoutsAsync(CancellationToken cancellationToken = default)
    {
        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        var ids = await orders.GetDeductionTimeoutItemIdsAsync(clock.GetUtcNow(), 50, cancellationToken);
        int processed = 0;
        // Duyệt tập dữ liệu của bước hiện tại để tích lũy kết quả, giữ nguyên phạm vi và thứ tự mà caller đã chọn.
        foreach (var id in ids)
        {
            // Xử lý kết quả thành công ở nhánh này; nhánh còn lại giữ cách trả lỗi hoặc đếm thất bại của caller.
            if ((await ExpireSingleItemDeductionAsync(id, cancellationToken)).IsSuccess) processed++;
        }
        return processed;
    }

    // Đọc lại item dưới khóa, chốt khấu trừ 0 nếu vẫn đủ điều kiện quá hạn; dùng key timeout ổn định để chống xử lý lặp.
    public async Task<Result> ExpireSingleItemDeductionAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        Guid? orderId = await orders.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (orderId is null) return Result.Success();
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);
        RentalOrder? order = await orders.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        RentalItem? item = order?.Items.FirstOrDefault(i => i.Id == itemId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (item is null || order is null) return Result.Success();
        DateTimeOffset now = clock.GetUtcNow();
        string operationId = $"deduction-timeout:{itemId:N}";
        // Chỉ chốt timeout khi entity vẫn đủ điều kiện tại thời điểm xử lý; nếu trạng thái đã đổi thì bỏ qua lượt quét cũ.
        if (!item.TryFinalizeZeroDeduction(now, operationId)) return Result.Success();
        order.AddOperationReceipt(new RentalOperationReceipt(Guid.NewGuid(), order.Id,
            operationId, "DeductionTimeout", operationId, true, null, null, now, targetItemId: itemId));
        RentalOrderStatusPolicy.Apply(order, now, operationId);
        order.RecordReadModelUpdate(now);
        await orders.UpdateAsync(order, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
        if (notifier != null)
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
        return Result.Success();
    }

    // Quét một batch item sẵn sàng quyết toán và lập journal cho executor, không trực tiếp chuyển tiền trong vòng quét.
    public async Task<int> ScheduleReadySettlementsAsync(CancellationToken cancellationToken = default)
    {
        // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
        var ids = await orders.GetSettlementReadyItemIdsAsync(50, cancellationToken);
        int count = 0;
        // Duyệt tập dữ liệu của bước hiện tại để tích lũy kết quả, giữ nguyên phạm vi và thứ tự mà caller đã chọn.
        foreach (var id in ids)
        {
            // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
            var result = await ScheduleSettlementAsync(id, cancellationToken);
            // Xử lý kết quả thành công ở nhánh này; nhánh còn lại giữ cách trả lỗi hoặc đếm thất bại của caller.
            if (result.IsSuccess) count++;
        }
        return count;
    }

    // Chỉ tạo một receipt quyết toán khi đã có số khấu trừ cuối, xác nhận đóng Inventory và mã thanh toán cần thiết.
    public async Task<Result> ScheduleSettlementAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        Guid? orderId = await orders.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (!orderId.HasValue) return RentalErrors.ItemNotFound;
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);
        var order = await orders.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        var item = order?.Items.FirstOrDefault(i => i.Id == itemId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null || item is null) return RentalErrors.ItemNotFound;
        // Một item chỉ có một tác vụ quyết toán. Receipt đã tồn tại thì để executor
        // retry/đối soát nó, không tạo một yêu cầu chuyển tiền thay thế.
        if (order.OperationReceipts.Any(r => r.Action == RentalItem.SettleRentalAction && r.TargetItemId == itemId))
            return Result.Success();
        // Số khấu trừ cuối chưa đủ: phải có xác nhận đóng allocation từ Inventory.
        // Nếu quyết toán sớm có thể hoàn cọc trong khi máy chưa được xử lý trả an toàn.
        if (!item.IsReadyForSettlement) return Result.Success();
        // Kiểm tra mã hóa đơn đã được gắn/khớp trước khi thao tác Payment, tránh xử lý nhầm hoặc tạo hóa đơn thay thế.
        if (string.IsNullOrWhiteSpace(order.InvoiceRef) || string.IsNullOrWhiteSpace(order.PaymentRef))
            return Error.Conflict("PAYMENT_REFERENCE_MISSING", "InvoiceRef or PaymentRef is not available for settlement.");

        // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
        string operationKey = RentalOperationExecutor.ComputeOperationKey(RentalItem.SettleRentalAction, itemId.ToString("N"));
        var request = new SettleRentalRequest(order.Id, item.Id, order.InvoiceRef, order.PaymentRef,
            item.SupplierId, item.DepositAmount, item.FinalDeductionAmount!.Value,
            item.DepositRefundAmount, order.Currency,
            item.DeductionProposal?.Id.ToString("D") ?? $"return:{item.Id:D}", operationKey);
        var valid = request.Validate();
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (valid.IsFailure) return valid.Error;
        var payload = JsonSerializer.Serialize(new { providerRequestJson = JsonSerializer.Serialize(request) });
        order.AddOperationReceipt(RentalOperationReceipt.CreatePending(Guid.NewGuid(), order.Id,
            operationKey, RentalItem.SettleRentalAction, RentalOperationExecutor.ComputeFingerprint(request),
            clock.GetUtcNow(), targetItemId: item.Id, payloadJson: payload));
        await orders.UpdateAsync(order, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
