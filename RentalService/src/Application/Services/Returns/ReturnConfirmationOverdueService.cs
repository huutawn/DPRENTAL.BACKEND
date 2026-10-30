#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.IServices.Returns;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Returns;

/// <remarks>
/// Worker tìm yêu cầu trả đến hạn, sau đó khóa và đọc lại order để áp dụng quy tắc quá hạn xác nhận.
/// Thông báo/quyết định trên yêu cầu trả không thay cho xác nhận đóng allocation từ Inventory.
/// </remarks>
public sealed class ReturnConfirmationOverdueService(
    IRentalOrderRepository orders,
    IOrderLockManager locks,
    IUnitOfWork unitOfWork,
    IRentalEventProducer rentalEvents,
    TimeProvider timeProvider,
    IOrderHubNotifier? notifier = null,
    ILogger<ReturnConfirmationOverdueService>? logger = null) : IReturnConfirmationOverdueService
{
    // Lấy batch order có yêu cầu trả đã đến hạn và còn unit chưa xử lý/chưa được cảnh báo.
    public Task<IReadOnlyList<Guid>> GetDueOrderIdsAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset now = timeProvider.GetUtcNow();
        return orders.GetReturnConfirmationOverdueOrderIdsAsync(now, batchSize, cancellationToken);
    }

    // Đọc lại request dưới khóa order; ghi mốc cảnh báo từng unit và event trong transaction để worker không phát cảnh báo trùng.
    public async Task<Result<int>> ProcessOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(15), cancellationToken);
        var order = await orders.GetByIdWithDetailsAsync(orderId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null) return 0;

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset now = timeProvider.GetUtcNow();
        var dueRequests = new List<(ReturnRequest Request, RentalItem Item, List<ReturnRequestUnit> OverdueUnits)>();
        int totalActionableUnits = 0;

        // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
        foreach (var item in order.Items)
        {
            // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
            foreach (var request in item.ReturnRequests)
            {
                // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
                if (request.ConfirmationDeadlineAt > now) continue;

                // Chỉ chọn unit chưa đóng/chưa từ chối/chưa cảnh báo trong request đến hạn; không cảnh báo lại phần đã được xử lý.
                var overdueUnits = request.Units
                    .Where(u => u.IsActionable && !u.OverdueNotifiedAt.HasValue)
                    .ToList();

                // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
                if (overdueUnits.Count > 0)
                {
                    // Gom đúng request, item và các unit đến hạn chưa cảnh báo để bước ghi chỉ tác động vào phần thực sự còn chờ.
                    dueRequests.Add((request, item, overdueUnits));
                    totalActionableUnits += overdueUnits.Count;
                }
            }
        }

        // Không còn unit đến hạn cần xử lý thì dừng sớm; không tạo event/version mới chỉ vì worker chạy lại.
        if (totalActionableUnits == 0)
        {
            return 0;
        }

        // Mở transaction cho phần ghi local; các thay đổi và Outbox trong phạm vi này phải được commit/rollback cùng nhau.
        await using var tx = await unitOfWork.BeginTransactionAsync(cancellationToken);
        // Thực hiện phần ghi có xử lý lỗi: nhánh catch rollback/bỏ thay đổi theo ranh giới transaction của luồng này.
        try
        {
            // Xử lý từng unit/assignment trong tập đã chọn; giữ định danh từng máy khi kiểm tra và ghi bằng chứng.
            foreach (var (_, _, overdueUnits) in dueRequests)
            {
                // Xử lý từng unit/assignment trong tập đã chọn; giữ định danh từng máy khi kiểm tra và ghi bằng chứng.
                foreach (var unit in overdueUnits)
                {
                    // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
                    unit.SetOverdueNotifiedAt(now);
                }
            }

            // Cập nhật version/mốc sửa dữ liệu đọc để client và event nhận biết một thay đổi mới của order.
            order.RecordReadModelUpdate(now);

            // Xử lý từng unit/assignment trong tập đã chọn; giữ định danh từng máy khi kiểm tra và ghi bằng chứng.
            foreach (var (request, item, overdueUnits) in dueRequests)
            {
                // Gắn correlation cho nhóm thông báo để theo dõi bản tin; việc chống lặp dựa trên dấu đã cảnh báo/receipt đã lưu.
                string correlationId = $"return-overdue:{request.Id}:{now.ToUnixTimeSeconds()}";
                var enqueueResult = await rentalEvents.EnqueueReturnConfirmationOverdueAsync(
                    new ReturnConfirmationOverduePayload(
                        order.Id,
                        item.Id,
                        request.Id,
                        item.SupplierId,
                        order.RenterId,
                        request.ConfirmationDeadlineAt,
                        now,
                        overdueUnits.Select(u => u.RentalUnitAssignmentId).ToArray(),
                        order.Version),
                    correlationId: correlationId,
                    cancellationToken: cancellationToken);

                // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
                if (enqueueResult.IsFailure)
                {
                    // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                    await tx.RollbackAsync(cancellationToken);
                    unitOfWork.DiscardChanges();
                    logger?.LogWarning("Failed to enqueue ReturnConfirmationOverdue event for request {RequestId}: {Error}",
                        request.Id, enqueueResult.Error.Message);
                    return enqueueResult.Error;
                }
            }

            // Đăng ký thay đổi aggregate với repository trước khi lưu; không truy cập DbContext trực tiếp từ service.
            await orders.UpdateAsync(order, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        // Lỗi trong phạm vi ghi: rollback transaction và bỏ graph đang tracked trước khi trả lỗi hoặc ném lại exception.
        catch (OperationCanceledException)
        {
            // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
            await tx.RollbackAsync(cancellationToken);
            unitOfWork.DiscardChanges();
            throw;
        }
        // Lỗi trong phạm vi ghi: rollback transaction và bỏ graph đang tracked trước khi trả lỗi hoặc ném lại exception.
        catch (Exception ex)
        {
            // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
            await tx.RollbackAsync(cancellationToken);
            unitOfWork.DiscardChanges();
            logger?.LogWarning(ex, "Failed to process overdue return confirmation for order {OrderId}", order.Id);
            return Error.Failure("RETURN_CONFIRMATION_OVERDUE_FAILED", ex.Message);
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
                        OrderChangeTypes.ReturnConfirmationOverdue,
                        now),
                    cancellationToken);
            }
            // Phân biệt timeout của lời gọi con theo catch filter; cancellation của caller không được biến thành kết quả nghiệp vụ thành công.
            catch (OperationCanceledException)
            {
                throw;
            }
            // Xử lý exception theo chính sách của nhánh này: chuyển thành lỗi/ghi nhận retry hoặc ném lại; không tự suy ra provider chưa thực thi.
            catch (Exception ex)
            {
                // Ghi log/audit của bước xử lý bằng metadata; không coi log là nguồn trạng thái nghiệp vụ.
                logger?.LogWarning(ex, "Failed to send ReturnConfirmationOverdue notification to renter {RenterId} for order {OrderId}",
                    order.RenterId, order.Id);
            }

            // Gộp supplier/suborder trước notification để nhiều request đến hạn trong cùng đơn con không tạo nhiều tín hiệu realtime trùng.
            var distinctSupplierSubOrders = dueRequests
                .Select(r => r.Item.SupplierId)
                .Distinct()
                .Select(supplierId => (SupplierId: supplierId, SubOrder: order.SubOrders.FirstOrDefault(s => s.SupplierId == supplierId)))
                .Where(x => x.SubOrder != null)
                .ToList();

            // Xử lý riêng từng đơn con thuộc order; không trộn thông tin hoặc thông báo giữa các supplier.
            foreach (var (supplierId, subOrder) in distinctSupplierSubOrders)
            {
                // Gửi thông báo theo chính sách bắt lỗi của luồng; đây là bước sau thay đổi nghiệp vụ, không phải bằng chứng provider đã xử lý.
                try
                {
                    // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                    await notifier.NotifySupplierSubOrderChangedAsync(
                        supplierId,
                        new SupplierSubOrderChangedNotification(
                            Guid.NewGuid(),
                            order.Id,
                            subOrder!.Id,
                            order.Version,
                            OrderChangeTypes.ReturnConfirmationOverdue,
                            now),
                        cancellationToken);
                }
                // Phân biệt timeout của lời gọi con theo catch filter; cancellation của caller không được biến thành kết quả nghiệp vụ thành công.
                catch (OperationCanceledException)
                {
                    throw;
                }
                // Xử lý exception theo chính sách của nhánh này: chuyển thành lỗi/ghi nhận retry hoặc ném lại; không tự suy ra provider chưa thực thi.
                catch (Exception ex)
                {
                    // Ghi log/audit của bước xử lý bằng metadata; không coi log là nguồn trạng thái nghiệp vụ.
                    logger?.LogWarning(ex, "Failed to send ReturnConfirmationOverdue notification to supplier {SupplierId} for sub-order {SubOrderId}",
                        supplierId, subOrder!.Id);
                }
            }
        }

        return totalActionableUnits;
    }

    // Xử lý lần lượt batch order đến hạn; cô lập lỗi từng order và giữ cancellation của worker.
    public async Task<ReturnConfirmationOverdueBatchResult> ProcessBatchAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
        var dueOrderIds = await GetDueOrderIdsAsync(batchSize, cancellationToken);
        int processed = 0;
        int warned = 0;
        int failed = 0;

        // Duyệt tập dữ liệu của bước hiện tại để tích lũy kết quả, giữ nguyên phạm vi và thứ tự mà caller đã chọn.
        foreach (var orderId in dueOrderIds)
        {
            // Tôn trọng cancellation của caller/worker trước khi bắt đầu phần việc tiếp theo của batch.
            cancellationToken.ThrowIfCancellationRequested();
            // Thực hiện lời gọi có thể phát sinh exception; nhánh catch phía dưới quyết định trả lỗi, retry hoặc chuyển tiếp exception.
            try
            {
                // Xử lý từng order qua use case có khóa/kiểm tra lại, không đổi dữ liệu trực tiếp từ danh sách quét batch.
                var result = await ProcessOrderAsync(orderId, cancellationToken);
                // Xử lý kết quả thành công ở nhánh này; nhánh còn lại giữ cách trả lỗi hoặc đếm thất bại của caller.
                if (result.IsSuccess)
                {
                    // Cập nhật số lượng xử lý của batch để báo cáo tiến độ; không dùng bộ đếm thay cho trạng thái đã lưu của từng tác vụ.
                    warned += result.Value;
                    processed++;
                }
                else
                {
                    // Cập nhật số lượng xử lý của batch để báo cáo tiến độ; không dùng bộ đếm thay cho trạng thái đã lưu của từng tác vụ.
                    failed++;
                    logger?.LogWarning("Failed to process overdue return confirmation for order {OrderId}: {Error}", orderId, result.Error);
                }
            }
            // Phân biệt timeout của lời gọi con theo catch filter; cancellation của caller không được biến thành kết quả nghiệp vụ thành công.
            catch (OperationCanceledException)
            {
                throw;
            }
            // Xử lý exception theo chính sách của nhánh này: chuyển thành lỗi/ghi nhận retry hoặc ném lại; không tự suy ra provider chưa thực thi.
            catch (Exception ex)
            {
                // Cập nhật số lượng xử lý của batch để báo cáo tiến độ; không dùng bộ đếm thay cho trạng thái đã lưu của từng tác vụ.
                failed++;
                logger?.LogWarning(ex, "Unexpected error processing overdue return confirmation for order {OrderId}", orderId);
            }
        }

        return new ReturnConfirmationOverdueBatchResult(processed, warned, failed);
    }
}
