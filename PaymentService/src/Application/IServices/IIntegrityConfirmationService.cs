namespace Dprental.PaymentService.Application.IServices;

public interface IIntegrityConfirmationService
{
    Task<int> ProcessConfirmationBatchAsync(CancellationToken cancellationToken = default);
}
