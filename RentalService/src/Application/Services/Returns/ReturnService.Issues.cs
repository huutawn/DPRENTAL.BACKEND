using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Returns;

/// <remarks>
/// Nhánh báo vấn đề với yêu cầu trả máy, dùng chung repository, khóa và notifier của ReturnService.
/// Lưu diễn biến để xử lý tiếp; báo vấn đề không tự xác nhận máy đã trả hoặc cho phép Inventory cấp phát lại.
/// </remarks>
public sealed partial class ReturnService
{
    private const string ReportReturnIssueAction = "ReportReturnIssue";

    // Renter hoặc supplier báo tranh chấp theo unit của yêu cầu trả; kiểm tra quyền, replay, bằng chứng và trạng thái có thể xử lý trước khi lưu issue.
    public async Task<Result<ReturnRequestResponse>> ReportReturnIssueAsync(
        Guid itemId,
        Guid returnRequestId,
        IRentalCallerContext caller,
        ReportReturnIssueRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(caller);
        // Kiểm tra danh tính và loại caller trước khi dùng user ID để xác định phạm vi dữ liệu được phép truy cập.
        if (!caller.IsAuthenticated || caller.IsService || !caller.UserId.HasValue)
        {
            // Kiểm tra danh tính và loại caller trước khi dùng user ID để xác định phạm vi dữ liệu được phép truy cập.
            if (caller.IsService)
                return Error.Forbidden("FORBIDDEN", "Service token không được báo issue trả máy.");
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu đăng nhập.");
        }
        Guid callerUserId = caller.UserId.Value;
        // Từ chối key rỗng hoặc vượt giới hạn lưu trữ để mỗi thao tác có thể được nhận diện khi client gửi lại.
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key is required (maximum 64 characters).");

        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (request is null)
            return Error.Validation("ReturnIssue.InvalidRequest", "Request body is required.");
        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500)
            return Error.Validation("ReturnIssue.InvalidReason", "Reason is required and cannot exceed 500 characters.");
        // Dừng nhánh không hợp lệ với mã ReturnIssue.InvalidNote; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (request.Note?.Length > 2000)
            return Error.Validation("ReturnIssue.InvalidNote", "Note cannot exceed 2000 characters.");
        // Kiểm tra số lượng bằng chứng và tính hợp lệ của ID trước khi gọi Media xác minh quyền sử dụng.
        if (request.MediaIds != null && request.MediaIds.Count > 10)
            return Error.Validation("ReturnIssue.TooManyMediaIds", "Maximum 10 media IDs are allowed.");
        // Nếu client chọn unit cụ thể thì kiểm tra danh sách; nhánh không chỉ định sẽ chọn tập actionable theo quy tắc bên dưới.
        if (request.AssignmentIds != null)
        {
            // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
            if (request.AssignmentIds.Count == 0)
                return Error.Validation("ReturnIssue.EmptyAssignmentIds", "AssignmentIds must not be empty when provided.");
            // Kiểm tra thành viên trong tập liên quan để không áp dụng yêu cầu lên dữ liệu ngoài phạm vi đang xử lý.
            if (request.AssignmentIds.Any(id => id == Guid.Empty))
                return Error.Validation("ReturnIssue.InvalidAssignmentId", "AssignmentId cannot be empty GUID.");
            // Kiểm tra tính duy nhất của danh sách ID để một unit hoặc bản ghi không được tính/xử lý nhiều lần trong cùng yêu cầu.
            if (request.AssignmentIds.Distinct().Count() != request.AssignmentIds.Count)
                return Error.Validation("ReturnIssue.DuplicateAssignmentIds", "Duplicate assignment IDs are not allowed.");
        }

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string key = idempotencyKey.Trim();
        string assignmentsFingerprint = (request.AssignmentIds != null && request.AssignmentIds.Count > 0)
            ? string.Join(",", request.AssignmentIds.Distinct().OrderBy(id => id))
            : "ALL_ACTIONABLE";
        string mediaFingerprint = (request.MediaIds != null && request.MediaIds.Count > 0)
            ? string.Join(",", request.MediaIds.Distinct().OrderBy(id => id))
            : "";
        string payload = JsonSerializer.Serialize(new
        {
            ItemId = itemId,
            ReturnRequestId = returnRequestId,
            ReporterId = callerUserId,
            Reason = request.Reason.Trim(),
            Note = request.Note,
            MediaIds = mediaFingerprint,
            AssignmentIds = assignmentsFingerprint
        });
        string fingerprint = RentalOperationExecutor.ComputeFingerprint(payload);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var orderId = await repository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (!orderId.HasValue) return RentalErrors.ItemNotFound;

        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);
        var order = await repository.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null) return RentalErrors.ItemNotFound;

        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (item is null) return RentalErrors.ItemNotFound;

        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == key);
        // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
        if (receipt is not null)
        {
            // Đối chiếu action, phạm vi và fingerprint; cùng key nhưng khác yêu cầu phải báo conflict thay vì dùng kết quả cũ.
            if (receipt.Action != ReportReturnIssueAction || receipt.TargetItemId != itemId || receipt.Fingerprint != fingerprint)
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "This key was used for another request.");
            var saved = string.IsNullOrWhiteSpace(receipt.ApiResponseJson) ? null :
                JsonSerializer.Deserialize<ReturnRequestResponse>(receipt.ApiResponseJson, ReceiptJsonOptions);
            return saved is null ? Error.Failure("INVALID_RETURN_RECEIPT", "Stored response is unavailable.") : saved;
        }

        // Giữ định danh/vai trò đã xác định cho thao tác để audit và các bước tiếp theo dùng cùng chủ thể.
        ActorRole reporterRole;
        // Xác định vai trò theo quyền sở hữu thực tế của order/item, không nhận vai trò tùy ý từ request.
        if (callerUserId == order.RenterId)
        {
            // Giữ định danh/vai trò đã xác định cho thao tác để audit và các bước tiếp theo dùng cùng chủ thể.
            reporterRole = ActorRole.Renter;
        }
        else if (callerUserId == item.SupplierId)
        {
            // Giữ định danh/vai trò đã xác định cho thao tác để audit và các bước tiếp theo dùng cùng chủ thể.
            reporterRole = ActorRole.Supplier;
        }
        else
        {
            return RentalErrors.ItemNotFound;
        }

        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
        var returnReq = item.ReturnRequests.FirstOrDefault(r => r.Id == returnRequestId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (returnReq is null)
            return Error.NotFound("RETURN_REQUEST_NOT_FOUND", "Return request not found on this item.");

        // Chọn, sắp xếp hoặc chiếu dữ liệu đã có thành tập phục vụ bước tiếp theo; không thay đổi nguồn dữ liệu gốc.
        var reqUnitsByAssign = returnReq.Units.ToDictionary(u => u.RentalUnitAssignmentId);
        List<Guid> targetAssignmentIds = [];

        // Nếu client chọn unit cụ thể thì kiểm tra danh sách; nhánh không chỉ định sẽ chọn tập actionable theo quy tắc bên dưới.
        if (request.AssignmentIds != null && request.AssignmentIds.Count > 0)
        {
            // Duyệt tập dữ liệu của bước hiện tại để tích lũy kết quả, giữ nguyên phạm vi và thứ tự mà caller đã chọn.
            foreach (var assignId in request.AssignmentIds)
            {
                // Dừng nhánh không hợp lệ với mã ReturnIssue.AssignmentNotInRequest; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
                if (!reqUnitsByAssign.TryGetValue(assignId, out var unit))
                    return Error.Validation("ReturnIssue.AssignmentNotInRequest", $"Assignment {assignId} does not belong to this return request.");
                // Dừng nhánh không hợp lệ với mã ReturnIssue.UnitNotActionable; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
                if (!unit.IsActionable)
                    return Error.Conflict("ReturnIssue.UnitNotActionable", $"Assignment {assignId} is already closed or rejected.");
                targetAssignmentIds.Add(assignId);
            }
        }
        else
        {
            // Chọn, sắp xếp hoặc chiếu dữ liệu đã có thành tập phục vụ bước tiếp theo; không thay đổi nguồn dữ liệu gốc.
            targetAssignmentIds = returnReq.Units.Where(u => u.IsActionable).Select(u => u.RentalUnitAssignmentId).ToList();
            // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
            if (targetAssignmentIds.Count == 0)
                return Error.Conflict("ReturnIssue.NoActionableUnits", "No actionable units remain in this return request.");
        }

        // Kiểm tra số lượng bằng chứng và tính hợp lệ của ID trước khi gọi Media xác minh quyền sử dụng.
        if (request.MediaIds != null && request.MediaIds.Count > 0)
        {
            // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
            var evidence = await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(
                request.MediaIds, callerUserId, mediaClient, ct: cancellationToken);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (evidence.IsFailure) return evidence.Error;
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        var now = timeProvider.GetUtcNow();
        var issue = new ReturnRequestIssue(
            Guid.NewGuid(),
            returnRequestId,
            callerUserId,
            reporterRole,
            request.Reason.Trim(),
            request.Note?.Trim(),
            request.MediaIds ?? [],
            now,
            targetAssignmentIds);

        // Gắn chứng từ/bằng chứng vào đúng yêu cầu trả và tập unit đã chọn, giữ lịch sử cần cho đối soát sau đó.
        returnReq.AddIssue(issue);
        order.RecordReadModelUpdate(now);

        // Dùng policy/mapper hiện có để dựng dữ liệu response nhất quán với các điểm đọc khác của cùng nghiệp vụ.
        var response = OrderQueryMapper.ToReturnRequests(item, now, includeAdminNotes: false)
            .First(r => r.ReturnRequestId == returnRequestId);

        // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
        order.AddOperationReceipt(new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            key,
            ReportReturnIssueAction,
            fingerprint,
            true,
            null,
            null,
            now,
            targetItemId: itemId,
            payloadJson: payload,
            apiResponseJson: JsonSerializer.Serialize(response)));

        // Đăng ký thay đổi aggregate với repository trước khi lưu; không truy cập DbContext trực tiếp từ service.
        await repository.UpdateAsync(order, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
        if (notifier != null)
        {
            // Gửi thông báo theo chính sách bắt lỗi của luồng; đây là bước sau thay đổi nghiệp vụ, không phải bằng chứng provider đã xử lý.
            try
            {
                // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                await notifier.NotifyRenterOrderChangedAsync(
                    order.RenterId,
                    new RenterOrderChangedNotification(
                        Guid.NewGuid(),
                        order.Id,
                        order.Version,
                        OrderChangeTypes.ReturnDisputed,
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
                            OrderChangeTypes.ReturnDisputed,
                            now),
                        cancellationToken);
                }
            }
            // Thông báo thất bại sau bước lưu: ghi cảnh báo để theo dõi, không tạo lại quyết định nghiệp vụ đã được ghi nhận.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (logger != null)
                {
                    // Ghi log/audit của bước xử lý bằng metadata; không coi log là nguồn trạng thái nghiệp vụ.
                    LogReturnIssueNotificationFailed(logger, ex, order.Id);
                }
            }
        }

        return response;
    }

    // Ghi cảnh báo khi thông báo issue thất bại, tách lỗi gửi thông báo với dữ liệu issue đã lưu.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send ReturnDisputed notification for order {OrderId}")]
    private static partial void LogReturnIssueNotificationFailed(ILogger logger, Exception ex, Guid orderId);
}
