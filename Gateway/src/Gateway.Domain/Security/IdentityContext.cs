namespace Dprental.Gateway.Domain.Security;

/// <summary>
/// Đại diện cho ngữ cảnh danh tính đáng tin cậy đã được Gateway xác thực và chuẩn hóa từ token JWT.
/// Dữ liệu này được sử dụng để chuyển tiếp an toàn xuống các downstream microservices.
/// </summary>
public sealed record IdentityContext
{
    /// <summary>
    /// Khởi tạo ngữ cảnh danh tính người dùng với các thông tin đã được trích xuất từ token.
    /// </summary>
    /// <param name="userId">Mã định danh người dùng duy nhất (từ claim sub).</param>
    /// <param name="partyId">Mã định danh đối tác/bên liên quan (từ claim party_id nếu có).</param>
    /// <param name="roles">Danh sách vai trò hợp lệ của người dùng.</param>
    /// <param name="permissions">Danh sách quyền hạn hoặc phạm vi (scope/permissions) của người dùng.</param>
    /// <param name="correlationId">Mã tương quan của request hiện tại.</param>
    /// <param name="isAuthenticated">Trạng thái xác thực của danh tính.</param>
    public IdentityContext(
        string? userId,
        string? partyId,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> permissions,
        string correlationId,
        bool isAuthenticated)
    {
        UserId = userId;
        PartyId = partyId;
        Roles = roles ?? Array.Empty<string>();
        Permissions = permissions ?? Array.Empty<string>();
        CorrelationId = correlationId ?? string.Empty;
        IsAuthenticated = isAuthenticated;
    }

    /// <summary>
    /// Mã định danh người dùng duy nhất (User ID), tương ứng với claim "sub".
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Mã định danh đối tác hoặc tổ chức liên kết (Party ID), tương ứng với claim "party_id".
    /// </summary>
    public string? PartyId { get; init; }

    /// <summary>
    /// Tập hợp các vai trò (Roles) của người dùng, tương ứng với các claim "role".
    /// </summary>
    public IReadOnlyList<string> Roles { get; init; }

    /// <summary>
    /// Tập hợp các quyền hạn hoặc phạm vi hoạt động (Permissions / Scopes) của người dùng.
    /// </summary>
    public IReadOnlyList<string> Permissions { get; init; }

    /// <summary>
    /// Mã tương quan phân tán (Correlation ID) cho request hiện tại.
    /// </summary>
    public string CorrelationId { get; init; }

    /// <summary>
    /// Cho biết danh tính này đã được xác thực thành công qua JWT hợp lệ hay chưa.
    /// </summary>
    public bool IsAuthenticated { get; init; }

    /// <summary>
    /// Biểu diễn ngữ cảnh rỗng dành cho request ẩn danh hoặc khi JWT không hợp lệ.
    /// </summary>
    public static readonly IdentityContext Anonymous = new(
        userId: null,
        partyId: null,
        roles: Array.Empty<string>(),
        permissions: Array.Empty<string>(),
        correlationId: string.Empty,
        isAuthenticated: false);
}
