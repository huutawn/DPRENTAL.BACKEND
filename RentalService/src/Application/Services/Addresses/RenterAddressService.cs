using Dprental.RentalService.Application.DTOs.Addresses;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.Addresses;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Addresses;

/// <summary>
/// Sổ địa chỉ giao của người thuê: tối đa <see cref="MaxAddresses"/> địa chỉ, luôn có đúng một địa chỉ mặc định khi sổ không rỗng.
/// Mọi thao tác sửa đổi khóa theo người thuê để hai yêu cầu cùng lúc không làm sổ có hai địa chỉ mặc định.
/// </summary>
public sealed class RenterAddressService(
    IRenterAddressRepository addresses,
    IOrderLockManager locks,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IRenterAddressService
{
    public const int MaxAddresses = 10;

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    // Đọc danh sách địa chỉ giao hàng theo renter và chỉ trả các trường thuộc response công khai.
    public async Task<IReadOnlyList<RenterAddressResponse>> ListAsync(Guid renterId, CancellationToken cancellationToken = default)
    {
        // Đọc danh sách theo renter ở repository; không lấy các mục của người khác rồi mới lọc ở response.
        IReadOnlyList<RenterAddress> list = await addresses.ListByRenterAsync(renterId, cancellationToken);
        return list.Select(ToResponse).ToList();
    }

    // Tra cứu địa chỉ giao hàng trong phạm vi renter; không tìm thấy hoặc không thuộc sở hữu đều trả NotFound.
    public async Task<Result<RenterAddressResponse>> GetAsync(Guid renterId, Guid addressId, CancellationToken cancellationToken = default)
    {
        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        RenterAddress? address = await addresses.GetByIdAsync(renterId, addressId, cancellationToken);
        return address is null ? RenterAddressErrors.NotFound : ToResponse(address);
    }

    // Validate trước khi lấy khóa renter; đọc lại danh sách để kiểm tra giới hạn và tạo mục đầu tiên làm mặc định.
    public async Task<Result<RenterAddressResponse>> CreateAsync(
        Guid renterId,
        SaveRenterAddressRequest request,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra các trường đầu vào trước khi lấy khóa và ghi dữ liệu; dữ liệu sai được trả về bằng lỗi validation.
        if (Validate(request) is { } invalid) return invalid;

        return await WithRenterLockAsync<RenterAddressResponse>(renterId, async () =>
        {
            // Đọc danh sách của renter để sửa dưới khóa; các entity cần tracking để lưu thay đổi mặc định cùng lần SaveChanges.
            IReadOnlyList<RenterAddress> existing = await addresses.ListForUpdateAsync(renterId, cancellationToken);
            // Giới hạn sổ địa chỉ ở 10 mục; kiểm tra dưới khóa để hai request đồng thời không cùng vượt giới hạn.
            if (existing.Count >= MaxAddresses) return RenterAddressErrors.LimitReached;

            // Địa chỉ đầu tiên tự thành mặc định.
            RenterAddress address = new(
                Guid.NewGuid(),
                renterId,
                request.Province!.Trim(),
                request.AddressDetail!.Trim(),
                request.RecipientName!.Trim(),
                request.RecipientPhone!.Trim(),
                isDefault: existing.Count == 0,
                timeProvider.GetUtcNow());
            addresses.Add(address);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return ToResponse(address);
        }, cancellationToken);
    }

    // Khóa renter, tìm đúng mục thuộc sở hữu và cập nhật nội dung; giữ nguyên lựa chọn mặc định.
    public async Task<Result<RenterAddressResponse>> UpdateAsync(
        Guid renterId,
        Guid addressId,
        SaveRenterAddressRequest request,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra các trường đầu vào trước khi lấy khóa và ghi dữ liệu; dữ liệu sai được trả về bằng lỗi validation.
        if (Validate(request) is { } invalid) return invalid;

        return await WithRenterLockAsync<RenterAddressResponse>(renterId, async () =>
        {
            // Đọc danh sách của renter để sửa dưới khóa; các entity cần tracking để lưu thay đổi mặc định cùng lần SaveChanges.
            IReadOnlyList<RenterAddress> existing = await addresses.ListForUpdateAsync(renterId, cancellationToken);
            RenterAddress? address = existing.FirstOrDefault(a => a.Id == addressId);
            // Mục không có trong danh sách của renter thì trả NotFound, bao gồm ID có thật nhưng thuộc người khác.
            if (address is null) return RenterAddressErrors.NotFound;

            // Cập nhật các trường đã chuẩn hóa và mốc sửa, giữ nguyên ID/chủ sở hữu/cờ mặc định của mục hiện tại.
            address.Update(
                request.Province!.Trim(),
                request.AddressDetail!.Trim(),
                request.RecipientName!.Trim(),
                request.RecipientPhone!.Trim(),
                timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return ToResponse(address);
        }, cancellationToken);
    }

    // Xóa mục thuộc renter; nếu xóa mục mặc định thì chọn mục còn lại theo thứ tự repository trả về.
    public async Task<Result> DeleteAsync(Guid renterId, Guid addressId, CancellationToken cancellationToken = default)
    {
        // Chạy toàn bộ phần đọc lại và chỉnh sửa trong cùng khóa renter, tránh hai request cùng đổi trạng thái mặc định hoặc vượt giới hạn.
        Result<bool> result = await WithRenterLockAsync<bool>(renterId, async () =>
        {
            // Đọc danh sách của renter để sửa dưới khóa; các entity cần tracking để lưu thay đổi mặc định cùng lần SaveChanges.
            IReadOnlyList<RenterAddress> existing = await addresses.ListForUpdateAsync(renterId, cancellationToken);
            RenterAddress? address = existing.FirstOrDefault(a => a.Id == addressId);
            // Mục không có trong danh sách của renter thì trả NotFound, bao gồm ID có thật nhưng thuộc người khác.
            if (address is null) return RenterAddressErrors.NotFound;

            // Đăng ký xóa mục đã xác định thuộc sở hữu; các thay đổi mặc định liên quan được lưu cùng UnitOfWork.
            addresses.Remove(address);
            // Khi mục mặc định bị xóa, chuyển mặc định sang mục còn lại để danh sách không mất lựa chọn mặc định.
            if (address.IsDefault)
            {
                // Xóa địa chỉ mặc định thì địa chỉ mới nhất còn lại thành mặc định (danh sách đã xếp mới nhất trước).
                existing.FirstOrDefault(a => a.Id != addressId)?.SetDefault(true, timeProvider.GetUtcNow());
            }

            // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

        return result.IsFailure ? Result.Failure(result.Error) : Result.Success();
    }

    // Đổi mặc định dưới cùng khóa renter; cập nhật cả mục được chọn và các mục phải bỏ mặc định trong một lần lưu.
    public async Task<Result<RenterAddressResponse>> SetDefaultAsync(
        Guid renterId,
        Guid addressId,
        CancellationToken cancellationToken = default)
    {
        return await WithRenterLockAsync<RenterAddressResponse>(renterId, async () =>
        {
            // Đọc danh sách của renter để sửa dưới khóa; các entity cần tracking để lưu thay đổi mặc định cùng lần SaveChanges.
            IReadOnlyList<RenterAddress> existing = await addresses.ListForUpdateAsync(renterId, cancellationToken);
            RenterAddress? target = existing.FirstOrDefault(a => a.Id == addressId);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (target is null) return RenterAddressErrors.NotFound;

            // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
            DateTimeOffset utcNow = timeProvider.GetUtcNow();
            // Duyệt tập dữ liệu của bước hiện tại để tích lũy kết quả, giữ nguyên phạm vi và thứ tự mà caller đã chọn.
            foreach (RenterAddress address in existing)
            {
                // Đặt lại cờ mặc định theo ID đích để chỉ mục được chọn giữ trạng thái mặc định.
                address.SetDefault(address.Id == addressId, utcNow);
            }

            // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return ToResponse(target);
        }, cancellationToken);
    }

    // Tái sử dụng validator checkout để sổ địa chỉ giao hàng và dữ liệu dùng khi gửi đơn có cùng quy tắc đầu vào.
    private static Error? Validate(SaveRenterAddressRequest request)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(request);

        // Tích lũy lỗi theo trường/dòng để trả đủ thông tin sửa yêu cầu thay vì che lỗi thành dữ liệu hợp lệ.
        List<CheckoutFieldError> errors = [];
        CheckoutRequestValidator.ValidateAddress(
            request.Province, request.AddressDetail, request.RecipientName, request.RecipientPhone, string.Empty, errors);
        return errors.Count == 0
            ? null
            : RenterAddressErrors.InvalidRequest(string.Join(" ", errors.Select(e => e.Message)));
    }

    // Chạy thao tác dưới khóa renter có timeout; trả lỗi Busy khi không lấy được khóa và luôn nhả khóa sau xử lý.
    private async Task<Result<T>> WithRenterLockAsync<T>(
        Guid renterId,
        Func<Task<Result<T>>> action,
        CancellationToken cancellationToken)
    {
        // Giữ tài nguyên khóa trong phạm vi xử lý này; await using bảo đảm nhả khóa cả khi trả sớm hoặc phát sinh lỗi.
        IAsyncDisposable renterLock;
        // Lấy khóa có timeout và xử lý riêng việc không lấy được khóa, tránh chờ vô hạn khi có request cạnh tranh.
        try
        {
            // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
            renterLock = await locks.AcquireRenterLockAsync(renterId, LockTimeout, cancellationToken);
        }
        // Không lấy được khóa trong thời gian cho phép: trả trạng thái bận/chưa sẵn sàng để caller thử lại thay vì ghi đồng thời.
        catch (TimeoutException)
        {
            return RenterAddressErrors.Busy;
        }

        // Giữ tài nguyên khóa trong phạm vi xử lý này; await using bảo đảm nhả khóa cả khi trả sớm hoặc phát sinh lỗi.
        await using (renterLock)
        {
            return await action();
        }
    }

    // Chiếu entity địa chỉ giao hàng sang response, không đưa navigation hoặc dữ liệu persistence ra API.
    private static RenterAddressResponse ToResponse(RenterAddress address) => new(
        address.Id,
        address.Province,
        address.AddressDetail,
        address.RecipientName,
        address.RecipientPhone,
        address.IsDefault);
}
