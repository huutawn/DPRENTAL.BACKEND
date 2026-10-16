using System.Net;
using System.Text.Json;
using Dprental.InventoryService.Common;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Clients;

public sealed partial class CatalogServiceClientTests
{
    [Fact]
    public async Task GetEquipmentTypeIdsByCategory_0Types_ReturnsEmptyList()
    {
        var categoryId = Guid.NewGuid();
        var (client, _) = CreateTestClient((req, ct) =>
        {
            Assert.Contains($"categoryId={categoryId:D}", req.RequestUri?.Query);
            Assert.Contains("page=1", req.RequestUri?.Query);
            Assert.Contains("pageSize=100", req.RequestUri?.Query);
            Assert.Contains("sort=name_asc", req.RequestUri?.Query);

            var json = JsonSerializer.Serialize(new
            {
                items = Array.Empty<object>(),
                page = 1,
                pageSize = 100,
                totalCount = 0,
                totalPages = 0
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetEquipmentTypeIdsByCategoryAsync(categoryId);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Value);
    }

    [Fact]
    public async Task GetEquipmentTypeIdsByCategory_1Type_ReturnsOneId()
    {
        var categoryId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                items = new[] { new { id = typeId } },
                page = 1,
                pageSize = 100,
                totalCount = 1,
                totalPages = 1
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetEquipmentTypeIdsByCategoryAsync(categoryId);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Value);
        Assert.Equal(typeId, result.Value.Value[0]);
    }

    [Fact]
    public async Task GetEquipmentTypeIdsByCategory_100Types_Returns100IdsInOnePage()
    {
        var categoryId = Guid.NewGuid();
        var typeIds = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToList();

        int callCount = 0;
        var (client, _) = CreateTestClient((req, ct) =>
        {
            callCount++;
            var json = JsonSerializer.Serialize(new
            {
                items = typeIds.Select(id => new { id }),
                page = 1,
                pageSize = 100,
                totalCount = 100,
                totalPages = 1
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetEquipmentTypeIdsByCategoryAsync(categoryId);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, callCount);
        Assert.Equal(100, result.Value.Value.Count);
    }

    [Fact]
    public async Task GetEquipmentTypeIdsByCategory_101Types_FetchesBothPagesAndCombinesWithoutDuplicates()
    {
        var categoryId = Guid.NewGuid();
        var page1Ids = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToList();
        var page2Id = Guid.NewGuid();

        var requestedPages = new List<int>();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var query = req.RequestUri?.Query ?? "";
            int page = query.Contains("page=2") ? 2 : 1;
            requestedPages.Add(page);

            string json;
            if (page == 1)
            {
                json = JsonSerializer.Serialize(new
                {
                    items = page1Ids.Select(id => new { id }),
                    page = 1,
                    pageSize = 100,
                    totalCount = 101,
                    totalPages = 2
                });
            }
            else
            {
                json = JsonSerializer.Serialize(new
                {
                    items = new[] { new { id = page2Id } },
                    page = 2,
                    pageSize = 100,
                    totalCount = 101,
                    totalPages = 2
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetEquipmentTypeIdsByCategoryAsync(categoryId);

        Assert.True(result.IsSuccess);
        Assert.Equal([1, 2], requestedPages);
        Assert.Equal(101, result.Value.Value.Count);
        Assert.Contains(page2Id, result.Value.Value);
    }

    [Fact]
    public async Task GetEquipmentTypeIdsByCategory_Page2Error_FailsEntireCall_DoesNotReturnPartialData()
    {
        var categoryId = Guid.NewGuid();
        var page1Ids = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToList();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var query = req.RequestUri?.Query ?? "";
            if (query.Contains("page=2"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            var json = JsonSerializer.Serialize(new
            {
                items = page1Ids.Select(id => new { id }),
                page = 1,
                pageSize = 100,
                totalCount = 101,
                totalPages = 2
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetEquipmentTypeIdsByCategoryAsync(categoryId);

        // Entire call fails with 503, partial data is NOT returned
        Assert.True(result.IsFailure);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task GetEquipmentTypeIdsByCategory_InvalidJson_Returns503()
    {
        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ invalid json content", System.Text.Encoding.UTF8, "application/json")
            }));

        var result = await client.GetEquipmentTypeIdsByCategoryAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task GetEquipmentTypeIdsByCategory_MissingIdInItem_Returns503()
    {
        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                items = new[] { new { name = "No Id Field" } },
                page = 1,
                pageSize = 100,
                totalCount = 1,
                totalPages = 1
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetEquipmentTypeIdsByCategoryAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Theory]
    [InlineData("{\"items\":[],\"page\":\"1\",\"pageSize\":100,\"totalCount\":0,\"totalPages\":0}")]
    [InlineData("{\"items\":[],\"page\":1,\"pageSize\":100,\"totalCount\":1,\"totalPages\":0}")]
    [InlineData("{\"items\":[{\"id\":123}],\"page\":1,\"pageSize\":100,\"totalCount\":1,\"totalPages\":1}")]
    [InlineData("{\"items\":[],\"page\":1,\"pageSize\":100,\"totalCount\":101,\"totalPages\":2}")]
    public async Task CategoryPagination_InvalidMetadata_FailsClosed(string body)
    {
        var (client, _) = CreateTestClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") }));
        var result = await client.GetEquipmentTypeIdsByCategoryAsync(Guid.NewGuid());
        Assert.True(result.IsFailure);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task CategoryPagination_UsesOneOperationDeadline_WithoutReturningPartialData()
    {
        var (client, options) = CreateTestClient(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        options.TimeoutSeconds = 1;
        var result = await client.GetEquipmentTypeIdsByCategoryAsync(Guid.NewGuid());
        Assert.True(result.IsFailure);
        Assert.Equal("CATALOG_SERVICE_TIMEOUT", result.Error.Code);
    }
}
