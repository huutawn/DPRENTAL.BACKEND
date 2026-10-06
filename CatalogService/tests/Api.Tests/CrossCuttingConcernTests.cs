using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.CatalogService.Api.Observability;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class CrossCuttingConcernTests
{
    private const string CategoriesUrl = "/api/v1/catalog/categories";
    private const string EquipmentTypesUrl = "/api/v1/catalog/equipment-types";

    [Fact]
    public async Task ModelBindingError_Returns400_WithStableCode_AndFieldErrors()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync($"{EquipmentTypesUrl}?page=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(CatalogErrorCode.Catalog.InvalidRequest, problem.GetProperty("title").GetString());
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.True(problem.GetProperty("errors").EnumerateObject().Any());
    }

    [Fact]
    public async Task MalformedJsonBody_Returns400_WithStableCode()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = CreateAdminClient(factory);
        using StringContent body = new("{ not json", Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync(CategoriesUrl, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(CatalogErrorCode.Catalog.InvalidRequest, problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task InvalidToken_Returns401_WithStableCode_WithoutLeakingValidationDetails()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestJwtHelper.GenerateToken(sub: Guid.NewGuid().ToString(), sid: Guid.NewGuid().ToString(), role: "adm", issuer: "wrong-issuer"));

        using HttpResponseMessage response = await client.PostAsJsonAsync(CategoriesUrl, new { name = "Camera" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        string content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("IDX", content, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-issuer", content, StringComparison.Ordinal);
        using JsonDocument problem = JsonDocument.Parse(content);
        Assert.Equal(CatalogErrorCode.Catalog.Unauthorized, problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(CatalogErrors.Catalog.Unauthorized.Message, problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task NonAdminToken_Returns403_WithStableCode()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = CreateClientWithRole(factory, "cus");

        using HttpResponseMessage response = await client.PostAsJsonAsync(CategoriesUrl, new { name = "Camera" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(CatalogErrorCode.Catalog.Forbidden, problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Category_NotFound_Returns404ProblemDetails_WithStableCode_AndCorrelationId()
    {
        const string correlationId = "gateway-req-42";
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, $"{CategoriesUrl}/{Guid.NewGuid()}");
        request.Headers.Add(CorrelationIdProvider.HeaderName, correlationId);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(CatalogErrorCode.Category.NotFound, problem.GetProperty("title").GetString());
        Assert.Equal(correlationId, problem.GetProperty(CorrelationIdMiddleware.ProblemDetailsKey).GetString());
    }

    [Fact]
    public async Task Category_InactiveIsReportedAsNotFound()
    {
        FakeCategoryRepository repository = new();
        Category inactive = new() { Id = Guid.NewGuid(), Name = "Old", Slug = "old", IsActive = false };
        await repository.AddAsync(inactive);
        using WebApplicationFactory<Program> factory = CreateFactory(repository);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync($"{CategoriesUrl}/{inactive.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("", CatalogErrorCode.Category.InvalidName)]
    [InlineData("   ", CatalogErrorCode.Category.InvalidName)]
    public async Task Category_Create_WithInvalidName_Returns400_WithStableCode(string name, string expectedCode)
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = CreateAdminClient(factory);

        using HttpResponseMessage response = await client.PostAsJsonAsync(CategoriesUrl, new { name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedCode, problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Category_Create_WithTooLongDescription_Returns400_WithStableCode()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = CreateAdminClient(factory);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            CategoriesUrl,
            new { name = "Camera", description = new string('x', 1001) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(CatalogErrorCode.Category.InvalidDescription, problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Category_Update_Unknown_Returns404_WithStableCode()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = CreateAdminClient(factory);

        using HttpResponseMessage response = await client.PatchAsJsonAsync($"{CategoriesUrl}/{Guid.NewGuid()}", new { name = "Camera" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(CatalogErrorCode.Category.NotFound, problem.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("line\nbreak")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65 ký tự
    public void CorrelationId_RejectsUnsafeOrOversizedValues(string value)
    {
        Assert.False(CorrelationIdMiddleware.IsValid(value));
    }

    [Fact]
    public async Task UnsafeCorrelationIdHeader_IsReplacedByGeneratedId()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, CategoriesUrl);
        request.Headers.TryAddWithoutValidation(CorrelationIdProvider.HeaderName, "bad value; drop table");

        using HttpResponseMessage response = await client.SendAsync(request);

        string echoed = Assert.Single(response.Headers.GetValues(CorrelationIdProvider.HeaderName));
        Assert.True(Guid.TryParse(echoed, out _));
    }

    [Fact]
    public async Task WriteOperation_LogsResult_InsideCorrelationIdScope_WithoutAuthorizationHeader()
    {
        const string correlationId = "gateway-req-77";
        CapturingLoggerProvider logs = new();
        using WebApplicationFactory<Program> factory = CreateFactory(logs: logs);
        string token = AdminToken();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, CategoriesUrl)
        {
            Content = JsonContent.Create(new { name = "Camera" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(CorrelationIdProvider.HeaderName, correlationId);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        CapturedLog entry = Assert.Single(logs.Entries, e => e.Message.StartsWith("Category Create succeeded", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(entry.Scopes, scope => scope.Key == "CorrelationId" && (string?)scope.Value == correlationId);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains(token, StringComparison.Ordinal));
        Assert.DoesNotContain(
            logs.Entries.SelectMany(e => e.Scopes),
            scope => scope.Value?.ToString()?.Contains(token, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task FailedWriteOperation_LogsErrorCode()
    {
        CapturingLoggerProvider logs = new();
        using WebApplicationFactory<Program> factory = CreateFactory(logs: logs);
        using HttpClient client = CreateAdminClient(factory);

        using HttpResponseMessage response = await client.PostAsJsonAsync(CategoriesUrl, new { name = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        CapturedLog entry = Assert.Single(logs.Entries, e => e.Message.StartsWith("Category Create failed", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(CatalogErrorCode.Category.InvalidName, entry.Message, StringComparison.Ordinal);
    }

    private static string AdminToken() =>
        TestJwtHelper.GenerateToken(sub: Guid.NewGuid().ToString(), sid: Guid.NewGuid().ToString(), role: "adm");

    private static HttpClient CreateAdminClient(WebApplicationFactory<Program> factory) => CreateClientWithRole(factory, "adm");

    private static HttpClient CreateClientWithRole(WebApplicationFactory<Program> factory, string role)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestJwtHelper.GenerateToken(sub: Guid.NewGuid().ToString(), sid: Guid.NewGuid().ToString(), role: role));
        return client;
    }

    private static WebApplicationFactory<Program> CreateFactory(
        FakeCategoryRepository? categories = null,
        CapturingLoggerProvider? logs = null) =>
        PublicCatalogEndpointTests.CreateFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ICategoryRepository>();
                    services.AddSingleton<ICategoryRepository>(categories ?? new FakeCategoryRepository());
                    if (logs is not null)
                    {
                        services.AddSingleton<ILoggerProvider>(logs);
                    }
                });
            });

    private sealed class FakeCategoryRepository : ICategoryRepository
    {
        private readonly ConcurrentDictionary<Guid, Category> _items = new();

        public Task<PagedResult<Category>> SearchAsync(CategorySearchQuery query, CancellationToken cancellationToken = default)
        {
            var filtered = _items.Values.AsEnumerable();
            if (query.IsActive.HasValue)
            {
                filtered = filtered.Where(c => c.IsActive == query.IsActive.Value);
            }
            if (!string.IsNullOrWhiteSpace(query.Keyword))
            {
                var kw = query.Keyword.Trim();
                filtered = filtered.Where(c =>
                    c.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                    c.Slug.Contains(kw, StringComparison.OrdinalIgnoreCase));
            }
            var list = filtered.ToList();
            var items = list.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList();
            return Task.FromResult(new PagedResult<Category>(items, query.Page, query.PageSize, list.Count));
        }

        public Task<Category?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.GetValueOrDefault(id));

        public Task<bool> IsSlugExistsAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.Values.Any(c => c.Slug == slug));

        public Task AddAsync(Category category, CancellationToken cancellationToken = default)
        {
            _items[category.Id] = category;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Category category, CancellationToken cancellationToken = default)
        {
            _items[category.Id] = category;
            return Task.CompletedTask;
        }

        public Task<IEnumerable<Category>> GetAllActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.Values.Where(c => c.IsActive));
    }

    private sealed record CapturedLog(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Scopes);

    private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public ConcurrentQueue<CapturedLog> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => provider._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                List<KeyValuePair<string, object?>> scopes = [];
                provider._scopes.ForEachScope(
                    (scope, list) =>
                    {
                        if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
                        {
                            list.AddRange(pairs.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)));
                        }
                    },
                    scopes);

                provider.Entries.Enqueue(new CapturedLog(logLevel, formatter(state, exception), scopes));
            }
        }
    }
}
