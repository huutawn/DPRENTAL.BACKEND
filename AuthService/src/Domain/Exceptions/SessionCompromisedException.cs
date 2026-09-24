namespace Dprental.AuthService.Domain.Exceptions;

public class SessionCompromisedException(Guid userId, Guid sessionId) : Exception("Phát hiện tái sử dụng Refresh Token.")
{
    public Guid UserId { get; } = userId;
    public Guid SessionId { get; } = sessionId;
}