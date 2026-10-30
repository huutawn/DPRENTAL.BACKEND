using Dprental.RentalService.Application.Hubs;
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;

namespace Dprental.RentalService.Application.Services.Operations;

/// <summary>
/// Supplier nhận hoặc từ chối cả đơn con: mọi dòng của mình trong đơn cùng một quyết định.
/// Trạng thái vẫn nằm trên từng dòng; mỗi dòng có khóa idempotency riêng suy ra từ khóa của request.
/// </summary>
public sealed partial class RentalOperationExecutor
{
    // Khóa order và kiểm tra toàn bộ item của suborder trước khi tạo/tiếp tục accept; không áp quyết định sang supplier khác.
    public async Task<Result<IReadOnlyList<SupplierDecisionResponse>>> ExecuteSubOrderAcceptAsync(
        Guid orderId,
        Guid subOrderId,
        Guid supplierId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string normalizedKey = idempotencyKey.Trim();
        List<Guid> itemIds;

        // Kiểm tra mọi dòng trước khi gọi Inventory cho dòng nào, để một dòng không hợp lệ không làm đơn con bị nhận dở.
        await using (await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken))
        {
            // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
            var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
            List<RentalItem> items = order is null ? [] : SubOrderItems(order, subOrderId, supplierId);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (order is null || items.Count == 0)
            {
                return RentalErrors.SubOrderNotFound;
            }

            // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
            DateTimeOffset utcNow = _timeProvider.GetUtcNow();
            List<(RentalItem Item, string Key)> unclaimed = [];
            // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
            foreach (RentalItem item in items)
            {
                // Dòng đã có receipt của khóa này (xong hoặc đang dở) để bước nhận từng dòng tự trả lại hoặc làm tiếp.
                string itemKey = SubOrderItemKey("SubOrderAccept", subOrderId, normalizedKey, item.Id);
                // Kiểm tra thành viên trong tập liên quan để không áp dụng yêu cầu lên dữ liệu ngoài phạm vi đang xử lý.
                if (order.OperationReceipts.Any(r => r.OperationId == itemKey))
                {
                    continue;
                }

                // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
                Result acceptable = CheckDecidable(order, item, utcNow);
                // Tác vụ đã thành công không được tạo side effect lần nữa; ưu tiên trả kết quả đã ghi nhận.
                if (acceptable.IsSuccess && !IsHoldExtended(order, item))
                {
                    // Chưa có receipt ExtendHold thành công thì không cho accept; tránh xác nhận trên nhóm hold không đủ thời hạn.
                    acceptable = Error.Conflict("HOLD_NOT_EXTENDED", "Hold cho thiết bị chưa được gia hạn thành công (Succeeded).");
                }

                // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
                if (acceptable.IsFailure)
                {
                    return acceptable.Error;
                }

                // Gom các item chưa có intent accept để lưu toàn bộ intent trước khi gọi Inventory cho từng dòng.
                unclaimed.Add((item, itemKey));
            }

            // Ghi phiếu "đang nhận" cho mọi dòng trong cùng một lần lưu, trước hạn và trước khi gọi Inventory:
            // dòng nào chưa chốt kịp thì expiry worker không hủy mà để recovery worker chốt nốt trong thời gian hold còn sống.
            foreach (var (item, key) in unclaimed)
            {
                // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
                order.AddOperationReceipt(NewAcceptIntent(order, item, supplierId, key, utcNow));
            }

            // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
            if (unclaimed.Count > 0)
            {
                // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
                await CommitLocalChangesAsync(order, cancellationToken);
            }

            // Chọn, sắp xếp hoặc chiếu dữ liệu đã có thành tập phục vụ bước tiếp theo; không thay đổi nguồn dữ liệu gốc.
            itemIds = items.Select(i => i.Id).ToList();
        }

