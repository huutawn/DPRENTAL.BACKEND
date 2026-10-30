#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.IServices.SupplierDecision;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Services.SupplierDecision;

using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

/// <remarks>
/// Điểm vào cho quyết định supplier: xác thực caller, quyền sở hữu và dữ liệu trước khi gọi executor nhận/từ chối suborder.
/// Luồng bàn giao khóa order, kiểm tra deadline và bằng chứng Media, lưu snapshot/receipt và lệnh Kafka.
/// HandoverRecord và trạng thái InRental chỉ được chốt khi handler nhận Inventory xác nhận thành công.
/// </remarks>
public sealed class SupplierDecisionService : ISupplierDecisionService
{
    private readonly IRentalOrderRepository _orderRepository;
    private readonly IRentalOperationExecutor _operationExecutor;
    private readonly IOrderLockManager _lockManager;
    private readonly IInventoryCommandProducer _inventoryCommands;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRentalMediaClient? _mediaClient;
    private readonly TimeProvider _timeProvider;
    private readonly int _autoExpireGraceHours;
    private readonly ILogger<SupplierDecisionService> _logger;

    // Khởi tạo các dependency và cấu hình dùng cho service; kiểm tra dependency bắt buộc ngay khi dựng đối tượng.
    public SupplierDecisionService(
        IRentalOrderRepository orderRepository,
        IRentalOperationExecutor operationExecutor,
        IOrderLockManager lockManager,
        IInventoryCommandProducer inventoryCommands,
        IUnitOfWork unitOfWork,
        TimeProvider? timeProvider = null,
        ILogger<SupplierDecisionService>? logger = null,
        IRentalMediaClient? mediaClient = null,
        IOptions<HandoverOverdueOptions>? handoverOptions = null)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _operationExecutor = operationExecutor ?? throw new ArgumentNullException(nameof(operationExecutor));
        _lockManager = lockManager ?? new NoOpOrderLockManager();
        _inventoryCommands = inventoryCommands!;
        _unitOfWork = unitOfWork!;
        _mediaClient = mediaClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _autoExpireGraceHours = (handoverOptions?.Value ?? new HandoverOverdueOptions()).AutoExpireGraceHours;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SupplierDecisionService>.Instance;
    }

    // Khởi tạo các dependency và cấu hình dùng cho service; kiểm tra dependency bắt buộc ngay khi dựng đối tượng.
    public SupplierDecisionService(
        IRentalOrderRepository orderRepository,
        IRentalOperationExecutor operationExecutor,
        ILogger<SupplierDecisionService>? logger = null)
        : this(orderRepository, operationExecutor, new NoOpOrderLockManager(), null!, null!, null, logger)
    {
    }

    // Xác thực supplier và sở hữu suborder trước khi giao executor xử lý quyết định dưới khóa order.
    public async Task<Result<SubOrderDecisionResponse>> AcceptSubOrderAsync(
        Guid subOrderId,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
        var keyValidation = ValidateIdempotencyKey(idempotencyKey);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (keyValidation.IsFailure) return keyValidation.Error;

        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        Result<Guid> supplier = RentalAuthorizationPolicy.AuthorizeSupplierCaller(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (supplier.IsFailure) return supplier.Error;

        // Giữ định danh/vai trò đã xác định cho thao tác để audit và các bước tiếp theo dùng cùng chủ thể.
        Guid supplierId = supplier.Value;

        // Tra cứu suborder không tracking để kiểm tra sở hữu; executor sẽ xử lý dưới khóa order.
        Result<RentalSubOrder> owned = RentalAuthorizationPolicy.AuthorizeSupplierSubOrder(
            supplierId, await _orderRepository.GetSubOrderByIdAsync(subOrderId, cancellationToken));
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (owned.IsFailure) return owned.Error;
        RentalSubOrder subOrder = owned.Value;

        // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
        var result = await _operationExecutor.ExecuteSubOrderAcceptAsync(
            subOrder.RentalOrderId,
            subOrder.Id,
            supplierId,
            idempotencyKey.Trim(),
            cancellationToken);

        return result.IsSuccess ? ToResponse(subOrder, result.Value) : result.Error;
    }

    // Validate lý do/ghi chú và quyền supplier, rồi chuyển toàn bộ quyết định từ chối suborder sang executor chung.
    public async Task<Result<SubOrderDecisionResponse>> RejectSubOrderAsync(
        Guid subOrderId,
        SupplierRejectRequest request,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
        var keyValidation = ValidateIdempotencyKey(idempotencyKey);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (keyValidation.IsFailure) return keyValidation.Error;

        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        Result<Guid> supplier = RentalAuthorizationPolicy.AuthorizeSupplierCaller(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (supplier.IsFailure) return supplier.Error;

        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (request == null || string.IsNullOrWhiteSpace(request.RejectReason) || request.RejectReason.Trim().Length > 500)
        {
            return Error.Validation("INVALID_REJECT_REASON", "Lý do từ chối là bắt buộc và tối đa 500 ký tự.");
        }

        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (request.OptionalNote != null && request.OptionalNote.Trim().Length > 2000)
        {
            return Error.Validation("INVALID_OPTIONAL_NOTE", "Ghi chú không được vượt quá 2000 ký tự.");
        }

        // Giữ định danh/vai trò đã xác định cho thao tác để audit và các bước tiếp theo dùng cùng chủ thể.
        Guid supplierId = supplier.Value;

        // Tra cứu suborder không tracking để kiểm tra sở hữu; executor sẽ xử lý dưới khóa order.
        Result<RentalSubOrder> owned = RentalAuthorizationPolicy.AuthorizeSupplierSubOrder(
            supplierId, await _orderRepository.GetSubOrderByIdAsync(subOrderId, cancellationToken));
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (owned.IsFailure) return owned.Error;
        RentalSubOrder subOrder = owned.Value;

        // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
        var result = await _operationExecutor.ExecuteSubOrderRejectAsync(
            subOrder.RentalOrderId,
            subOrder.Id,
            supplierId,
            request.RejectReason.Trim(),
            string.IsNullOrWhiteSpace(request.OptionalNote) ? null : request.OptionalNote.Trim(),
            idempotencyKey.Trim(),
            cancellationToken);

        return result.IsSuccess ? ToResponse(subOrder, result.Value) : result.Error;
    }

    // Tóm tắt trạng thái các item thành response suborder; không tự áp một trạng thái giả định cho toàn bộ dòng.
    private static SubOrderDecisionResponse ToResponse(RentalSubOrder subOrder, IReadOnlyList<SupplierDecisionResponse> items)
    {
        // Tổng hợp trạng thái các response item bằng policy đơn con; không mặc định cả suborder đã hoàn tất chỉ vì một dòng thành công.
        RentalItemStatus status = SubOrderStatusPolicy.Summarize(
            items.Select(i => Enum.Parse<RentalItemStatus>(i.Status)).ToList());
        return new SubOrderDecisionResponse(subOrder.Id, subOrder.SubOrderCode, subOrder.RentalOrderId, status.ToString(), items);
    }

    // Ghi intent bàn giao cùng snapshot và Outbox sau kiểm tra replay, deadline, đủ unit và Media; chờ Inventory mới chốt biên bản.
    public async Task<Result<HandoverItemResponse>> HandoverItemAsync(
        Guid itemId,
        HandoverItemRequest request,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
        var keyValidation = ValidateIdempotencyKey(idempotencyKey);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (keyValidation.IsFailure) return keyValidation.Error;

        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        Result<Guid> supplier = RentalAuthorizationPolicy.AuthorizeSupplierCaller(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (supplier.IsFailure) return supplier.Error;

        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (request == null || request.Units == null || request.Units.Count == 0)
        {
            return Error.Validation("INVALID_REQUEST", "Thông tin bàn giao không hợp lệ.");
        }

        // Dừng nhánh không hợp lệ với mã INVALID_HANDOVER_TIME; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (request.ActualHandoverAt == default)
        {
            return Error.Validation("INVALID_HANDOVER_TIME", "Thời gian bàn giao thực tế không hợp lệ.");
        }

        // Giữ định danh/vai trò đã xác định cho thao tác để audit và các bước tiếp theo dùng cùng chủ thể.
        Guid supplierId = supplier.Value;
        var orderId = await _orderRepository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (!orderId.HasValue) return RentalErrors.ItemNotFound;

        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order == null) return RentalErrors.ItemNotFound;

        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        Result<RentalItem> owned = RentalAuthorizationPolicy.AuthorizeSupplierItem(
            supplierId, order.Items.FirstOrDefault(i => i.Id == itemId));
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (owned.IsFailure) return owned.Error;
        RentalItem item = owned.Value;

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string opKey = idempotencyKey.Trim();
        string fingerprint = RentalOperationExecutor.ComputeFingerprint(new
        {
            Action = "MarkAssetsHandedOver",
            ItemId = item.Id,
            ActualHandoverAt = request.ActualHandoverAt,
            request.Note,
            request.Confirmed,
            Units = request.Units.OrderBy(u => u.AssignmentId).Select(u => new
            {
                u.AssignmentId,
                u.Condition,
                u.Note,
                MediaIds = (u.MediaIds ?? []).OrderBy(m => m).ToList()
            }).ToList()
        });

        // Replay kiểm tra trước trạng thái/deadline: lần đầu có thể đã bàn giao xong.
        // Gửi lại cùng nội dung cần nhận trạng thái receipt cũ, không gửi lệnh bàn giao thứ hai.
        var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == opKey);
        // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
        if (existingReceipt != null)
        {
            // Đối chiếu action, phạm vi và fingerprint; cùng key nhưng khác yêu cầu phải báo conflict thay vì dùng kết quả cũ.
            if (existingReceipt.Action != "MarkAssetsHandedOver" ||
                existingReceipt.TargetItemId != item.Id ||
                !string.Equals(existingReceipt.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa idempotency đã được dùng với payload khác.");
            }

            return new HandoverItemResponse(item.HandoverRecord?.Id, request.ActualHandoverAt, existingReceipt.Status);
        }

        // Chặn cả receipt cần đối soát: chưa có phản hồi không có nghĩa máy chưa giao.
        // Một yêu cầu mới lúc này có thể tạo hai bộ bằng chứng cho cùng lần bàn giao.
        if (order.OperationReceipts.Any(r =>
                r.Action == "MarkAssetsHandedOver" &&
                r.TargetItemId == item.Id &&
                (r.Status == RentalOperationReceipt.StatusPending ||
                 r.Status == RentalOperationReceipt.StatusProcessing ||
                 r.Status == RentalOperationReceipt.StatusManualReconciliationRequired)))
        {
            return Error.Conflict("HANDOVER_IN_PROGRESS", "Yêu cầu bàn giao trước đó đang chờ Inventory xử lý hoặc đối soát.");
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (item.Status != RentalItemStatus.AwaitingHandover)
        {
            return Error.Conflict("INVALID_STATUS", $"Mục thuê không ở trạng thái AwaitingHandover. Trạng thái hiện tại: {item.Status}.");
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
        if (HandoverDeadlinePolicy.IsAutoExpireDue(item, utcNow, _autoExpireGraceHours))
        {
            return Error.Conflict("HANDOVER_DEADLINE_PASSED", "Đã hết hạn bàn giao; cần đối soát nếu thiết bị đã được giao thực tế.");
        }

        // Dừng nhánh không hợp lệ với mã INVALID_HANDOVER_TIME; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (request.ActualHandoverAt > utcNow)
        {
            return Error.Validation("INVALID_HANDOVER_TIME", "Thời gian bàn giao thực tế không thể ở tương lai.");
        }
        // Dừng nhánh không hợp lệ với mã HANDOVER_NOT_CONFIRMED; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (!request.Confirmed)
        {
            return Error.Validation("HANDOVER_NOT_CONFIRMED", "Supplier phải xác nhận đã bàn giao thiết bị cho người thuê.");
        }

        // Kiểm tra số lượng bằng chứng và tính hợp lệ của ID trước khi gọi Media xác minh quyền sử dụng.
        if (request.Units.Any(u => u.MediaIds == null || u.MediaIds.Count == 0))
        {
            return Error.Validation("MISSING_MEDIA", "Mỗi thiết bị bàn giao phải kèm bằng chứng hình ảnh.");
        }

        // Kiểm tra tính duy nhất của danh sách ID để một unit hoặc bản ghi không được tính/xử lý nhiều lần trong cùng yêu cầu.
        if (request.Units.Select(u => u.AssignmentId).Distinct().Count() != request.Units.Count)
        {
            return Error.Validation("INVALID_UNITS", "Danh sách thiết bị bàn giao có chứa AssignmentId trùng lặp.");
        }


        // Kiểm tra tính duy nhất của danh sách ID để một unit hoặc bản ghi không được tính/xử lý nhiều lần trong cùng yêu cầu.
        if (item.UnitAssignments.Count != item.Quantity ||
            item.UnitAssignments.Any(a => a.AssetId == Guid.Empty || a.AllocationId == Guid.Empty) ||
            item.UnitAssignments.Select(a => a.AssetId).Distinct().Count() != item.Quantity ||
            item.UnitAssignments.Select(a => a.AllocationId).Distinct().Count() != item.Quantity)
        {
            return Error.Validation("INVALID_ASSIGNMENTS", "Thông tin phân bổ thiết bị của mục thuê không hợp lệ hoặc chưa đủ số lượng.");
        }

        // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
        if (request.Units.Count != item.Quantity ||
            request.Units.Any(u => item.UnitAssignments.All(a => a.Id != u.AssignmentId)))
        {
            return Error.Validation("INVALID_UNITS", "Danh sách thiết bị bàn giao phải bao gồm đầy đủ và duy nhất các máy thuộc mục thuê.");
        }
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (_mediaClient is null)
        {
            return Error.Failure("MEDIA_VERIFICATION_UNAVAILABLE", "Chưa xác minh được bằng chứng bàn giao.");
        }

        // Xác minh quyền dùng Media trước khi mở transaction DB. Vẫn giữ khóa order để
        // hủy/quá hạn không chạy xen vào giữa bước kiểm tra và ghi intent bàn giao.
        foreach (HandoverUnitObservationRequest unit in request.Units)
        {
            // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
            var evidence = await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(
                unit.MediaIds, supplierId, _mediaClient, isRequired: true, cancellationToken);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (evidence.IsFailure) return evidence.Error;
        }

        // Chưa tạo HandoverRecord: bằng chứng nằm trong receipt cho tới khi Inventory xác nhận (xem handler kết quả).
        var snapshot = new HandoverEvidenceSnapshot(
            supplierId,
            utcNow,
            request.ActualHandoverAt,
            request.Note,
            request.Units.Select(u => new HandoverUnitSnapshot(u.AssignmentId, u.Condition, u.Note, u.MediaIds)).ToList());

        // Key phía Inventory được suy ra từ intent của Rental; receipt lưu cả hai để
        // ghép đúng kết quả Kafka, kể cả khi nhiều lần retry dùng lại cùng intent.
        string providerKey = RentalOperationExecutor.ComputeOperationKey(
            "MarkAssetsHandedOver", $"{order.Id}:{item.Id}:{opKey}");
        var handoverCmd = new MarkAssetsHandedOverCommandPayload(
            order.Id,
            item.Id,
            providerKey,
            request.ActualHandoverAt);

        // Receipt chứa bằng chứng và outbox command phải commit cùng nhau: không được
        // có lệnh Inventory đã phát nhưng Rental không còn snapshot để hoàn tất biên bản.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        // Thực hiện phần ghi có xử lý lỗi: nhánh catch rollback/bỏ thay đổi theo ranh giới transaction của luồng này.
        try
        {
            // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
            var enqueueResult = await _inventoryCommands.EnqueueMarkAssetsHandedOverAsync(
                handoverCmd, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
            // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
            if (enqueueResult.IsFailure)
            {
                // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                await transaction.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return enqueueResult.Error;
            }

            // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
            var receipt = RentalOperationReceipt.CreatePending(
                Guid.NewGuid(), order.Id, opKey, "MarkAssetsHandedOver", fingerprint,
                utcNow, item.Id, snapshot.ToJson());
            receipt.SetInventoryOperationKey(providerKey);
            order.AddOperationReceipt(receipt);
            order.RecordReadModelUpdate(utcNow);
            await _orderRepository.UpdateAsync(order, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        // Lỗi trong phạm vi ghi: rollback transaction và bỏ graph đang tracked trước khi trả lỗi hoặc ném lại exception.
        catch
        {
            // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
            await transaction.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();
            throw;
        }

        return new HandoverItemResponse(null, request.ActualHandoverAt, "Processing");
    }

    // Yêu cầu key không rỗng và trong giới hạn lưu trữ trước mọi quyết định có side effect.
    private static Result ValidateIdempotencyKey(string? idempotencyKey)
    {
        // Từ chối key rỗng hoặc vượt giới hạn lưu trữ để mỗi thao tác có thể được nhận diện khi client gửi lại.
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
        {
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key là bắt buộc và không được vượt quá 64 ký tự.");
        }
        return Result.Success();
    }

    private sealed class NoOpOrderLockManager : IOrderLockManager
    {
        // Disposable rỗng của lock thay thế trong constructor giản lược; không có tài nguyên khóa thực tế để nhả.
        private sealed class NoOpDisposable : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
        private static readonly IAsyncDisposable Disposer = new NoOpDisposable();
        // Lock thay thế trả về ngay cho đường khởi tạo giản lược; không cung cấp bảo đảm đồng thời của lock production.
        public Task<IAsyncDisposable> AcquireOrderLockAsync(Guid orderId, TimeSpan? timeout = null, CancellationToken cancellationToken = default) => Task.FromResult(Disposer);
    }
}
