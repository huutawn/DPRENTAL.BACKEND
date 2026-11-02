using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.IServices.Returns;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Common;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Application.DTOs.Integration;
using Microsoft.Extensions.Configuration;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.Kafka.Producers;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Returns;

/// <remarks>
/// Luồng supplier ghi nhận trả máy: xác thực/quyền sở hữu → khóa order → kiểm tra idempotency và từng unit
/// → xác minh bằng chứng Media → lưu ReturnRecord, receipt, lệnh ApplyReturnedUnits và event trong cùng transaction.
/// Ghi nhận trả tại Rental chưa xác nhận allocation đã đóng; InventoryReturnResultHandler xử lý xác nhận riêng.
/// Các file Requests, Issues và Admin là các nhánh của cùng ReturnService, dùng chung phần xác nhận trả.
/// </remarks>
public sealed partial class ReturnService(
    IRentalOrderRepository repository,
    IOrderLockManager locks,
    IUnitOfWork unitOfWork,
    IRentalMediaClient mediaClient,
    TimeProvider timeProvider,
    IRentalEventProducer rentalEvents,
    IConfiguration configuration,
    IInventoryCommandProducer inventoryCommands,
    IOrderHubNotifier? notifier = null,
    ILogger<ReturnService>? logger = null) : IReturnService
{

    // Supplier ghi biên bản nhận lại các unit; fingerprint chuẩn hóa thứ tự để nhận biết retry cùng nội dung.
    // Response này xác nhận Rental đã ghi biên bản, chưa xác nhận Inventory đã đóng allocation.
    public async Task<Result<RecordReturnResponse>> RecordReturnAsync(Guid itemId, IRentalCallerContext caller,
        RecordReturnRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(caller);
        Result<Guid> supplier = RentalAuthorizationPolicy.AuthorizeSupplierCaller(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (supplier.IsFailure) return supplier.Error;
        // Từ chối key rỗng hoặc vượt giới hạn lưu trữ để mỗi thao tác có thể được nhận diện khi client gửi lại.
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key is required (maximum 64 characters).");
        // Kiểm tra số lượng bằng chứng và tính hợp lệ của ID trước khi gọi Media xác minh quyền sử dụng.
        if (request?.Units is null || request.Units.Count == 0 || request.Units.Any(u => u is null || u.MediaIds is null))
            return Error.Validation("Return.InvalidUnits", "Return units and media collections are required.");
        bool kafkaEnabled = configuration.GetValue("Kafka:Enabled", true);
        // Dừng nhánh không hợp lệ với mã KAFKA_UNAVAILABLE; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (!kafkaEnabled)
        {
            return Error.ServiceUnavailable("KAFKA_UNAVAILABLE", "Kafka service is disabled or unavailable.");
        }
        Guid supplierId = supplier.Value;
        var orderId = await repository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (!orderId.HasValue) return RentalErrors.ItemNotFound;
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);
        var order = await repository.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null) return RentalErrors.ItemNotFound;
        Result<RentalItem> owned = RentalAuthorizationPolicy.AuthorizeSupplierItem(
            supplierId, order.Items.FirstOrDefault(i => i.Id == itemId));
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (owned.IsFailure) return owned.Error;
        RentalItem item = owned.Value;
        string key = idempotencyKey.Trim();
        string payload = JsonSerializer.Serialize(new
        {
            ItemId = itemId,
            SupplierId = supplierId,
            ActualReturnAt = request.ActualReturnAt.ToUniversalTime(),
            request.Note,
            Units = request.Units.OrderBy(u => u.RentalUnitAssignmentId).Select(u => new
            {
                u.RentalUnitAssignmentId,
                u.Condition,
                u.Description,
                MediaIds = u.MediaIds.OrderBy(id => id)
            })
        });
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        // Kiểm tra retry trước khi chạy lại điều kiện trả: unit có thể đã được ghi nhận
        // ở lần đầu. Response lưu trong receipt giữ nguyên kết quả của lần gửi đó.
        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == key);
        // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
        if (receipt is not null)
        {
            // Đối chiếu action, phạm vi và fingerprint; cùng key nhưng khác yêu cầu phải báo conflict thay vì dùng kết quả cũ.
            if (receipt.Action != "RecordReturn" || receipt.TargetItemId != itemId || receipt.Fingerprint != fingerprint)
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "This key was used for another request.");
            // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
            if (!receipt.IsSuccess || string.IsNullOrWhiteSpace(receipt.ApiResponseJson))
                return Error.Conflict("RETURN_IN_PROGRESS", "The return operation is not complete.");
            var saved = JsonSerializer.Deserialize<RecordReturnResponse>(receipt.ApiResponseJson);
            return saved is null ? Error.Failure("INVALID_RETURN_RECEIPT", "Stored response is unavailable.") : saved;
        }
        var coreResult = await ExecuteReturnConfirmationCoreAsync(
            order,
            item,
            request.ActualReturnAt,
            request.Note,
            request.Units,
            supplierId,
            ActorRole.Supplier,
            key,
            fingerprint,
            payload,
            "RecordReturn",
            "RecordReturn",
            "Supplier recorded returned units",
            null,
            (rec, processedAt) => JsonSerializer.Serialize(new RecordReturnResponse(
                rec.Id,
                processedAt,
                item.UnitAssignments.Count(a => !a.ReturnRecordId.HasValue),
                item.Status,
                item.DeductionDeadline)),
            [OrderChangeTypes.ReturnSubmitted],
            cancellationToken);

        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (coreResult.IsFailure) return coreResult.Error;
        var (record, now) = coreResult.Value;
        return new RecordReturnResponse(
            record.Id,
            now,
            item.UnitAssignments.Count(a => !a.ReturnRecordId.HasValue),
            item.Status,
            item.DeductionDeadline);
    }

    // Validate từng unit/Media, ghi biên bản và đóng phần request tương ứng; lưu receipt, command Inventory và event cùng transaction.
    private async Task<Result<(ReturnRecord Record, DateTimeOffset ProcessedAt)>> ExecuteReturnConfirmationCoreAsync(
        RentalOrder order,
        RentalItem item,
        DateTimeOffset actualReturnAt,
        string? note,
        List<ReturnUnitRequest> units,
        Guid actorId,
        ActorRole actorRole,
        string idempotencyKey,
        string fingerprint,
        string payloadJson,
        string operationAction,
        string transitionAction,
        string transitionNote,
        Action<ReturnRecord, DateTimeOffset>? onBeforeCommit,
        Func<ReturnRecord, DateTimeOffset, string> buildApiResponseJson,
        IReadOnlyList<string> notificationChangeTypes,
        CancellationToken cancellationToken)
    {
        // Luồng cần command Inventory qua Kafka; kiểm tra cấu hình trước khi ghi trạng thái không thể được đồng bộ sang Inventory.
        bool kafkaEnabled = configuration.GetValue("Kafka:Enabled", true);
        // Dừng nhánh không hợp lệ với mã KAFKA_UNAVAILABLE; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (!kafkaEnabled)
        {
            return Error.ServiceUnavailable("KAFKA_UNAVAILABLE", "Kafka service is disabled or unavailable.");
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (item.Status != RentalItemStatus.InRental)
            return RentalErrors.InvalidItemStatusForReturn;

        // Dừng nhánh không hợp lệ với mã Return.InvalidRequest; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (actualReturnAt == default || note?.Length > 2000)
            return Error.Validation("Return.InvalidRequest", "Invalid return time or note.");

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        var now = timeProvider.GetUtcNow();
        // Điều kiện này quyết định có thể tiếp tục hay phải trả RentalErrors.FutureReturnTimeNotAllowed theo contract của use case.
        if (actualReturnAt > now)
            return RentalErrors.FutureReturnTimeNotAllowed;

        // Kiểm tra tính duy nhất của danh sách ID để một unit hoặc bản ghi không được tính/xử lý nhiều lần trong cùng yêu cầu.
        if (units.Select(u => u.RentalUnitAssignmentId).Distinct().Count() != units.Count)
            return Error.Validation("Return.InvalidUnits", "Specify distinct assignments to return.");

        // Xử lý từng unit/assignment trong tập đã chọn; giữ định danh từng máy khi kiểm tra và ghi bằng chứng.
        foreach (var unit in units)
        {
            // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
            var assignment = item.UnitAssignments.FirstOrDefault(a => a.Id == unit.RentalUnitAssignmentId);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (assignment is null)
                return RentalErrors.AssignmentNotFound;

            // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
            var eligibility = ReturnEligibilityPolicy.ValidateAssignmentEligibleForReturn(item, assignment, actualReturnAt);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (eligibility.IsFailure)
                return eligibility.Error;

            // Dừng nhánh không hợp lệ với mã Return.InvalidEvidence; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
            if (!Enum.IsDefined(unit.Condition))
                return Error.Validation("Return.InvalidEvidence", "Invalid condition, description or media IDs.");

            // Dừng nhánh không hợp lệ với mã Return.InvalidEvidence; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
            if (unit.Description?.Length > 2000)
                return Error.Validation("Return.InvalidEvidence", "Invalid condition, description or media IDs.");

            // Kiểm tra số lượng bằng chứng và tính hợp lệ của ID trước khi gọi Media xác minh quyền sử dụng.
            if (unit.MediaIds.Count > 10 || unit.MediaIds.Contains(Guid.Empty) ||
                unit.MediaIds.Distinct().Count() != unit.MediaIds.Count)
                return Error.Validation("Return.InvalidEvidence", "Invalid condition, description or media IDs.");

            // Tách điều kiện máy hỏng khỏi bình thường để áp đúng yêu cầu bằng chứng và cách xử lý trả/khấu trừ.
            if (unit.Condition == UnitCondition.Damaged)
            {
                // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
                if (string.IsNullOrWhiteSpace(unit.Description))
                    return Error.Validation(actorRole == ActorRole.Admin ? "Return.DamagedDescriptionRequired" : "Return.InvalidEvidence",
                        actorRole == ActorRole.Admin ? "Description is required when unit condition is Damaged." : "Damaged unit requires a description.");

                // Kiểm tra số lượng bằng chứng và tính hợp lệ của ID trước khi gọi Media xác minh quyền sử dụng.
                if (unit.MediaIds.Count == 0)
                    return Error.Validation(actorRole == ActorRole.Admin ? "Return.DamagedMediaRequired" : "Return.InvalidEvidence",
                        actorRole == ActorRole.Admin ? "Media evidence is required when unit condition is Damaged." : "Damaged unit requires at least one media evidence.");
            }
        }

        // Tách kiểm tra dữ liệu khỏi xác minh Media để từ chối request sai trước khi gọi mạng.
        // Media phải hợp lệ trước khi thay đổi graph order và trước transaction persistence.
        foreach (var unit in units)
        {
            // Kiểm tra số lượng bằng chứng và tính hợp lệ của ID trước khi gọi Media xác minh quyền sử dụng.
            if (unit.MediaIds.Count == 0 && unit.Condition != UnitCondition.Damaged) continue;
            var evidence = await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(
                unit.MediaIds, actorId, mediaClient, isRequired: unit.Condition == UnitCondition.Damaged, ct: cancellationToken);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (evidence.IsFailure) return evidence.Error;
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        now = timeProvider.GetUtcNow();
        var previousStatus = item.Status;

        // Gắn chứng từ/bằng chứng vào đúng yêu cầu trả và tập unit đã chọn, giữ lịch sử cần cho đối soát sau đó.
        var record = new ReturnRecord(
            Guid.NewGuid(),
            item.Id,
            actualReturnAt.ToUniversalTime(),
            note,
            actorId,
            now.ToUniversalTime());
        item.AddReturnRecord(record);

        // Xử lý từng unit/assignment trong tập đã chọn; giữ định danh từng máy khi kiểm tra và ghi bằng chứng.
        foreach (var unit in units)
        {
            // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
            var assignment = item.UnitAssignments.First(a => a.Id == unit.RentalUnitAssignmentId);
            assignment.RecordReturn(record.Id, unit.Condition, unit.Description, unit.MediaIds, null, null);
            record.AddAssignment(assignment);
        }

        // Biên bản có thể chỉ trả một phần số máy. Chỉ đóng các request-unit liên quan,
        // giữ nguyên yêu cầu của những máy chưa được nhận lại.
        var returnedAssignmentIds = units.Select(u => u.RentalUnitAssignmentId).ToHashSet();
        // Xử lý từng unit/assignment trong tập đã chọn; giữ định danh từng máy khi kiểm tra và ghi bằng chứng.
        foreach (var reqUnit in item.ReturnRequests.SelectMany(r => r.Units))
        {
            // Kiểm tra thành viên trong tập liên quan để không áp dụng yêu cầu lên dữ liệu ngoài phạm vi đang xử lý.
            if (returnedAssignmentIds.Contains(reqUnit.RentalUnitAssignmentId) && reqUnit.IsActionable)
            {
                // Ghi nhận phần xử lý trả máy trong aggregate; xác nhận đóng allocation tại Inventory vẫn là bước riêng.
                reqUnit.SetClosedAt(now);
            }
        }

        // Chỉ mở bước chốt khấu trừ sau khi tất cả unit đã trả. Máy hỏng và có cọc
        // cho supplier 48 giờ đề xuất; nhánh còn lại chốt khấu trừ 0. Đây chưa là ACK Inventory.
        if (item.AreAllUnitsReturned)
        {
            // Chuẩn hóa mốc nhận đủ unit về UTC để tính cửa sổ đề xuất khấu trừ và lưu lịch sử nhất quán.
            var completedAt = now.ToUniversalTime();
            // Đối chiếu số tiền và tiền tệ theo dữ liệu của luồng; không chấp nhận chênh lệch khi ghi nhận kết quả tài chính.
            if (item.HasDamagedUnits && item.DepositAmount > 0m)
            {
                // Ghi nhận phần xử lý trả máy trong aggregate; xác nhận đóng allocation tại Inventory vẫn là bước riêng.
                item.SetReturnsCompleted(completedAt, completedAt.AddHours(48), null);
            }
            else
            {
                // Ghi nhận phần xử lý trả máy trong aggregate; xác nhận đóng allocation tại Inventory vẫn là bước riêng.
                item.SetReturnsCompleted(completedAt, null, 0m);
            }
        }

        // Chạy phần bổ sung của nhánh gọi (ví dụ resolution admin) trong cùng aggregate trước transaction lưu biên bản trả.
        onBeforeCommit?.Invoke(record, now);

        // Tổng hợp lại trạng thái order từ các item sau quyết định; không ép các item khác đi theo một trạng thái chung.
        RentalOrderStatusPolicy.Apply(order, now, idempotencyKey);
        order.RecordReadModelUpdate(now);

        // Lưu dấu chuyển trạng thái kèm actor và operation để đối soát lịch sử của quyết định này.
        order.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(), order.Id, item.Id,
            previousStatus.ToString(), item.Status.ToString(),
            transitionAction, actorId, actorRole,
            transitionNote, now, idempotencyKey));

        // Dựng API response tại mốc xử lý hiện tại để lưu trong receipt, giữ nguyên kết quả khi client replay.
        string apiResponseJson = buildApiResponseJson(record, now);

        // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
        order.AddOperationReceipt(new RentalOperationReceipt(
            Guid.NewGuid(), order.Id, idempotencyKey, operationAction,
            fingerprint, true, null, null, now, targetItemId: item.Id,
            payloadJson: payloadJson, apiResponseJson: apiResponseJson));

        // Media đã được kiểm tra trước khi mở transaction. Từ đây lưu đồng thời biên bản,
        // receipt, command ApplyReturnedUnits và event RentalUnitsReturned; lỗi enqueue phải rollback tất cả.
        await using var tx = await unitOfWork.BeginTransactionAsync(cancellationToken);
        // Thực hiện phần ghi có xử lý lỗi: nhánh catch rollback/bỏ thay đổi theo ranh giới transaction của luồng này.
        try
        {
            // Lập tác vụ phụ thuộc cho luồng hiện tại; việc lập lịch chưa có nghĩa Inventory hoặc Payment đã thực hiện xong.
            var returnCmd = ScheduleKafkaInventoryReturn(order, item, record, now);
            var enqueueResult = await inventoryCommands.EnqueueApplyReturnedUnitsAsync(returnCmd, cancellationToken);
            // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
            if (enqueueResult.IsFailure)
            {
                // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                await tx.RollbackAsync(cancellationToken);
                unitOfWork.DiscardChanges();
                return enqueueResult.Error;
            }

            // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
            var returnedEvent = await rentalEvents.EnqueueRentalUnitsReturnedAsync(
                new RentalUnitsReturnedPayload(order.Id, item.Id, record.Id,
                    actualReturnAt.ToUniversalTime(), now,
                    units.Select(u => u.RentalUnitAssignmentId).ToArray(),
                    order.Version),
                correlationId: idempotencyKey, cancellationToken: cancellationToken);
            // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
            if (returnedEvent.IsFailure)
            {
                // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                await tx.RollbackAsync(cancellationToken);
                unitOfWork.DiscardChanges();
                return returnedEvent.Error;
            }

            // Đăng ký thay đổi aggregate với repository trước khi lưu; không truy cập DbContext trực tiếp từ service.
            await repository.UpdateAsync(order, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        // Lỗi trong phạm vi ghi: rollback transaction và bỏ graph đang tracked trước khi trả lỗi hoặc ném lại exception.
        catch
        {
            // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
            await tx.RollbackAsync(cancellationToken);
            unitOfWork.DiscardChanges();
            throw;
        }

        // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
        if (notifier != null)
        {
            // Duyệt tập dữ liệu của bước hiện tại để tích lũy kết quả, giữ nguyên phạm vi và thứ tự mà caller đã chọn.
            foreach (var changeType in notificationChangeTypes)
            {
                // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                await NotifyParticipantsOrderChangedAsync(order, item, changeType, now, cancellationToken);
            }
        }

        return (record, now);
    }
    // Dựng lệnh ApplyReturnedUnits từ đúng unit của biên bản, giữ condition từng máy và journal để ghép kết quả Inventory.
    internal static ApplyReturnedUnitsCommandPayload ScheduleKafkaInventoryReturn(
        RentalOrder order, RentalItem item, ReturnRecord record, DateTimeOffset now)
    {
        // Chỉ lấy unit gắn với ReturnRecord đang gửi và sắp theo allocation; mỗi máy mang condition NORMAL/DAMAGED riêng.
        var units = item.UnitAssignments
            .Where(a => a.ReturnRecordId == record.Id)
            .OrderBy(a => a.AllocationId)
            .Select(a => new ApplyReturnedUnitPayload(a.AllocationId,
                a.ReturnCondition == UnitCondition.Damaged ? "DAMAGED" : "NORMAL"))
            .ToArray();
        // Key theo ReturnRecord để retry cùng biên bản dùng lại một lệnh Inventory.
        // Truyền điều kiện từng allocation: máy hỏng phải được Inventory bảo vệ khi đóng thuê.
        string operationId = $"return:{record.Id:N}:apply";
        string operationKey = RentalOperationExecutor.ComputeOperationKey(
            RentalItem.ApplyReturnedUnitsAction, operationId);
        var command = new ApplyReturnedUnitsCommandPayload(order.Id, item.Id,
            item.AllocationGroupId, record.Id, record.ActualReturnAt, operationKey, units);
        var json = JsonSerializer.Serialize(command);
        order.AddOperationReceipt(RentalOperationReceipt.CreatePending(Guid.NewGuid(),
            order.Id, operationId, RentalItem.ApplyReturnedUnitsAction,
            RentalOperationExecutor.ComputeFingerprint(json), now, item.Id, json));
        return command;
    }


}
