using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Integration;
using Dprental.RentalService.Application.DTOs.Orders;
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
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Returns;

/// <remarks>
/// Nhánh Admin đọc và giải quyết yêu cầu trả có vấn đề. Kiểm tra quyền Admin trước khi truy vấn hoặc thay đổi.
/// Khi quyết định cần ghi nhận trả thực tế, dùng chung ExecuteReturnConfirmationCoreAsync để giữ nhất quán bằng chứng và lệnh Inventory.
/// </remarks>
public sealed partial class ReturnService
{
    private const string AdminResolveReturnAction = "AdminResolveReturnRequest";

    // Kiểm tra quyền admin, chuẩn hóa bộ lọc/trang và dựng danh sách yêu cầu trả cần theo dõi hoặc giải quyết.
    public async Task<Result<PagedResponse<AdminReturnRequestSummaryResponse>>> GetAdminReturnRequestsAsync(
        AdminReturnRequestQuery query,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(caller);
        var auth = RentalAuthorizationPolicy.AuthorizeAdminReturnQuery(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (auth.IsFailure) return auth.Error;

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        var now = timeProvider.GetUtcNow();
        int page = Math.Max(1, query.Page);
        int pageSize = Math.Clamp(query.PageSize, 1, 100);

        // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
        var criteria = new AdminReturnRequestSearchCriteria(query.Disputed, query.Overdue, query.ItemId, query.OrderId);
        var paged = await repository.SearchReturnRequestsForAdminAsync(
            criteria, now, (page - 1) * pageSize, pageSize, cancellationToken);

        // Chọn, sắp xếp hoặc chiếu dữ liệu đã có thành tập phục vụ bước tiếp theo; không thay đổi nguồn dữ liệu gốc.
        var summaries = paged.Items.Select(x =>
        {
            // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
            var disputedSet = (x.DisputedAssignmentIds ?? []).ToHashSet();
            var units = x.Units ?? [];
            var unitStatuses = units.Select(u =>
            {
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (u.ClosedAt.HasValue) return ReturnRequestUnitStatus.Confirmed;
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (u.RejectedAt.HasValue) return ReturnRequestUnitStatus.Rejected;
                // Kiểm tra thành viên trong tập liên quan để không áp dụng yêu cầu lên dữ liệu ngoài phạm vi đang xử lý.
                if (disputedSet.Contains(u.RentalUnitAssignmentId)) return ReturnRequestUnitStatus.Disputed;
                return ReturnRequestUnitStatus.PendingSupplierConfirmation;
            }).ToList();

            // Tính trạng thái từ từng unit và issue hiện có qua policy chung, không dùng một cờ chung che phần đã xử lý.
            var reqStatus = ReturnRequestStatusPolicy.GetRequestStatus(unitStatuses);
            bool isOverdue = units.Any(u => !u.ClosedAt.HasValue && !u.RejectedAt.HasValue) && now >= x.ConfirmationDeadlineAt;

            return new AdminReturnRequestSummaryResponse(
                x.ReturnRequestId,
                x.RentalItemId,
                x.OrderId,
                x.OrderCode,
                x.RenterId,
                x.SupplierId,
                x.SubmittedAt,
                x.ConfirmationDeadlineAt,
                reqStatus,
                isOverdue,
                units.Count,
                units.Count(u => !u.ClosedAt.HasValue && !u.RejectedAt.HasValue),
                unitStatuses.Count(s => s == ReturnRequestUnitStatus.Disputed),
                unitStatuses.Count(s => s == ReturnRequestUnitStatus.Confirmed),
                unitStatuses.Count(s => s == ReturnRequestUnitStatus.Rejected));
        }).ToList();

        return OrderQueryMapper.ToPage(summaries, page, pageSize, paged.TotalCount);
    }

    // Đọc aggregate yêu cầu trả sau phân quyền và ghép unit, issue cùng resolution để admin có đủ dữ liệu đối soát.
    public async Task<Result<AdminReturnRequestDetailResponse>> GetAdminReturnRequestDetailAsync(
        Guid returnRequestId,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(caller);
        var auth = RentalAuthorizationPolicy.AuthorizeAdminReturnQuery(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (auth.IsFailure) return auth.Error;

        // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
        var agg = await repository.GetReturnRequestAggregateAsync(returnRequestId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (agg is null)
            return Error.NotFound("RETURN_REQUEST_NOT_FOUND", "Return request not found.");

        // Dùng bộ aggregate đã truy xuất theo ReturnRequestId để các bước tiếp theo cùng tham chiếu đúng order/item/request.
        var (order, item, request) = agg.Value;
        var now = timeProvider.GetUtcNow();

        // Dùng policy/mapper hiện có để dựng dữ liệu response nhất quán với các điểm đọc khác của cùng nghiệp vụ.
        var requestResponse = OrderQueryMapper.ToReturnRequests(item, now, includeAdminNotes: true)
            .First(r => r.ReturnRequestId == returnRequestId);

        // Ghép mapping unit của item vào hồ sơ admin để đối soát từng máy và trạng thái Inventory đã ghi nhận.
        var inventoryUnits = item.UnitAssignments
            .Where(a => request.Units.Any(u => u.RentalUnitAssignmentId == a.Id))
            .OrderBy(a => a.AssetId)
            .Select(a => new AdminReturnUnitInventoryStatusResponse(
                a.Id,
                a.AssetId,
                a.AllocationId,
                a.Status,
                a.ReturnRecordId,
                a.ReturnCondition,
                a.AllocationClosedAt,
                a.InventoryDispositionRef))
            .ToList();

        return new AdminReturnRequestDetailResponse(
            requestResponse,
            order.Id,
            order.OrderCode,
            item.SupplierId,
            order.RenterId,
            item.Status,
            inventoryUnits);
    }

    // Admin giải quyết tập unit được chọn sau replay/validation; xác nhận trả qua core chung hoặc ghi từ chối theo quyết định.
    public async Task<Result<ReturnRequestResponse>> ResolveReturnRequestAsync(
        Guid itemId,
        Guid returnRequestId,
        IRentalCallerContext caller,
        AdminResolveReturnRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(caller);
        var auth = RentalAuthorizationPolicy.AuthorizeAdminReturnResolution(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (auth.IsFailure) return auth.Error;
        Guid adminId = caller.UserId!.Value;

        // Từ chối key rỗng hoặc vượt giới hạn lưu trữ để mỗi thao tác có thể được nhận diện khi client gửi lại.
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key is required (maximum 64 characters).");

        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (request is null)
            return Error.Validation("ReturnResolution.InvalidRequest", "Request body is required.");
        // Dừng nhánh không hợp lệ với mã ReturnResolution.InvalidDecision; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (!Enum.IsDefined(request.Decision))
            return Error.Validation("ReturnResolution.InvalidDecision", "Invalid return resolution decision.");
        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (string.IsNullOrWhiteSpace(request.DecisionNote) || request.DecisionNote.Trim().Length > 2000)
            return Error.Validation("ReturnResolution.InvalidNote", "Decision note is required and cannot exceed 2000 characters.");
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (request.AssignmentIds is null || request.AssignmentIds.Count == 0)
            return Error.Validation("ReturnResolution.EmptyAssignments", "AssignmentIds must be specified.");
        // Kiểm tra thành viên trong tập liên quan để không áp dụng yêu cầu lên dữ liệu ngoài phạm vi đang xử lý.
        if (request.AssignmentIds.Any(id => id == Guid.Empty))
            return Error.Validation("ReturnResolution.InvalidAssignmentId", "AssignmentId cannot be empty GUID.");
        // Kiểm tra tính duy nhất của danh sách ID để một unit hoặc bản ghi không được tính/xử lý nhiều lần trong cùng yêu cầu.
        if (request.AssignmentIds.Distinct().Count() != request.AssignmentIds.Count)
            return Error.Validation("ReturnResolution.DuplicateAssignmentIds", "Duplicate assignment IDs are not allowed.");
        // Kiểm tra số lượng bằng chứng và tính hợp lệ của ID trước khi gọi Media xác minh quyền sử dụng.
        if (request.ResolutionMediaIds != null && request.ResolutionMediaIds.Count > 10)
            return Error.Validation("ReturnResolution.TooManyMediaIds", "Maximum 10 resolution media IDs are allowed.");
        // Kiểm tra số lượng bằng chứng và tính hợp lệ của ID trước khi gọi Media xác minh quyền sử dụng.
        if (request.ResolutionMediaIds != null && (request.ResolutionMediaIds.Contains(Guid.Empty) || request.ResolutionMediaIds.Distinct().Count() != request.ResolutionMediaIds.Count))
            return Error.Validation("ReturnResolution.InvalidMediaIds", "Resolution media IDs must be non-empty and distinct.");

        // Dừng nhánh không hợp lệ với mã ReturnResolution.InvalidActualReturnAt; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (request.Decision == ReturnResolutionDecision.ConfirmReceived)
        {
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (!request.ActualReturnAt.HasValue || request.ActualReturnAt.Value == default)
                return Error.Validation("ReturnResolution.InvalidActualReturnAt", "ActualReturnAt is required when confirming receipt.");
            // Kiểm tra số lượng bằng chứng và tính hợp lệ của ID trước khi gọi Media xác minh quyền sử dụng.
            if (request.Units is null || request.Units.Count == 0 || request.Units.Any(u => u is null || u.MediaIds is null))
                return Error.Validation("ReturnResolution.InvalidUnits", "Units inspection details and media collections are required when confirming receipt.");

            // Chọn, sắp xếp hoặc chiếu dữ liệu đã có thành tập phục vụ bước tiếp theo; không thay đổi nguồn dữ liệu gốc.
            var unitAssignIds = request.Units.Select(u => u.RentalUnitAssignmentId).ToList();
            // Kiểm tra tính duy nhất của danh sách ID để một unit hoặc bản ghi không được tính/xử lý nhiều lần trong cùng yêu cầu.
            if (unitAssignIds.Distinct().Count() != request.Units.Count || !unitAssignIds.ToHashSet().SetEquals(request.AssignmentIds))
                return Error.Validation("ReturnResolution.UnitsMismatch", "Inspection units must match targeted AssignmentIds exactly.");

            // Xử lý từng unit/assignment trong tập đã chọn; giữ định danh từng máy khi kiểm tra và ghi bằng chứng.
            foreach (var unit in request.Units)
            {
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

                // Dừng nhánh không hợp lệ với mã Return.DamagedDescriptionRequired; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
                if (unit.Condition == UnitCondition.Damaged)
                {
                    // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
                    if (string.IsNullOrWhiteSpace(unit.Description))
                        return Error.Validation("Return.DamagedDescriptionRequired", "Description is required when unit condition is Damaged.");
                    // Kiểm tra số lượng bằng chứng và tính hợp lệ của ID trước khi gọi Media xác minh quyền sử dụng.
                    if (unit.MediaIds.Count == 0)
                        return Error.Validation("Return.DamagedMediaRequired", "Media evidence is required when unit condition is Damaged.");
                }
            }
        }

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string key = idempotencyKey.Trim();
        var sortedAssignIds = string.Join(",", request.AssignmentIds.OrderBy(id => id));
        var sortedResMedia = request.ResolutionMediaIds != null ? string.Join(",", request.ResolutionMediaIds.OrderBy(id => id)) : "";
        string payload = JsonSerializer.Serialize(new
        {
            ItemId = itemId,
            ReturnRequestId = returnRequestId,
            AdminId = adminId,
            Decision = request.Decision.ToString(),
            DecisionNote = request.DecisionNote.Trim(),
            AssignmentIds = sortedAssignIds,
            ResolutionMediaIds = sortedResMedia,
            ActualReturnAt = request.ActualReturnAt?.ToUniversalTime(),
            Units = request.Units?.OrderBy(u => u.RentalUnitAssignmentId).Select(u => new
            {
                u.RentalUnitAssignmentId,
                Condition = u.Condition.ToString(),
                u.Description,
                MediaIds = (u.MediaIds ?? []).OrderBy(id => id)
            })
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
            if (receipt.Action != AdminResolveReturnAction || receipt.TargetItemId != itemId || receipt.Fingerprint != fingerprint)
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "This key was used for another request.");
            var saved = string.IsNullOrWhiteSpace(receipt.ApiResponseJson) ? null :
                JsonSerializer.Deserialize<ReturnRequestResponse>(receipt.ApiResponseJson, ReceiptJsonOptions);
            return saved is null ? Error.Failure("INVALID_RETURN_RECEIPT", "Stored response is unavailable.") : saved;
        }

        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
        var returnReq = item.ReturnRequests.FirstOrDefault(r => r.Id == returnRequestId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (returnReq is null)
            return Error.NotFound("RETURN_REQUEST_NOT_FOUND", "Return request not found on this item.");

        // Chọn, sắp xếp hoặc chiếu dữ liệu đã có thành tập phục vụ bước tiếp theo; không thay đổi nguồn dữ liệu gốc.
        var reqUnitsByAssign = returnReq.Units.ToDictionary(u => u.RentalUnitAssignmentId);
        // Duyệt tập dữ liệu của bước hiện tại để tích lũy kết quả, giữ nguyên phạm vi và thứ tự mà caller đã chọn.
        foreach (var assignId in request.AssignmentIds)
        {
            // Dừng nhánh không hợp lệ với mã ReturnResolution.AssignmentNotInRequest; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
            if (!reqUnitsByAssign.TryGetValue(assignId, out var u))
                return Error.Validation("ReturnResolution.AssignmentNotInRequest", $"Assignment {assignId} does not belong to this return request.");
            // Dừng nhánh không hợp lệ với mã ReturnResolution.UnitNotActionable; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
            if (!u.IsActionable)
                return Error.Conflict("ReturnResolution.UnitNotActionable", $"Assignment {assignId} is already closed or rejected.");
        }

        // Kiểm tra số lượng bằng chứng và tính hợp lệ của ID trước khi gọi Media xác minh quyền sử dụng.
        if (request.ResolutionMediaIds != null && request.ResolutionMediaIds.Count > 0)
        {
            // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
            var evidence = await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(
                request.ResolutionMediaIds, adminId, mediaClient, ct: cancellationToken);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (evidence.IsFailure) return evidence.Error;
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        var now = timeProvider.GetUtcNow();

        // Chọn nhánh theo quyết định gửi lên; mỗi nhánh có điều kiện bằng chứng và thay đổi trạng thái riêng.
        if (request.Decision == ReturnResolutionDecision.RejectRequest)
        {
            // Duyệt tập dữ liệu của bước hiện tại để tích lũy kết quả, giữ nguyên phạm vi và thứ tự mà caller đã chọn.
            foreach (var assignId in request.AssignmentIds)
            {
                // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
                reqUnitsByAssign[assignId].SetRejectedAt(now);
            }

            // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
            var resolution = new ReturnRequestResolution(
                Guid.NewGuid(),
                returnRequestId,
                adminId,
                ReturnResolutionDecision.RejectRequest,
                request.DecisionNote.Trim(),
                request.ResolutionMediaIds ?? [],
                now,
                request.AssignmentIds);

            // Gắn chứng từ/bằng chứng vào đúng yêu cầu trả và tập unit đã chọn, giữ lịch sử cần cho đối soát sau đó.
            returnReq.AddResolution(resolution);
            order.RecordReadModelUpdate(now);

            // Dùng policy/mapper hiện có để dựng dữ liệu response nhất quán với các điểm đọc khác của cùng nghiệp vụ.
            var response = OrderQueryMapper.ToReturnRequests(item, now, includeAdminNotes: true)
                .First(r => r.ReturnRequestId == returnRequestId);

            // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
            order.AddOperationReceipt(new RentalOperationReceipt(
                Guid.NewGuid(),
                order.Id,
                key,
                AdminResolveReturnAction,
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
                // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                await NotifyParticipantsOrderChangedAsync(order, item, OrderChangeTypes.ReturnResolved, now, cancellationToken);
            }

            return response;
        }

        // Decision == ConfirmReceived -> delegate to unified return workflow
        var coreResult = await ExecuteReturnConfirmationCoreAsync(
            order,
            item,
            request.ActualReturnAt!.Value,
            request.DecisionNote,
            request.Units!,
            adminId,
            ActorRole.Admin,
            key,
            fingerprint,
            payload,
            AdminResolveReturnAction,
            "AdminConfirmReceived",
            "Admin confirmed returned units",
            (rec, processedAt) =>
            {
                // Dựng request/command từ snapshot và định danh của luồng hiện tại để gửi đúng tác vụ sang service sở hữu.
                var confirmResolution = new ReturnRequestResolution(
                    Guid.NewGuid(),
                    returnRequestId,
                    adminId,
                    ReturnResolutionDecision.ConfirmReceived,
                    request.DecisionNote.Trim(),
                    request.ResolutionMediaIds ?? [],
                    processedAt,
                    request.AssignmentIds);
                returnReq.AddResolution(confirmResolution);
            },
            (rec, processedAt) => JsonSerializer.Serialize(
                OrderQueryMapper.ToReturnRequests(item, processedAt, includeAdminNotes: true)
                    .First(r => r.ReturnRequestId == returnRequestId)),
            [OrderChangeTypes.ReturnSubmitted, OrderChangeTypes.ReturnResolved],
            cancellationToken);

        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (coreResult.IsFailure) return coreResult.Error;
        var (_, finishedAt) = coreResult.Value;
        return OrderQueryMapper.ToReturnRequests(item, finishedAt, includeAdminNotes: true)
            .First(r => r.ReturnRequestId == returnRequestId);
    }

    // Thông báo thay đổi cho renter và supplier của item sau lưu dữ liệu; ghi nhận lỗi thông báo theo xử lý hiện có.
    private async Task NotifyParticipantsOrderChangedAsync(
        RentalOrder order, RentalItem item, string changeType, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
        if (notifier is null) return;
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
                    changeType,
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
                        changeType,
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
                LogAdminReturnNotificationFailed(logger, ex, order.Id);
            }
        }
    }

    // Ghi cảnh báo khi gửi thông báo kết quả xử lý trả thất bại mà không log payload bằng chứng.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send return notification for order {OrderId}")]
    private static partial void LogAdminReturnNotificationFailed(ILogger logger, Exception ex, Guid orderId);
}
