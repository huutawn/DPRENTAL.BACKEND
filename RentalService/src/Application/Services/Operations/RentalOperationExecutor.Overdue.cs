using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Operations;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Services.Operations;

/// <remarks>
/// Ghi lệnh MarkAssetsOverdue qua Outbox với operation key đã lưu.
/// Thiết bị chưa trả phải tiếp tục bị giữ lịch; gửi lệnh không có nghĩa đã nhận xác nhận từ Inventory.
/// </remarks>
public sealed partial class RentalOperationExecutor
{
    // Đọc payload bảo vệ máy quá hạn và gọi Inventory bằng key đã lưu; ghi thành công hoặc lỗi để retry không mở lại lịch khi máy chưa trả.
    internal async Task<Result> MarkAssetsOverdueCoreAsync(RentalOrder order, RentalItem item,
        RentalOperationReceipt receipt, CancellationToken cancellationToken)
    {
        // Khai báo request cần khôi phục từ journal trước khi thực thi; thiếu/hỏng payload phải dừng để đối soát.
        OverdueProtectionCommand? command;
        // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
        try { command = JsonSerializer.Deserialize<OverdueProtectionCommand>(receipt.PayloadJson ?? "", JsonOptions); }
        // Dữ liệu JSON không đọc được: trả lỗi hoặc đi nhánh fallback đã định nghĩa, không dùng một snapshot dựng từ dữ liệu hỏng.
        catch (JsonException) { return Error.Unprocessable("CORRUPTED_OVERDUE_COMMAND", "Payload bảo vệ quá hạn không hợp lệ."); }

        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (command is null || command.AssignmentId == Guid.Empty || command.ProviderRequest is null ||
            command.ProviderRequest.AllocationIds is not { Count: 1 } || string.IsNullOrWhiteSpace(command.ProviderRequest.OperationKey) ||
            command.ProviderRequest.OperationKey != ComputeOperationKey(RentalItem.OverdueProtectionAction, command.AssignmentId.ToString("D")))
            return Error.Unprocessable("CORRUPTED_OVERDUE_COMMAND", "Payload bảo vệ quá hạn không hợp lệ.");

        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
        RentalUnitAssignment? unit = item.UnitAssignments.FirstOrDefault(u => u.Id == command.AssignmentId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (unit is null || unit.AllocationId != command.ProviderRequest.AllocationIds[0])
            return Error.Unprocessable("INVALID_OVERDUE_TARGET", "Allocation không thuộc mục thuê.");

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset now = _timeProvider.GetUtcNow();
        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (!item.IsOverdue(now) || unit.Status != RentalUnitAssignmentStatus.HandedOver || unit.ReturnRecordId.HasValue)
        {
            // Ghi kết quả thành công của receipt; ý nghĩa hoàn tất nghiệp vụ còn phụ thuộc action và event xác nhận riêng.
            receipt.MarkSucceeded(now);
            await CommitLocalChangesAsync(order, cancellationToken);
            return Result.Success();
        }

        // Mở transaction cho phần ghi local; các thay đổi và Outbox trong phạm vi này phải được commit/rollback cùng nhau.
        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        var enqueue = await _inventoryCommands.EnqueueMarkAssetsOverdueAsync(
            new MarkAssetsOverdueCommandPayload(order.Id, item.Id, item.AllocationGroupId,
                command.ProviderRequest.OperationKey, command.ProviderRequest.AllocationIds),
            order.Id.ToString("D"), cancellationToken: cancellationToken);
        // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
        if (enqueue.IsFailure)
        {
            // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();
            return enqueue.Error;
        }
        receipt.MarkProcessing(now);
        await CommitLocalChangesAsync(order, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return Result.Success();
    }
}
