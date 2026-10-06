using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Common;

/// <summary>
/// Phân loại lỗi trả về trong hệ thống phục vụ ánh xạ HTTP status code hoặc xử lý nghiệp vụ.
/// </summary>
public enum ErrorType
{
    /// <summary>
    /// Lỗi dữ liệu đầu vào không hợp lệ (Validation).
    /// </summary>
    Validation = 0,

    /// <summary>
    /// Lỗi chưa xác thực danh tính (Unauthorized).
    /// </summary>
    Unauthorized = 1,

    /// <summary>
    /// Lỗi không có quyền truy cập tài nguyên (Forbidden).
    /// </summary>
    Forbidden = 2,

    /// <summary>
    /// Lỗi không tìm thấy tài nguyên (Not Found).
    /// </summary>
    NotFound = 3,

    /// <summary>
    /// Lỗi xung đột trạng thái hoặc dữ liệu trùng lặp (Conflict).
    /// </summary>
    Conflict = 4,

    /// <summary>
    /// Lỗi tài nguyên hoặc phiên đã hết hạn (Expired).
    /// </summary>
    Expired = 5,

    /// <summary>
    /// Lỗi vi phạm quy tắc nghiệp vụ không thể xử lý (Unprocessable Entity).
    /// </summary>
    Unprocessable = 6,

    /// <summary>
    /// Lỗi hệ thống hoặc thất bại chung không xác định (Internal Failure).
    /// </summary>
    Failure = 7,

    /// <summary>
    /// Lỗi dịch vụ phụ thuộc hoặc upstream tạm thời không khả dụng (Service Unavailable).
    /// </summary>
    Unavailable = 8
}

/// <summary>
/// Đại diện cho một lỗi validation chi tiết ở cấp trường (Field-level validation error).
/// Type trung lập độc lập với DTO Catalog và HTTP boundary.
/// </summary>
public sealed record ValidationError(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Đại diện cho một lỗi nghiệp vụ với mã lỗi, thông điệp, phân loại lỗi và danh sách lỗi validation tùy chọn.
/// </summary>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Consumed only from C#.")]
public sealed record Error(
    string Code,
    string Message,
    ErrorType Type = ErrorType.Validation,
    IReadOnlyList<ValidationError>? Errors = null)
{
    /// <summary>
    /// Đối tượng lỗi rỗng đại diện cho trạng thái không có lỗi.
    /// </summary>
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.Failure);

    /// <summary>
    /// Tạo lỗi validation dữ liệu đầu vào.
    /// </summary>
    public static Error Validation(string code, string message, IReadOnlyList<ValidationError>? errors = null) =>
        new(code, message, ErrorType.Validation, errors);
    /// <summary>
    /// Tạo lỗi chưa xác thực tài khoản.
    /// </summary>
    public static Error Unauthorized(string code, string message) =>
        new(code, message, ErrorType.Unauthorized);

    /// <summary>
    /// Tạo lỗi bị cấm truy cập tài nguyên.
    /// </summary>
    public static Error Forbidden(string code, string message) =>
        new(code, message, ErrorType.Forbidden);

    /// <summary>
    /// Tạo lỗi không tìm thấy tài nguyên.
    /// </summary>
    public static Error NotFound(string code, string message) =>
        new(code, message, ErrorType.NotFound);

    /// <summary>
    /// Tạo lỗi xung đột trạng thái tài nguyên.
    /// </summary>
    public static Error Conflict(string code, string message) =>
        new(code, message, ErrorType.Conflict);

    /// <summary>
    /// Tạo lỗi tài nguyên đã hết hạn.
    /// </summary>
    public static Error Expired(string code, string message) =>
        new(code, message, ErrorType.Expired);

    /// <summary>
    /// Tạo lỗi vi phạm quy tắc nghiệp vụ.
    /// </summary>
    public static Error Unprocessable(string code, string message) =>
        new(code, message, ErrorType.Unprocessable);

    /// <summary>
    /// Tạo lỗi thất bại hệ thống.
    /// </summary>
    public static Error Failure(string code, string message) =>
        new(code, message, ErrorType.Failure);

    /// <summary>
    /// Tạo lỗi dịch vụ hoặc tài nguyên phụ thuộc không khả dụng.
    /// </summary>
    public static Error Unavailable(string code, string message) =>
        new(code, message, ErrorType.Unavailable);
}