        // Inventory xác nhận từng dòng. Lỗi giữa chừng thì gửi lại cùng khóa sẽ làm tiếp các dòng còn thiếu.
        List<SupplierDecisionResponse> responses = [];
        // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
        foreach (Guid itemId in itemIds)
        {
            // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
            Result<SupplierDecisionResponse> accepted = await ExecuteSupplierAcceptAsync(
                orderId,
                itemId,
                supplierId,
                SubOrderItemKey("SubOrderAccept", subOrderId, normalizedKey, itemId),
                cancellationToken);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (accepted.IsFailure)
            {
                return accepted.Error;
            }

            // Tích lũy response theo các phần tử đã kiểm tra; danh sách này chỉ phục vụ kết quả của lần xử lý hiện tại.
            responses.Add(accepted.Value);
        }

        return Result.Success<IReadOnlyList<SupplierDecisionResponse>>(responses);
    }

    // Kiểm tra replay và khả năng từ chối toàn suborder trước khi đổi từng item và ghi các tác vụ bù trừ.
    public async Task<Result<IReadOnlyList<SupplierDecisionResponse>>> ExecuteSubOrderRejectAsync(
        Guid orderId,
        Guid subOrderId,
        Guid supplierId,
        string reason,
        string? note,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string normalizedKey = idempotencyKey.Trim();
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        List<RentalItem> items = order is null ? [] : SubOrderItems(order, subOrderId, supplierId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null || items.Count == 0)
        {
            return RentalErrors.SubOrderNotFound;
        }

        // Tạo key và fingerprint riêng cho từng item từ cùng request suborder để đối chiếu replay trước khi áp dụng bất kỳ dòng nào.
        var decisions = items
            .Select(item => (
                Item: item,
                Key: SubOrderItemKey("SubOrderReject", subOrderId, normalizedKey, item.Id),
                Fingerprint: ComputeFingerprint(new { Action = "SupplierReject", ItemId = item.Id, SupplierId = supplierId, Reason = reason, Note = note })))
            .ToList();

        // Từ chối lưu trọn trong một lần commit, nên lần gửi trước hoặc có đủ receipt cho mọi dòng hoặc không có receipt nào.
        var previous = decisions
            .Select(d => (Decision: d, Receipt: order.OperationReceipts.FirstOrDefault(r => r.OperationId == d.Key)))
            .ToList();
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (previous.Any(p => p.Receipt is not null))
        {
            return ReplayReject(previous.Select(p => (p.Decision.Item, p.Decision.Fingerprint, p.Receipt)).ToList());
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
        foreach (RentalItem item in items)
        {
            // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
            Result rejectable = CheckDecidable(order, item, utcNow);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (rejectable.IsFailure)
            {
                return rejectable.Error;
            }
        }

        // Mở transaction cho phần ghi local; các thay đổi và Outbox trong phạm vi này phải được commit/rollback cùng nhau.
        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        // Thực hiện phần ghi có xử lý lỗi: nhánh catch rollback/bỏ thay đổi theo ranh giới transaction của luồng này.
        try
        {
            // Giữ trạng thái hoặc loại thay đổi để so sánh trước/sau và chỉ phát đúng thông báo/event của nhánh.
            RentalOrderStatus previousOrderStatus = order.Status;
            List<SupplierDecisionResponse> responses = [];
            foreach (var (item, key, fingerprint) in decisions)
            {
                // Áp dụng quyết định trạng thái đã qua các điều kiện của luồng; sau đó lưu history/receipt và side effect liên quan.
                responses.Add(await ApplySupplierRejectAsync(order, item, supplierId, reason, note, key, fingerprint, utcNow, cancellationToken, rollup: false, enqueueEvent: false));
            }

            // Tổng hợp lại trạng thái order từ các item sau quyết định; không ép các item khác đi theo một trạng thái chung.
            RentalOrderStatusPolicy.Apply(order, utcNow, normalizedKey);
            order.RecordReadModelUpdate(utcNow);

            // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
            foreach (var (item, key, _) in decisions)
            {
                // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                var rejectRes = await _rentalEvents.EnqueueRentalItemRejectedAsync(
                    new RentalItemRejectedPayload(order.Id, item.Id, supplierId, utcNow, KafkaContractSpec.ReasonCodes.SupplierRejected, null, order.Version),
                    correlationId: key,
                    cancellationToken: cancellationToken);
                // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
                if (rejectRes.IsFailure)
                {
                    // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return rejectRes.Error;
                }
            }

            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (previousOrderStatus != RentalOrderStatus.Completed && order.Status == RentalOrderStatus.Completed)
            {
                // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                var orderCompletedRes = await _rentalEvents.EnqueueRentalOrderCompletedAsync(
                    new RentalOrderCompletedPayload(order.Id, order.RenterId, utcNow, order.Version),
                    correlationId: normalizedKey,
                    cancellationToken: cancellationToken);
                // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
                if (orderCompletedRes.IsFailure)
                {
                    // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return orderCompletedRes.Error;
                }
            }

            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (previousOrderStatus != RentalOrderStatus.Rejected && order.Status == RentalOrderStatus.Rejected)
            {
                // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                var orderRejected = await _rentalEvents.EnqueueRentalOrderRejectedAsync(
                    new RentalOrderRejectedPayload(order.Id, order.RenterId, utcNow, order.Version),
                    correlationId: normalizedKey,
                    cancellationToken: cancellationToken);
                // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
                if (orderRejected.IsFailure)
                {
                    // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return orderRejected.Error;
                }
            }

            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (previousOrderStatus != RentalOrderStatus.Expired && order.Status == RentalOrderStatus.Expired)
            {
                // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                var orderExpired = await _rentalEvents.EnqueueRentalOrderExpiredAsync(
                    new RentalOrderExpiredPayload(order.Id, order.RenterId, utcNow, KafkaContractSpec.ReasonCodes.SupplierConfirmationExpired, order.Version),
                    correlationId: normalizedKey,
                    cancellationToken: cancellationToken);
                // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
                if (orderExpired.IsFailure)
                {
                    // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return orderExpired.Error;
                }
            }

            // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
            await CommitLocalChangesAsync(order, cancellationToken);
            await tx.CommitAsync(cancellationToken);

            // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
            if (_notifier != null)
            {
                // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                await _notifier.NotifyRenterOrderChangedAsync(
                    order.RenterId,
                    new RenterOrderChangedNotification(
                        Guid.NewGuid(),
                        order.Id,
                        order.Version,
                        OrderChangeTypes.SupplierRejected,
                        utcNow),
                    cancellationToken);

                // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                await _notifier.NotifySupplierSubOrderChangedAsync(
                    supplierId,
                    new SupplierSubOrderChangedNotification(
                        Guid.NewGuid(),
                        order.Id,
                        subOrderId,
                        order.Version,
                        OrderChangeTypes.SupplierRejected,
                        utcNow),
                    cancellationToken);
            }
            return Result.Success<IReadOnlyList<SupplierDecisionResponse>>(responses);
        }
        // Lỗi trong phạm vi ghi: rollback transaction và bỏ graph đang tracked trước khi trả lỗi hoặc ném lại exception.
        catch
        {
            // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();
            throw;
        }
    }

    // Chỉ lấy item thuộc đúng suborder trong order đang xử lý và sắp xếp ổn định.
    private static List<RentalItem> SubOrderItems(RentalOrder order, Guid subOrderId, Guid supplierId)
    {
        // Chọn đúng item của suborder và sắp theo ID; kiểm tra supplier bên dưới bảo vệ việc truy cập suborder của người khác.
        List<RentalItem> items = order.Items
            .Where(i => i.RentalSubOrderId == subOrderId)
            .OrderBy(i => i.Id)
            .ToList();

        // Đơn con của supplier khác trả như không tồn tại để không lộ dữ liệu.
        return items.Count > 0 && items.All(i => RentalAuthorizationPolicy.AuthorizeSupplierItem(supplierId, i).IsSuccess)
            ? items
            : [];
    }

    // Sinh key item từ key quyết định suborder để mỗi dòng có journal riêng nhưng retry cùng quyết định vẫn ổn định.
    private static string SubOrderItemKey(string action, Guid subOrderId, string idempotencyKey, Guid itemId) =>
        ComputeOperationKey(action, $"{subOrderId}:{idempotencyKey}:{itemId}");

    /// <summary>Cùng các điều kiện mà bước nhận/từ chối từng dòng kiểm tra.</summary>
    private static Result CheckDecidable(RentalOrder order, RentalItem item, DateTimeOffset utcNow)
    {
        // Bảo vệ thao tác đang chạy hoặc chưa rõ kết quả; cần đối soát trước khi tạo quyết định có thể mâu thuẫn.
        if (order.HasInFlightAccept(item.Id))
        {
            return Error.Conflict("DECISION_IN_PROGRESS", "Yêu cầu chấp nhận đang được xử lý hoặc chờ đối soát; không thể thực hiện thao tác khác.");
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (item.Status != RentalItemStatus.PendingSupplierConfirmation)
        {
            return Error.Conflict("ITEM_ALREADY_DECIDED", "Mục thuê đã được quyết định với khóa idempotency khác.");
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (order.Status != RentalOrderStatus.Paid)
        {
            return Error.Conflict("ORDER_NOT_PAID", "Đơn hàng chưa được thanh toán.");
        }

        // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
        if (item.SupplierConfirmationDeadline.HasValue && utcNow >= item.SupplierConfirmationDeadline.Value)
        {
            return Error.Expired("SUPPLIER_DEADLINE_EXPIRED", "Đã quá thời hạn xác nhận của supplier.");
        }

        return Result.Success();
    }

    // Đọc receipt gia hạn thành công để không accept trên hold chưa được kéo dài đủ điều kiện.
    private static bool IsHoldExtended(RentalOrder order, RentalItem item)
    {
        // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
        string holdOpKey = ComputeOperationKey("ExtendHold", $"{order.Id}:{item.Id}");
        var holdReceipt = order.OperationReceipts.FirstOrDefault(r =>
            r.OperationId == holdOpKey || (r.Action == "ExtendHold" && r.TargetItemId == item.Id));
        return holdReceipt is { IsSuccess: true, Status: RentalOperationReceipt.StatusSucceeded };
    }

    // Khôi phục kết quả reject theo receipt đã lưu, không chạy lại release/refund cho một quyết định đã xử lý.
    private static Result<IReadOnlyList<SupplierDecisionResponse>> ReplayReject(
        List<(RentalItem Item, string Fingerprint, RentalOperationReceipt? Receipt)> previous)
    {
        // Tích lũy response theo các phần tử đã kiểm tra; danh sách này chỉ phục vụ kết quả của lần xử lý hiện tại.
        List<SupplierDecisionResponse> responses = [];
        foreach (var (item, fingerprint, receipt) in previous)
        {
            // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
            if (receipt is null ||
                receipt.Action != "SupplierReject" ||
                receipt.TargetItemId != item.Id ||
                !string.Equals(receipt.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase) ||
                !receipt.IsSuccess ||
                receipt.Status != RentalOperationReceipt.StatusSucceeded)
            {
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa idempotency đã được sử dụng với hành động hoặc nội dung khác.");
            }

            // Chuẩn bị replay từ API response đã lưu; nếu không đọc được thì trả lỗi journal thay vì dựng kết quả mới.
            SupplierDecisionResponse? stored = null;
            // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
            try
            {
                // Đọc response đã lưu của từng receipt reject; thiếu/hỏng response phải báo lỗi journal thay vì tạo quyết định reject mới.
                stored = string.IsNullOrWhiteSpace(receipt.ApiResponseJson)
                    ? null
                    : JsonSerializer.Deserialize<SupplierDecisionResponse>(receipt.ApiResponseJson, JsonOptions);
            }
            // Giữ nhánh bỏ qua lỗi đọc dữ liệu cũ: phần sau phải kiểm tra lại kết quả và chọn fallback/lỗi, không coi parse lỗi là dữ liệu hợp lệ.
            catch (JsonException)
            {
            }

            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (stored is null)
            {
                return Error.Failure("CORRUPTED_JOURNAL_RESPONSE", "Dữ liệu phản hồi trong journal không hợp lệ.");
            }

            // Tích lũy response theo các phần tử đã kiểm tra; danh sách này chỉ phục vụ kết quả của lần xử lý hiện tại.
            responses.Add(stored);
        }

        return Result.Success<IReadOnlyList<SupplierDecisionResponse>>(responses);
    }
}
