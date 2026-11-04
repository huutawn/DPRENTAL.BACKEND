using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Dprental.InventoryService.Application.Clients;

public sealed class SupplierOrganizationLookup(HttpClient client, ILogger<SupplierOrganizationLookup> logger) : ISupplierOrganizationLookup
{
    private sealed record Response(Guid OrganizationId);

    public async Task<Guid?> FindByOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        if (ownerUserId == Guid.Empty || !client.DefaultRequestHeaders.Contains("X-Inventory-Lookup-Key")) return null;
        try
        {
            using var response = await client.GetAsync($"api/v1/auth/internal/supplier-organizations/by-owner/{ownerUserId:D}", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var value = await response.Content.ReadFromJsonAsync<Response>(cancellationToken: cancellationToken);
            return value is { OrganizationId: var id } && id != Guid.Empty ? id : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Supplier organization lookup timed out.");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            logger.LogWarning("Supplier organization lookup unavailable: {ErrorType}", ex.GetType().Name);
            return null;
        }
    }
}
