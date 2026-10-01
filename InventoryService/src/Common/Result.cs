namespace Dprental.InventoryService.Common;

/// <summary>
/// Kết quả thực thi của một thao tác nghiệp vụ không trả về giá trị (chỉ có thành công hoặc thất bại).
/// </summary>
public class Result
{
    /// <summary>
    /// Khởi tạo đối tượng Result với trạng thái và thông tin lỗi.
    /// </summary>
    protected Result(bool isSuccess, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (isSuccess != (error == Error.None))
        {
            throw new ArgumentException("Thao tác thành công không thể chứa lỗi và thao tác thất bại phải chứa lỗi.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    /// <summary>
    /// Xác định thao tác có thành công hay không.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Xác định thao tác có thất bại hay không.
    /// </summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// Chi tiết lỗi nếu thao tác thất bại (trả về Error.None nếu thành công).
    /// </summary>
    public Error Error { get; }

    /// <summary>
    /// Tạo kết quả thành công.
    /// </summary>
    public static Result Success() => new(true, Error.None);

    /// <summary>
    /// Tạo kết quả thất bại kèm thông tin lỗi.
    /// </summary>
    public static Result Failure(Error error) => new(false, error);

    /// <summary>
    /// Tạo kết quả thành công chứa giá trị trả về.
    /// </summary>
    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);

    /// <summary>
    /// Tạo kết quả thất bại cho thao tác có kiểu trả về.
    /// </summary>
    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);

    /// <summary>
    /// Ép kiểu ngầm định từ Error sang Result thất bại.
    /// </summary>
    public static implicit operator Result(Error error) => Failure(error);
}

/// <summary>
/// Kết quả thực thi của một thao tác nghiệp vụ có trả về giá trị kiểu TValue.
/// </summary>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, Error error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    /// <summary>
    /// Giá trị trả về khi thao tác thành công.
    /// </summary>
    /// <exception cref="InvalidOperationException">Ném ra nếu truy cập giá trị từ kết quả thất bại.</exception>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Không thể truy xuất giá trị từ một kết quả thất bại.");

    /// <summary>
    /// Ép kiểu ngầm định từ giá trị sang Result thành công.
    /// </summary>
    public static implicit operator Result<TValue>(TValue value) => Success(value);

    /// <summary>
    /// Ép kiểu ngầm định từ Error sang Result thất bại.
    /// </summary>
    public static implicit operator Result<TValue>(Error error) => Failure<TValue>(error);
}
