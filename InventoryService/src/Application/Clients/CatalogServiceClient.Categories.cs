using System.Net;
using System.Text.Json;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.Clients;

public sealed partial class CatalogServiceClient
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<Guid>>> GetEquipmentTypeIdsByCategoryAsync(
        Guid categoryId,
        CancellationToken cancellationToken = default)
    {
        if (categoryId == Guid.Empty)
        {
            return Error.Validation("INVALID_CATEGORY_ID", "Category ID không được rỗng.");
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var distinctIds = new HashSet<Guid>();
        int currentPage = 1;
        int expectedTotalPages = 1;
        int expectedTotalCount = 0;
        const int pageSize = 100;

        try
        {
            while (currentPage <= expectedTotalPages)
            {
                string relativeUri = $"api/v1/catalog/equipment-types?categoryId={categoryId:D}&page={currentPage}&pageSize={pageSize}&sort=name_asc";

                using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
                using var response = await _httpClient.SendAsync(request, operation.Token);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return Error.Unavailable("CATALOG_SERVICE_ERROR", $"CatalogService trả về mã lỗi HTTP {(int)response.StatusCode}.");
                }

                var content = await response.Content.ReadAsStringAsync(operation.Token);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "CatalogService trả về nội dung rỗng.");
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(content);
                }
                catch (JsonException ex)
                {
                    if (_logger is not null) LogJsonError(_logger, "get-equipment-types-by-category", ex);
                    return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phản hồi từ CatalogService không đúng định dạng JSON.");
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phản hồi từ CatalogService phải là JSON object.");
                    }

                    if (!root.TryGetProperty("items", out var itemsProp) || itemsProp.ValueKind != JsonValueKind.Array ||
                        !root.TryGetProperty("page", out var pageProp) || pageProp.ValueKind != JsonValueKind.Number || !pageProp.TryGetInt32(out var parsedPage) ||
                        !root.TryGetProperty("pageSize", out var pageSizeProp) || pageSizeProp.ValueKind != JsonValueKind.Number || !pageSizeProp.TryGetInt32(out var parsedPageSize) ||
                        !root.TryGetProperty("totalCount", out var totalCountProp) || totalCountProp.ValueKind != JsonValueKind.Number || !totalCountProp.TryGetInt32(out var parsedTotalCount) ||
                        !root.TryGetProperty("totalPages", out var totalPagesProp) || totalPagesProp.ValueKind != JsonValueKind.Number || !totalPagesProp.TryGetInt32(out var parsedTotalPages))
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phản hồi từ CatalogService thiếu metadata phân trang bắt buộc.");
                    }

                    if (parsedPage != currentPage || parsedPageSize != pageSize || parsedTotalCount < 0 || parsedTotalPages != (int)(((long)parsedTotalCount + pageSize - 1) / pageSize) ||
                        itemsProp.GetArrayLength() != Math.Min(pageSize, Math.Max(0, parsedTotalCount - (currentPage - 1) * pageSize)))
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Giá trị metadata phân trang không hợp lệ.");
                    }

                    if (currentPage == 1)
                    {
                        expectedTotalPages = parsedTotalPages;
                        expectedTotalCount = parsedTotalCount;
                        if (parsedTotalCount == 0 && parsedTotalPages == 0)
                        {
                            return Result.Success<IReadOnlyList<Guid>>(Array.Empty<Guid>());
                        }
                    }
                    else if (parsedTotalPages != expectedTotalPages || parsedTotalCount != expectedTotalCount)
                    {
                        return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Metadata totalPages thay đổi bất thường giữa các trang.");
                    }

                    foreach (var item in itemsProp.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object ||
                            !item.TryGetProperty("id", out var idProp) ||
                            idProp.ValueKind != JsonValueKind.String ||
                            !idProp.TryGetGuid(out var id) ||
                            id == Guid.Empty)
                        {
                            return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Phần tử equipment type thiếu 'id' hợp lệ.");
                        }

                        distinctIds.Add(id);
                    }
                }

                currentPage++;
            }

            if (distinctIds.Count != expectedTotalCount)
            {
                return Error.Unavailable("CATALOG_SERVICE_INVALID_RESPONSE", "Danh sách type không đủ số ID duy nhất đã công bố.");
            }
            return Result.Success<IReadOnlyList<Guid>>(distinctIds.ToList());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            if (_logger is not null) LogTimeoutResource(_logger, categoryId.ToString("D"), "get-equipment-types-by-category", ex);
            return Error.Unavailable("CATALOG_SERVICE_TIMEOUT", "Hết thời gian chờ phản hồi từ CatalogService.");
        }
        catch (HttpRequestException ex)
        {
            if (_logger is not null) LogNetworkErrorResource(_logger, categoryId.ToString("D"), "get-equipment-types-by-category", ex);
            return Error.Unavailable("CATALOG_SERVICE_NETWORK_ERROR", "Không thể kết nối tới CatalogService.");
        }
    }

}
