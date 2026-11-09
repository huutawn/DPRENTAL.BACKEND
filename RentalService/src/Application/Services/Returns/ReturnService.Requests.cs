using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.Hubs;
using Microsoft.Extensions.Logging;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Application.Services.Returns;

/// <remarks>
/// Nhánh renter tạo yêu cầu trả máy: kiểm tra quyền, assignment và yêu cầu đang mở trước khi lưu.
/// Yêu cầu trả là bước trao đổi với supplier; chưa phải biên bản đã nhận máy hoặc kết quả đóng allocation.
/// </remarks>
public sealed partial class ReturnService
{
    private const string RequestReturnAction = "RequestReturn";
    private static readonly JsonSerializerOptions ReceiptJsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Renter gửi yêu cầu trả từng unit và bằng chứng; lưu request/receipt cùng lần SaveChanges, chưa coi supplier đã nhận máy.
    public async Task<Result<ReturnRequestResponse>> CreateReturnRequestAsync(Guid itemId,
        IRentalCallerContext caller, CreateReturnRequest request, string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(caller);
        // Từ chối key rỗng hoặc vượt giới hạn lưu trữ để mỗi thao tác có thể được nhận diện khi client gửi lại.
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key is required (maximum 64 characters).");
        // Kiểm tra số lượng bằng chứng và tính hợp lệ của ID trước khi gọi Media xác minh quyền sử dụng.
        if (request?.Units is null || request.Units.Count == 0 ||
            request.Units.Any(u => u is null || u.RentalUnitAssignmentId == Guid.Empty || u.MediaIds is null) ||
            request.Units.Select(u => u.RentalUnitAssignmentId).Distinct().Count() != request.Units.Count)
            return Error.Validation("ReturnRequest.InvalidUnits", "Specify distinct assignments and media collections.");
        // Dừng nhánh không hợp lệ với mã ReturnRequest.InvalidRequest; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (request.ActualReturnAt == default || request.Note?.Length > 2000)
            return Error.Validation("ReturnRequest.InvalidRequest", "Invalid return time or note.");

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var orderId = await repository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (!orderId.HasValue) return RentalErrors.ItemNotFound;
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);
        var order = await repository.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null) return RentalErrors.ItemNotFound;
        var authorization = RentalAuthorizationPolicy.AuthorizeRenterAction(caller, order);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (authorization.IsFailure)
            return authorization.Error.Type == ErrorType.NotFound ? RentalErrors.ItemNotFound : authorization.Error;
        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (item is null) return RentalErrors.ItemNotFound;

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string key = idempotencyKey.Trim();
        string payload = JsonSerializer.Serialize(new
        {
            ItemId = itemId,
            order.RenterId,
            ActualReturnAt = request.ActualReturnAt.ToUniversalTime(),
            request.Note,
            Units = request.Units.OrderBy(u => u.RentalUnitAssignmentId).Select(u => new
            {
                u.RentalUnitAssignmentId,
                MediaIds = (u.MediaIds ?? []).OrderBy(id => id)
            })
        });
        string fingerprint = RentalOperationExecutor.ComputeFingerprint(payload);
        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == key);
        // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
        if (receipt is not null)
        {
            // Đối chiếu action, phạm vi và fingerprint; cùng key nhưng khác yêu cầu phải báo conflict thay vì dùng kết quả cũ.
            if (receipt.Action != RequestReturnAction || receipt.TargetItemId != itemId || receipt.Fingerprint != fingerprint)
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "This key was used for another request.");
            var saved = string.IsNullOrWhiteSpace(receipt.ApiResponseJson) ? null :
                JsonSerializer.Deserialize<ReturnRequestResponse>(receipt.ApiResponseJson, ReceiptJsonOptions);
            return saved is null ? Error.Failure("INVALID_RETURN_RECEIPT", "Stored response is unavailable.") : saved;
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        var now = timeProvider.GetUtcNow();
        var validation = ValidateReturnRequest(item, request, now);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (validation.IsFailure) return validation.Error;
        // Xử lý từng unit/assignment trong tập đã chọn; giữ định danh từng máy khi kiểm tra và ghi bằng chứng.
        foreach (var unit in request.Units!)
        {
            // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
            var evidence = await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(
                unit.MediaIds, order.RenterId, mediaClient, isRequired: true, ct: cancellationToken);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (evidence.IsFailure) return evidence.Error;
        }

        // Cấp ID cho bản ghi mới của lần xử lý; ID được đưa vào journal/snapshot để các bước tiếp tục tham chiếu đúng đối tượng.
        Guid requestId = Guid.NewGuid();
        var record = new ReturnRequest(requestId, itemId, request.ActualReturnAt.ToUniversalTime(),
            request.Note, order.RenterId, now, request.Units.Select(u =>
                new ReturnRequestUnit(Guid.NewGuid(), requestId, u.RentalUnitAssignmentId, u.MediaIds ?? [])));
        var response = new ReturnRequestResponse(
            record.Id,
            itemId,
            record.ActualReturnAt,
            record.Note,
            record.SubmittedAt,
            record.Units
                .OrderBy(u => u.RentalUnitAssignmentId)
                .Select(u => new ReturnRequestUnitResponse(
                    u.RentalUnitAssignmentId,
                    u.MediaIds,
                    u.ClosedAt,
                    u.RejectedAt,
                    ReturnRequestUnitStatus.PendingSupplierConfirmation,
                    false,
                    u.OverdueNotifiedAt))
                .ToArray(),
            ReturnRequestStatus.PendingSupplierConfirmation,
            record.ConfirmationDeadlineAt,
            false,
            [],
            []);
        item.AddReturnRequest(record);
        order.RecordReadModelUpdate(now);
        order.AddOperationReceipt(new RentalOperationReceipt(Guid.NewGuid(), order.Id, key, RequestReturnAction,
            fingerprint, true, null, null, now, targetItemId: itemId, payloadJson: payload,
            apiResponseJson: JsonSerializer.Serialize(response)));
        // Thực hiện lời gọi có thể phát sinh exception; nhánh catch phía dưới quyết định trả lỗi, retry hoặc chuyển tiếp exception.
        try
        {
            // SaveChanges commits request, units and receipt atomically, after media calls have finished.
            await repository.UpdateAsync(order, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        // Bỏ thay đổi tracked khi lưu thất bại; giữ cách trả conflict hoặc chuyển tiếp exception của use case.
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Bỏ thay đổi đang tracked sau lỗi để lần xử lý tiếp theo không lưu nhầm dữ liệu của nhánh đã thất bại.
            unitOfWork.DiscardChanges();
            return Error.Conflict("RETURN_REQUEST_ALREADY_OPEN", "This unit already has an open return request.");
        }
        // Bỏ thay đổi tracked khi lưu thất bại; giữ cách trả conflict hoặc chuyển tiếp exception của use case.
        catch
        {
            // Bỏ thay đổi đang tracked sau lỗi để lần xử lý tiếp theo không lưu nhầm dữ liệu của nhánh đã thất bại.
            unitOfWork.DiscardChanges();
            throw;
        }

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
                        OrderChangeTypes.ReturnRequested,
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
                            OrderChangeTypes.ReturnRequested,
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
                    LogReturnRequestedNotificationFailed(logger, ex, order.Id);
                }
            }
        }

        return response;
    }

    // Kiểm tra trạng thái thuê, thời điểm và eligibility từng assignment, gồm yêu cầu trả còn mở để tránh gửi trùng unit.
    private static Result ValidateReturnRequest(RentalItem item, CreateReturnRequest request, DateTimeOffset now)
    {
        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (item.Status != RentalItemStatus.InRental) return RentalErrors.InvalidItemStatusForReturn;
        // Điều kiện này quyết định có thể tiếp tục hay phải trả RentalErrors.FutureReturnTimeNotAllowed theo contract của use case.
        if (request.ActualReturnAt > now) return RentalErrors.FutureReturnTimeNotAllowed;
        var assignments = item.UnitAssignments.ToDictionary(u => u.Id);
        var openUnits = ReturnEligibilityPolicy.GetOpenReturnRequestAssignmentIds(item);
        // Xử lý từng unit/assignment trong tập đã chọn; giữ định danh từng máy khi kiểm tra và ghi bằng chứng.
        foreach (var unit in request.Units!)
        {
            // Chỉ xử lý ID có trong tập đã tra cứu; thiếu mapping phải báo lỗi thay vì gắn sang phần tử khác.
            if (!assignments.TryGetValue(unit.RentalUnitAssignmentId, out var assignment))
                return RentalErrors.AssignmentNotFound;
            var validation = ReturnEligibilityPolicy.ValidateAssignmentForReturnRequest(item, assignment, request.ActualReturnAt, openUnits);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (validation.IsFailure) return validation.Error;
        }
        return Result.Success();
    }

    // Ghi cảnh báo khi gửi thông báo yêu cầu trả thất bại mà không ghi nội dung Media vào log.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send ReturnRequested notification for order {OrderId}")]
    private static partial void LogReturnRequestedNotificationFailed(ILogger logger, Exception ex, Guid orderId);
}
