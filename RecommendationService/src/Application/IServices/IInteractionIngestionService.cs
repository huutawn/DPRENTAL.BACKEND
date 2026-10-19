using Dprental.RecommendationService.Application.DTOs;
using Dprental.RecommendationService.Application.Validation;

namespace Dprental.RecommendationService.Application.IServices;

public interface IInteractionIngestionService
{
    Task<EventProcessResult> ProcessEventAsync(string rawJson, InteractionEventSource source, CancellationToken cancellationToken);
}
