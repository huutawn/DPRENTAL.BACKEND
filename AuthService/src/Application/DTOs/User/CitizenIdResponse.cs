namespace Dprental.AuthService.Application.DTOs.User;

/// <summary>
/// CCCD đầy đủ của chính người dùng — chỉ trả qua GET /users/me/citizen-id khi người dùng chủ động xem.
/// GET /users/me vẫn chỉ trả số đã che.
/// </summary>
/// <param name="CitizenId">Null nếu người dùng chưa nhập CCCD (chưa hoàn thiện hồ sơ).</param>
public sealed record CitizenIdResponse(string? CitizenId);
