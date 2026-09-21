namespace Dprental.AuthService.Application.IServices;

public interface ICorrelationIdProvider
{
    string GetCorrelationId();
}
