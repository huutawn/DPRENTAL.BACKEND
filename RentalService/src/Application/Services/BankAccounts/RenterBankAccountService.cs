using Dprental.RentalService.Application.DTOs.BankAccounts;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.BankAccounts;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.BankAccounts;

/// <summary>
/// Sổ tài khoản ngân hàng của người thuê: tối đa <see cref="MaxBankAccounts"/> tài khoản, không trùng ngân hàng và số tài khoản,
/// luôn có đúng một tài khoản mặc định khi sổ không rỗng.
/// Sửa tài khoản giữ nguyên trạng thái mặc định.
/// Mọi thao tác sửa đổi khóa theo người thuê để hai yêu cầu cùng lúc không làm sổ có hai tài khoản mặc định hay hai tài khoản trùng.
/// </summary>
public sealed class RenterBankAccountService(
    IRenterBankAccountRepository bankAccounts,
    IOrderLockManager locks,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IRenterBankAccountService
{
    public const int MaxBankAccounts = 10;

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    // Đọc danh sách tài khoản ngân hàng theo renter và chỉ trả các trường thuộc response công khai.
    public async Task<IReadOnlyList<RenterBankAccountResponse>> ListAsync(Guid renterId, CancellationToken cancellationToken = default)
    {
        // Đọc danh sách theo renter ở repository; không lấy các mục của người khác rồi mới lọc ở response.
        IReadOnlyList<RenterBankAccount> list = await bankAccounts.ListByRenterAsync(renterId, cancellationToken);
        return list.Select(ToResponse).ToList();
    }

    // Tra cứu tài khoản ngân hàng trong phạm vi renter; không tìm thấy hoặc không thuộc sở hữu đều trả NotFound.
    public async Task<Result<RenterBankAccountResponse>> GetAsync(Guid renterId, Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        RenterBankAccount? bankAccount = await bankAccounts.GetByIdAsync(renterId, bankAccountId, cancellationToken);
        return bankAccount is null ? RenterBankAccountErrors.NotFound : ToResponse(bankAccount);
    }

    // Validate trước khi lấy khóa renter; đọc lại danh sách để kiểm tra giới hạn và tạo mục đầu tiên làm mặc định.
    public async Task<Result<RenterBankAccountResponse>> CreateAsync(
        Guid renterId,
        SaveRenterBankAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra các trường đầu vào trước khi lấy khóa và ghi dữ liệu; dữ liệu sai được trả về bằng lỗi validation.
        if (Validate(request) is { } invalid) return invalid;

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string bankCode = request.BankCode!.Trim();
        string accountNumber = request.AccountNumber!.Trim();

        return await WithRenterLockAsync<RenterBankAccountResponse>(renterId, async () =>
        {
            // Đọc danh sách của renter để sửa dưới khóa; các entity cần tracking để lưu thay đổi mặc định cùng lần SaveChanges.
            IReadOnlyList<RenterBankAccount> existing = await bankAccounts.ListForUpdateAsync(renterId, cancellationToken);
            // Kiểm tra thành viên trong tập liên quan để không áp dụng yêu cầu lên dữ liệu ngoài phạm vi đang xử lý.
            if (existing.Any(a => a.BankCode == bankCode && a.AccountNumber == accountNumber))
            {
                return RenterBankAccountErrors.Duplicate;
            }

            // Giới hạn sổ ngân hàng ở 10 mục; kiểm tra dưới khóa để hai request đồng thời không cùng vượt giới hạn.
            if (existing.Count >= MaxBankAccounts) return RenterBankAccountErrors.LimitReached;

            // Tài khoản đầu tiên tự thành mặc định.
            RenterBankAccount bankAccount = new(
                Guid.NewGuid(),
                renterId,
                bankCode,
                accountNumber,
                request.AccountHolderName!.Trim(),
                isDefault: existing.Count == 0,
                timeProvider.GetUtcNow());
            bankAccounts.Add(bankAccount);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return ToResponse(bankAccount);
        }, cancellationToken);
    }

    // Khóa renter, tìm đúng mục thuộc sở hữu và cập nhật nội dung; giữ nguyên lựa chọn mặc định.
    public async Task<Result<RenterBankAccountResponse>> UpdateAsync(
        Guid renterId,
        Guid bankAccountId,
        SaveRenterBankAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra các trường đầu vào trước khi lấy khóa và ghi dữ liệu; dữ liệu sai được trả về bằng lỗi validation.
        if (Validate(request) is { } invalid) return invalid;

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string bankCode = request.BankCode!.Trim();
        string accountNumber = request.AccountNumber!.Trim();

        return await WithRenterLockAsync<RenterBankAccountResponse>(renterId, async () =>
        {
            // Đọc danh sách của renter để sửa dưới khóa; các entity cần tracking để lưu thay đổi mặc định cùng lần SaveChanges.
            IReadOnlyList<RenterBankAccount> existing = await bankAccounts.ListForUpdateAsync(renterId, cancellationToken);
            RenterBankAccount? bankAccount = existing.FirstOrDefault(a => a.Id == bankAccountId);
            // Mục không có trong danh sách của renter thì trả NotFound, bao gồm ID có thật nhưng thuộc người khác.
            if (bankAccount is null) return RenterBankAccountErrors.NotFound;

            // Trùng với chính nó thì không tính: sửa mỗi tên chủ tài khoản vẫn được.
            if (existing.Any(a => a.Id != bankAccountId && a.BankCode == bankCode && a.AccountNumber == accountNumber))
            {
                return RenterBankAccountErrors.Duplicate;
            }

            // Cập nhật các trường đã chuẩn hóa và mốc sửa, giữ nguyên ID/chủ sở hữu/cờ mặc định của mục hiện tại.
            bankAccount.Update(bankCode, accountNumber, request.AccountHolderName!.Trim(), timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return ToResponse(bankAccount);
        }, cancellationToken);
    }

    // Xóa mục thuộc renter; nếu xóa mục mặc định thì chọn mục còn lại theo thứ tự repository trả về.
    public async Task<Result> DeleteAsync(Guid renterId, Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        // Chạy toàn bộ phần đọc lại và chỉnh sửa trong cùng khóa renter, tránh hai request cùng đổi trạng thái mặc định hoặc vượt giới hạn.
        Result<bool> result = await WithRenterLockAsync<bool>(renterId, async () =>
        {
            // Đọc danh sách của renter để sửa dưới khóa; các entity cần tracking để lưu thay đổi mặc định cùng lần SaveChanges.
            IReadOnlyList<RenterBankAccount> existing = await bankAccounts.ListForUpdateAsync(renterId, cancellationToken);
            RenterBankAccount? bankAccount = existing.FirstOrDefault(a => a.Id == bankAccountId);
            // Mục không có trong danh sách của renter thì trả NotFound, bao gồm ID có thật nhưng thuộc người khác.
            if (bankAccount is null) return RenterBankAccountErrors.NotFound;

            // Đăng ký xóa mục đã xác định thuộc sở hữu; các thay đổi mặc định liên quan được lưu cùng UnitOfWork.
            bankAccounts.Remove(bankAccount);
            // Khi mục mặc định bị xóa, chuyển mặc định sang mục còn lại để danh sách không mất lựa chọn mặc định.
            if (bankAccount.IsDefault)
            {
                // Xóa tài khoản mặc định thì tài khoản mới nhất còn lại thành mặc định (danh sách đã xếp mới nhất trước).
                existing.FirstOrDefault(a => a.Id != bankAccountId)?.SetDefault(true, timeProvider.GetUtcNow());
            }

            // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

        return result.IsFailure ? Result.Failure(result.Error) : Result.Success();
    }

    // Đổi mặc định dưới cùng khóa renter; cập nhật cả mục được chọn và các mục phải bỏ mặc định trong một lần lưu.
    public async Task<Result<RenterBankAccountResponse>> SetDefaultAsync(
        Guid renterId,
        Guid bankAccountId,
        CancellationToken cancellationToken = default)
    {
        return await WithRenterLockAsync<RenterBankAccountResponse>(renterId, async () =>
        {
            // Đọc danh sách của renter để sửa dưới khóa; các entity cần tracking để lưu thay đổi mặc định cùng lần SaveChanges.
            IReadOnlyList<RenterBankAccount> existing = await bankAccounts.ListForUpdateAsync(renterId, cancellationToken);
            RenterBankAccount? target = existing.FirstOrDefault(a => a.Id == bankAccountId);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (target is null) return RenterBankAccountErrors.NotFound;

            // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
            DateTimeOffset utcNow = timeProvider.GetUtcNow();
            // Duyệt tập dữ liệu của bước hiện tại để tích lũy kết quả, giữ nguyên phạm vi và thứ tự mà caller đã chọn.
            foreach (RenterBankAccount bankAccount in existing)
            {
                // Đặt lại cờ mặc định theo ID đích để chỉ mục được chọn giữ trạng thái mặc định.
                bankAccount.SetDefault(bankAccount.Id == bankAccountId, utcNow);
            }

            // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return ToResponse(target);
        }, cancellationToken);
    }

    // Tái sử dụng validator checkout để sổ tài khoản ngân hàng và dữ liệu dùng khi gửi đơn có cùng quy tắc đầu vào.
    private static Error? Validate(SaveRenterBankAccountRequest request)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(request);

        // Tích lũy lỗi theo trường/dòng để trả đủ thông tin sửa yêu cầu thay vì che lỗi thành dữ liệu hợp lệ.
        List<CheckoutFieldError> errors = [];
        CheckoutRequestValidator.ValidateBankAccount(
            request.BankCode, request.AccountNumber, request.AccountHolderName, string.Empty, errors);
        return errors.Count == 0
            ? null
            : RenterBankAccountErrors.InvalidRequest(string.Join(" ", errors.Select(e => e.Message)));
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
            return RenterBankAccountErrors.Busy;
        }

        // Giữ tài nguyên khóa trong phạm vi xử lý này; await using bảo đảm nhả khóa cả khi trả sớm hoặc phát sinh lỗi.
        await using (renterLock)
        {
            return await action();
        }
    }

    // Chiếu entity tài khoản ngân hàng sang response, không đưa navigation hoặc dữ liệu persistence ra API.
    private static RenterBankAccountResponse ToResponse(RenterBankAccount bankAccount) => new(
        bankAccount.Id,
        bankAccount.BankCode,
        bankAccount.AccountNumber,
        bankAccount.AccountHolderName,
        bankAccount.IsDefault);
}
