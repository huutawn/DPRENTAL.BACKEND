using System.Net.Http.Headers;
using System.Text.Json;
using Dprental.InventoryService.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Dprental.InventoryService.Application.Clients;

public sealed class OrganizationMembershipClient(HttpClient client, IHttpContextAccessor contextAccessor) : IOrganizationMembershipClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed record RemoteMembership(Guid OrganizationId, string? RoleName, JsonElement Type,
        JsonElement OrganizationStatus, JsonElement MembershipStatus);

    public async Task<Result<IReadOnlyList<OrganizationMembershipSnapshot>>> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var feature = contextAccessor.HttpContext?.Features.Get<IAuthenticateResultFeature>();
        var token = feature?.AuthenticateResult is { Succeeded: true } authentication
            ? authentication.Properties?.GetTokenValue("access_token") : null;
        if (string.IsNullOrWhiteSpace(token))
            return Error.Unauthorized("AUTH_TOKEN_REQUIRED", "Không tìm thấy thông tin đăng nhập để xác minh tổ chức.");
        if (!client.DefaultRequestHeaders.TryGetValues("X-Api-Key", out var apiKeys) ||
            !apiKeys.Any(value => !string.IsNullOrWhiteSpace(value)))
            return Error.Unavailable("AUTH_SERVICE_NOT_CONFIGURED", "Chưa cấu hình xác thực nội bộ tới Auth.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/auth/users/organizations");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Error.Unavailable("AUTH_SERVICE_UNAVAILABLE", "Không thể xác minh tổ chức với Auth.");
            var values = await JsonSerializer.DeserializeAsync<RemoteMembership[]>(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                JsonOptions, cancellationToken);
            if (values is null || values.Any(m => m is null || m.OrganizationId == Guid.Empty || string.IsNullOrWhiteSpace(m.RoleName)))
                return Error.Unavailable("AUTH_SERVICE_INVALID_RESPONSE", "Auth trả dữ liệu tổ chức không hợp lệ.");
            var result = new List<OrganizationMembershipSnapshot>(values.Length);
            foreach (var membership in values)
            {
                if (!TryReadEnum(membership.Type, ["Platform", "Supplier", "Renter"], out var type) ||
                    !TryReadEnum(membership.OrganizationStatus, ["Pending", "Active", "Rejected", "Suspended"], out var organizationStatus) ||
                    !TryReadEnum(membership.MembershipStatus, ["Invited", "Active", "Suspended"], out var membershipStatus))
                    return Error.Unavailable("AUTH_SERVICE_INVALID_RESPONSE", "Auth trả trạng thái tổ chức không hợp lệ.");
                result.Add(new OrganizationMembershipSnapshot(membership.OrganizationId, membership.RoleName!, type, organizationStatus, membershipStatus));
            }
            return Result.Success<IReadOnlyList<OrganizationMembershipSnapshot>>(result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Unavailable("AUTH_SERVICE_UNAVAILABLE", "Hết thời gian chờ xác minh tổ chức.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return Error.Unavailable("AUTH_SERVICE_UNAVAILABLE", "Không thể xác minh tổ chức với Auth.");
        }
    }

    private static bool TryReadEnum(JsonElement element, string[] names, out int result)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out result))
            return result >= 0 && result < names.Length;
        if (element.ValueKind == JsonValueKind.String)
        {
            result = Array.FindIndex(names, value => string.Equals(value, element.GetString(), StringComparison.OrdinalIgnoreCase));
            return result >= 0;
        }
        result = -1;
        return false;
    }
}
