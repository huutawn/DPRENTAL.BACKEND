using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Cart;

/// <remarks>
/// Luồng ghi giỏ: nhận danh tính và dữ liệu dòng thuê → đánh giá bằng RentalCartItemEvaluator → lưu qua repository.
/// Giỏ chỉ thể hiện ý định thuê; thêm/sửa giỏ chưa giữ allocation và chưa chốt giá của đơn hàng.
/// </remarks>
public sealed class RentalCartUseCases(
    IRentalCartRepository carts,
    RentalCartItemEvaluator evaluator,
    IOrderLockManager lockManager,
    IUnitOfWork unitOfWork,
    IRentalEventProducer eventProducer,
    TimeProvider timeProvider)
{
    // Khóa giỏ theo renter, gộp dòng cùng listing/khoảng ngày, kiểm tra số lượng sau gộp rồi lưu giỏ cùng event nếu thêm dòng mới.
    public async Task<Result<RentalCartItem>> AddCartItemAsync(
        Guid renterId,
        Guid equipmentListingId,
        int quantity,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        // Từ chối định danh rỗng trước khi tra cứu hoặc gắn dữ liệu vào tài nguyên của luồng.
        if (renterId == Guid.Empty)
            return RentalErrors.CartNotFound;

        // Từ chối định danh rỗng trước khi tra cứu hoặc gắn dữ liệu vào tài nguyên của luồng.
        if (equipmentListingId == Guid.Empty)
            return RentalErrors.CartItemNotFound;

        // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
        if (quantity <= 0)
            return RentalErrors.InvalidQuantity;

        // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
        Result dateCheck = evaluator.ValidateRentalDates(startDate, endDate);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (dateCheck.IsFailure)
            return dateCheck.Error;

        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var renterLock = await lockManager.AcquireRenterLockAsync(
            renterId,
            TimeSpan.FromSeconds(5),
            cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        RentalCart? cart = await carts.GetActiveByRenterIdAsync(renterId, cancellationToken);
        bool isNewCart = false;

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (cart is null || cart.Status != RentalCartStatus.Active)
        {
            // Tạo giỏ active khi renter chưa có giỏ hoạt động, giữ riêng cờ để persistence dùng nhánh INSERT.
            Result<RentalCart> newCart = RentalCart.Create(
                Guid.NewGuid(),
                renterId,
                timeProvider.GetUtcNow());

            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (newCart.IsFailure)
                return newCart.Error;

            // Dùng giỏ mới đã tạo hợp lệ và đăng ký lưu; giỏ cũ sẽ đi qua nhánh cập nhật thay vì tạo thêm giỏ active.
            cart = newCart.Value;
            isNewCart = true;
        }

        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
        RentalCartItem? existing = cart.Items.FirstOrDefault(i =>
            i.EquipmentListingId == equipmentListingId &&
            i.ExpectedStartDate == startDate &&
            i.ExpectedEndDate == endDate);

        // Tính số lượng sau thao tác hoặc sau gộp dòng trước khi kiểm tra availability; không chỉ kiểm tra phần tăng thêm.
        int targetQuantity = quantity;
        // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
        if (existing is not null)
        {
            // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
            if (quantity > int.MaxValue - existing.Quantity)
                return RentalErrors.QuantityOverflow;

            // Tính số lượng sau thao tác hoặc sau gộp dòng trước khi kiểm tra availability; không chỉ kiểm tra phần tăng thêm.
            targetQuantity = existing.Quantity + quantity;
        }

        // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
        Result<RentalCartItemEvaluationResult> evalResult = await evaluator.EvaluateItemAsync(
            renterId,
            equipmentListingId,
            targetQuantity,
            startDate,
            endDate,
            cancellationToken);

        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (evalResult.IsFailure)
        {
            return evalResult.Error;
        }

        // Sử dụng dữ liệu của kết quả đã kiểm tra ở bước trước để dựng quyết định tiếp theo, không gọi lại để thay snapshot giữa luồng.
        RentalCartItemEvaluationResult evaluation = evalResult.Value;
        Result checkResult = MapEvaluationToMutationResult(evaluation, allowDatePassed: false);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (checkResult.IsFailure)
        {
            return checkResult.Error;
        }

        // Sử dụng dữ liệu của kết quả đã kiểm tra ở bước trước để dựng quyết định tiếp theo, không gọi lại để thay snapshot giữa luồng.
        RentalListingSnapshot? listing = evaluation.Snapshot;
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (listing is null)
        {
            return RentalErrors.ListingUnavailable;
        }

        // Dừng nhánh không hợp lệ với mã INVALID_EQUIPMENT_TYPE_ID; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (listing.EquipmentTypeId == Guid.Empty)
        {
            return Error.Validation("INVALID_EQUIPMENT_TYPE_ID", "EquipmentTypeId của listing không được để trống hoặc rỗng.");
        }

        // Phân biệt tạo dòng mới với tăng số lượng để chỉ phát CartItemAdded cho lần tạo dòng.
        bool isNewItem = existing is null;

        // Áp thao tác thêm/gộp dòng vào giỏ sau kiểm tra availability; giá snapshot vẫn thuộc checkout, không được cố định tại giỏ.
        Result<RentalCartItem> added = cart.AddItem(
            Guid.NewGuid(),
            equipmentListingId,
            listing.OwnerId,
            listing.EquipmentTypeId,
            quantity,
            startDate,
            endDate,
            timeProvider.GetUtcNow());

        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (added.IsFailure)
            return added.Error;

        // Mở transaction cho phần ghi local; các thay đổi và Outbox trong phạm vi này phải được commit/rollback cùng nhau.
        await using var tx = await unitOfWork.BeginTransactionAsync(cancellationToken);
        // Thực hiện phần ghi có xử lý lỗi: nhánh catch rollback/bỏ thay đổi theo ranh giới transaction của luồng này.
        try
        {
            // Dùng giỏ mới đã tạo hợp lệ và đăng ký lưu; giỏ cũ sẽ đi qua nhánh cập nhật thay vì tạo thêm giỏ active.
            if (isNewCart)
            {
                // Dùng giỏ mới đã tạo hợp lệ và đăng ký lưu; giỏ cũ sẽ đi qua nhánh cập nhật thay vì tạo thêm giỏ active.
                await carts.CreateAsync(cart, cancellationToken);
            }
            else
            {
                // Đăng ký thay đổi aggregate với repository trước khi lưu; không truy cập DbContext trực tiếp từ service.
                bool updated = await carts.UpdateAsync(cart, cancellationToken);
                // Dừng nhánh không hợp lệ với mã CONCURRENCY_CONFLICT; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
                if (!updated)
                {
                    // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                    await tx.RollbackAsync(cancellationToken);
                    unitOfWork.DiscardChanges();
                    return Error.Conflict("CONCURRENCY_CONFLICT", "Giỏ hàng đã bị thay đổi đồng thời.");
                }
            }

            // Chỉ phát sự kiện thêm dòng khi dòng mới được tạo, không phát lại cho thao tác tăng số lượng dòng đã có.
            if (isNewItem)
            {
                // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
                var enqueueResult = await eventProducer.EnqueueCartItemAddedAsync(
                    new CartItemAddedPayload(
                        renterId,
                        equipmentListingId,
                        listing.EquipmentTypeId,
                        cart.Id,
                        added.Value.Id),
                    cancellationToken: cancellationToken);

                // Không ghi tiếp khi bước xử lý lỗi: rollback phần đang làm và bỏ thay đổi tracked trước khi trả lỗi.
                if (enqueueResult.IsFailure)
                {
                    // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
                    await tx.RollbackAsync(cancellationToken);
                    unitOfWork.DiscardChanges();
                    return enqueueResult.Error;
                }
            }

            // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return added.Value;
        }
        // Lỗi trong phạm vi ghi: rollback transaction và bỏ graph đang tracked trước khi trả lỗi hoặc ném lại exception.
        catch (Exception)
        {
            // Rollback transaction hiện tại; bước sau bỏ thay đổi tracked để không vô tình lưu lại graph đã thất bại.
            await tx.RollbackAsync(cancellationToken);
            unitOfWork.DiscardChanges();
            throw;
        }
    }

    // Chỉ nhận một kiểu chỉnh sửa mỗi request: số lượng hoặc đủ cặp ngày; chuyển sang đúng use case để dùng chung validation.
    public async Task<Result> UpdateCartItemAsync(
        Guid renterId,
        Guid cartItemId,
        UpdateCartItemRequest request,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(request);

        // Xác định loại chỉnh sửa: phải có đủ cặp ngày hoặc một số lượng, không nhận request trộn hai kiểu cập nhật.
        bool hasAnyDate = request.ExpectedStartDate is not null || request.ExpectedEndDate is not null;
        bool hasBothDates = request.ExpectedStartDate is not null && request.ExpectedEndDate is not null;
        bool hasQuantity = request.Quantity is not null;

        // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
        if ((hasAnyDate && !hasBothDates) || hasQuantity == hasBothDates)
        {
            return RentalErrors.InvalidCartUpdate;
        }

        // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
        if (hasQuantity)
        {
            return await UpdateCartItemQuantityAsync(
                renterId, cartItemId, request.Quantity.GetValueOrDefault(), cancellationToken);
        }

        return await UpdateCartItemDatesAsync(
            renterId, cartItemId, request.ExpectedStartDate.GetValueOrDefault(), request.ExpectedEndDate.GetValueOrDefault(), cancellationToken);
    }

    // Đọc dòng giỏ dưới khóa renter và đánh giá lại với số lượng mới; phản hồi conflict nếu persistence báo thay đổi đồng thời.
    public async Task<Result> UpdateCartItemQuantityAsync(
        Guid renterId,
        Guid cartItemId,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
        if (quantity < 1)
            return RentalErrors.InvalidQuantity;

        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var renterLock = await lockManager.AcquireRenterLockAsync(
            renterId, TimeSpan.FromSeconds(5), cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        RentalCart? cart = await GetActiveCartAsync(renterId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (cart is null)
            return RentalErrors.CartNotFound;

        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
        RentalCartItem? item = cart.Items.FirstOrDefault(i => i.Id == cartItemId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (item is null)
            return RentalErrors.CartItemNotFound;

        // Đánh giá dữ liệu đích bằng evaluator duy nhất (không tự gọi availability hay check riêng)
        Result<RentalCartItemEvaluationResult> evalResult = await evaluator.EvaluateItemAsync(
            renterId,
            item.EquipmentListingId,
            quantity,
            item.ExpectedStartDate,
            item.ExpectedEndDate,
            cancellationToken);

        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (evalResult.IsFailure)
        {
            return evalResult.Error;
        }

        // Sử dụng dữ liệu của kết quả đã kiểm tra ở bước trước để dựng quyết định tiếp theo, không gọi lại để thay snapshot giữa luồng.
        RentalCartItemEvaluationResult evaluation = evalResult.Value;
        Result checkResult = MapEvaluationToMutationResult(evaluation, allowDatePassed: true);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (checkResult.IsFailure)
        {
            return checkResult.Error;
        }

        // Lấy owner từ snapshot đã đánh giá để thao tác sửa giỏ kiểm tra đúng chủ listing hiện tại.
        Guid ownerId = evaluation.Snapshot?.OwnerId ?? Guid.Empty;
        // Từ chối định danh rỗng trước khi tra cứu hoặc gắn dữ liệu vào tài nguyên của luồng.
        if (ownerId == Guid.Empty)
        {
            return RentalErrors.ListingUnavailable;
        }

        // Đổi số lượng trên đúng dòng đã được kiểm tra; không chỉnh ngày hoặc tạo hold ở thao tác sửa giỏ.
        Result updated = cart.UpdateItemQuantity(
            cartItemId,
            ownerId,
            quantity,
            timeProvider.GetUtcNow());

        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (updated.IsFailure)
            return updated.Error;

        return await carts.UpdateAsync(cart, cancellationToken)
            ? Result.Success()
            : Error.Conflict("CONCURRENCY_CONFLICT", "Giỏ hàng đã bị thay đổi đồng thời.");
    }

    // Kiểm tra ngày mới và availability dưới khóa renter trước khi thay khoảng thuê của dòng giỏ.
    public async Task<Result> UpdateCartItemDatesAsync(
        Guid renterId,
        Guid cartItemId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
        Result dateCheck = evaluator.ValidateRentalDates(startDate, endDate);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (dateCheck.IsFailure)
            return dateCheck.Error;

        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var renterLock = await lockManager.AcquireRenterLockAsync(
            renterId, TimeSpan.FromSeconds(5), cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        RentalCart? cart = await GetActiveCartAsync(renterId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (cart is null)
            return RentalErrors.CartNotFound;

        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
        RentalCartItem? item = cart.Items.FirstOrDefault(i => i.Id == cartItemId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (item is null)
            return RentalErrors.CartItemNotFound;

        // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
        RentalCartItem? duplicate = cart.Items.FirstOrDefault(i =>
            i.Id != cartItemId &&
            i.EquipmentListingId == item.EquipmentListingId &&
            i.ExpectedStartDate == startDate &&
            i.ExpectedEndDate == endDate);

        // Tính số lượng sau thao tác hoặc sau gộp dòng trước khi kiểm tra availability; không chỉ kiểm tra phần tăng thêm.
        int targetQuantity = item.Quantity;
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (duplicate is not null)
        {
            // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
            if (item.Quantity > int.MaxValue - duplicate.Quantity)
                return RentalErrors.QuantityOverflow;

            // Tính số lượng sau thao tác hoặc sau gộp dòng trước khi kiểm tra availability; không chỉ kiểm tra phần tăng thêm.
            targetQuantity = duplicate.Quantity + item.Quantity;
        }

        // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
        Result<RentalCartItemEvaluationResult> evalResult = await evaluator.EvaluateItemAsync(
            renterId,
            item.EquipmentListingId,
            targetQuantity,
            startDate,
            endDate,
            cancellationToken);

        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (evalResult.IsFailure)
        {
            return evalResult.Error;
        }

        // Sử dụng dữ liệu của kết quả đã kiểm tra ở bước trước để dựng quyết định tiếp theo, không gọi lại để thay snapshot giữa luồng.
        RentalCartItemEvaluationResult evaluation = evalResult.Value;
        Result checkResult = MapEvaluationToMutationResult(evaluation, allowDatePassed: false);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (checkResult.IsFailure)
        {
            return checkResult.Error;
        }

        // Lấy owner từ snapshot đã đánh giá để thao tác sửa giỏ kiểm tra đúng chủ listing hiện tại.
        Guid ownerId = evaluation.Snapshot?.OwnerId ?? Guid.Empty;
        // Từ chối định danh rỗng trước khi tra cứu hoặc gắn dữ liệu vào tài nguyên của luồng.
        if (ownerId == Guid.Empty)
        {
            return RentalErrors.ListingUnavailable;
        }

        // Đổi ngày qua thao tác aggregate hiện có để xử lý trường hợp trùng dòng; availability đã được kiểm tra cho số lượng sau gộp.
        Result updated = cart.UpdateItemDates(
            cartItemId,
            ownerId,
            startDate,
            endDate,
            timeProvider.GetUtcNow());

        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (updated.IsFailure)
            return updated.Error;

        return await carts.UpdateAsync(cart, cancellationToken)
            ? Result.Success()
            : Error.Conflict("CONCURRENCY_CONFLICT", "Giỏ hàng đã bị thay đổi đồng thời.");
    }

    // Chỉ xóa dòng trong giỏ active của renter; báo lỗi khi không tìm thấy hoặc khi cập nhật bị tranh chấp.
    public async Task<Result> RemoveCartItemAsync(
        Guid renterId,
        Guid cartItemId,
        CancellationToken cancellationToken = default)
    {
        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var renterLock = await lockManager.AcquireRenterLockAsync(
            renterId, TimeSpan.FromSeconds(5), cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        RentalCart? cart = await GetActiveCartAsync(renterId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (cart is null)
            return RentalErrors.CartNotFound;

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        Result removed = cart.RemoveItem(cartItemId, timeProvider.GetUtcNow());
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (removed.IsFailure)
            return removed.Error;

        return await carts.UpdateAsync(cart, cancellationToken)
            ? Result.Success()
            : Error.Conflict("CONCURRENCY_CONFLICT", "Giỏ hàng đã bị thay đổi đồng thời.");
    }

    // Làm rỗng giỏ active dưới khóa renter; không tác động đến các đơn hoặc hold đã tạo từ checkout trước đó.
    public async Task<Result> ClearCartAsync(
        Guid renterId,
        CancellationToken cancellationToken = default)
    {
        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var renterLock = await lockManager.AcquireRenterLockAsync(
            renterId, TimeSpan.FromSeconds(5), cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        RentalCart? cart = await GetActiveCartAsync(renterId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (cart is null)
            return RentalErrors.CartNotFound;

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        Result cleared = cart.ClearItems(timeProvider.GetUtcNow());
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (cleared.IsFailure)
            return cleared.Error;

        return await carts.UpdateAsync(cart, cancellationToken)
            ? Result.Success()
            : Error.Conflict("CONCURRENCY_CONFLICT", "Giỏ hàng đã bị thay đổi đồng thời.");
    }

    // Đổi trạng thái đánh giá dòng thành lỗi của lệnh sửa giỏ; nhánh cho phép ngày đã qua được điều khiển bởi caller.
    private static Result MapEvaluationToMutationResult(
        RentalCartItemEvaluationResult evaluation,
        bool allowDatePassed)
    {
        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (evaluation.Status == RentalCartLineStatus.Available)
        {
            return Result.Success();
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (evaluation.Status == RentalCartLineStatus.DatePassed && allowDatePassed)
        {
            return Result.Success();
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (evaluation.Status == RentalCartLineStatus.Unavailable ||
            evaluation.Status == RentalCartLineStatus.ListingDisabled)
        {
            return RentalErrors.ListingUnavailable;
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (evaluation.InteractionStatus == RentalCartItemInteractionStatus.DisabledReadOnly &&
            evaluation.Status == RentalCartLineStatus.ValidationFailed)
        {
            return RentalErrors.CannotRentOwnListing;
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (evaluation.Status == RentalCartLineStatus.InsufficientQuantity)
        {
            return RentalErrors.InsufficientAvailability(evaluation.AvailableQuantity ?? 0);
        }

        // Giữ mã lỗi cụ thể của evaluator khi có; chỉ dùng mã mặc định khi nhánh đánh giá không cung cấp mã riêng.
        string errorCode = !string.IsNullOrEmpty(evaluation.ErrorCode)
            ? evaluation.ErrorCode
            : (evaluation.Status == RentalCartLineStatus.DatePassed ? "Cart.StartDateInvalid" : "Cart.ValidationFailed");

        return Error.Validation(errorCode, evaluation.Message ?? "Mục thuê không hợp lệ.");
    }

    // Chỉ trả giỏ còn Active của renter, tránh sửa giỏ đã kết thúc checkout.
    private async Task<RentalCart?> GetActiveCartAsync(
        Guid renterId,
        CancellationToken cancellationToken)
    {
        // Từ chối định danh rỗng trước khi tra cứu hoặc gắn dữ liệu vào tài nguyên của luồng.
        if (renterId == Guid.Empty)
            return null;

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        RentalCart? cart = await carts.GetActiveByRenterIdAsync(renterId, cancellationToken);
        return cart?.Status == RentalCartStatus.Active ? cart : null;
    }
}
