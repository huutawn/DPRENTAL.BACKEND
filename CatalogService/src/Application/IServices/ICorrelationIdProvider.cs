namespace Dprental.CatalogService.Application.IServices;

/// <summary>Cung cấp correlation ID của request hiện tại.</summary>
public interface ICorrelationIdProvider
{
    string GetCorrelationId();
}
