namespace Dprental.PaymentService.Application.IServices;

public interface IIntegrityScannerService
{
    Task<int> ScanAuditHistoryAsync(CancellationToken cancellationToken = default);
}
