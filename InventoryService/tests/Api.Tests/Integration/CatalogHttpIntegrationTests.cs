using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Api.Clients;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class CatalogHttpIntegrationTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgresFixture _fixture;

    public CatalogHttpIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    private sealed class TrackingMediaClient : IMediaServiceClient
    {
        public int VerifyCalls { get; private set; }
        public int SetPublicCalls { get; private set; }

        public Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            VerifyCalls++;
            return Task.FromResult(Result.Success(new MediaVerificationInfo(mediaId, "Ready", Guid.NewGuid().ToString("D"))));
        }

        public Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            SetPublicCalls++;
            return Task.FromResult(Result.Success(new MediaVisibilityInfo(mediaId, "Public")));
        }
    }

    public sealed record CapturedHttpRequest(string Method, string PathAndQuery, string Body);
    public sealed record MockHttpResponse(int StatusCode, string? Body = null, string ContentType = "application/json", int DelayMs = 0);

    public sealed class LocalCatalogServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private Task? _listenTask;

        public string BaseUrl { get; }
        public List<CapturedHttpRequest> CapturedRequests { get; } = [];
        public Func<HttpListenerRequest, Task<MockHttpResponse>> Handler { get; set; } = _ => Task.FromResult(new MockHttpResponse(200, "{}"));

        public LocalCatalogServer()
        {
            using var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _listenTask = Task.Run(ListenLoopAsync);
        }

        private async Task ListenLoopAsync()
        {
            while (!_cts.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string body = string.Empty;
                            if (context.Request.HasEntityBody)
                            {
                                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                                body = await reader.ReadToEndAsync();
                            }

                            lock (CapturedRequests)
                            {
                                CapturedRequests.Add(new CapturedHttpRequest(
                                    context.Request.HttpMethod,
                                    context.Request.RawUrl ?? string.Empty,
                                    body));
                            }

                            var responseDef = await Handler(context.Request);
                            if (responseDef.DelayMs > 0)
                            {
                                await Task.Delay(responseDef.DelayMs);
                            }

                            context.Response.StatusCode = responseDef.StatusCode;
                            if (!string.IsNullOrEmpty(responseDef.ContentType))
                            {
                                context.Response.ContentType = responseDef.ContentType;
                            }

                            if (!string.IsNullOrEmpty(responseDef.Body))
                            {
                                byte[] buffer = Encoding.UTF8.GetBytes(responseDef.Body);
                                context.Response.ContentLength64 = buffer.Length;
                                await context.Response.OutputStream.WriteAsync(buffer);
                            }
                            else
                            {
                                context.Response.ContentLength64 = 0;
                            }
                        }
                        catch
                        {
                            // Ignored: client disconnected or test disposed
                        }
                        finally
                        {
                            try { context.Response.Close(); } catch { }
                        }
                    });
                }
                catch when (_cts.IsCancellationRequested || !_listener.IsListening)
                {
                    break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            if (_listenTask is not null)
            {
                try { await _listenTask; } catch { }
            }
            _cts.Dispose();
        }
    }

    private (WebApplicationFactory<Program> App, InventorySecurityTestFactory SecFactory, TrackingMediaClient MediaClient) CreateFactory(
        string catalogBaseUrl,
        int catalogTimeoutSeconds = 10)
    {
        var mediaClient = new TrackingMediaClient();
        var secFactory = new InventorySecurityTestFactory(_fixture.ConnectionString, new TestTimeProvider(FixedNow));
        var app = secFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                // Giữ nguyên typed CatalogServiceClient từ DI, cấu hình URL trỏ tới local test server
                services.Configure<CatalogServiceOptions>(options =>
                {
                    options.BaseUrl = catalogBaseUrl;
                    options.TimeoutSeconds = catalogTimeoutSeconds;
                });

                // Cung cấp test double cho MediaService để kiểm tra không bị gọi khi validation thất bại
                services.RemoveAll<IMediaServiceClient>();
                services.AddSingleton<IMediaServiceClient>(mediaClient);
            });
        });

        return (app, secFactory, mediaClient);
    }

    [Fact]
    public async Task CreateAsset_EndToEnd_RealHttpClient_Success201_VerifiesUriBodyAndPersists()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        // Cấu hình mock responses từ CatalogServer
        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (req.HttpMethod == "GET" && path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-FX3",
                    name = "Sony FX3 Cinema Line",
                    status = "Published",
                    createdAt = "2026-09-01T00:00:00Z"
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            if (req.HttpMethod == "GET" && path.Contains($"brands/{brandId:D}"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = brandId,
                    code = "BR-SONY",
                    name = "Sony",
                    status = "Active"
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            if (req.HttpMethod == "POST" && path.Contains($"equipment-types/{eqTypeId:D}/validate-specification"))
            {
                return Task.FromResult(new MockHttpResponse(204));
            }

            return Task.FromResult(new MockHttpResponse(404, "Not Found"));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            model = "Sony FX3 Cinema Line",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame", resolution = "4K" },
            serialNumber = "SN-12345",
            manufactureYear = 2024
        };

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(created);
        Assert.Equal(brandId, created.BrandId);
        Assert.Equal(eqTypeId, created.EquipmentTypeId);
        Assert.Equal("Sony FX3 Cinema Line", created.Model);

        // 1. Kiểm tra chính xác URI và body gửi tới CatalogServer
        lock (server.CapturedRequests)
        {
            Assert.Equal(3, server.CapturedRequests.Count);

            // Request 1: GET equipment-type
            var req1 = server.CapturedRequests[0];
            Assert.Equal("GET", req1.Method);
            Assert.Equal($"/api/v1/catalog/equipment-types/{eqTypeId:D}", req1.PathAndQuery);

            // Request 2: GET brand
            var req2 = server.CapturedRequests[1];
            Assert.Equal("GET", req2.Method);
            Assert.Equal($"/api/v1/catalog/brands/{brandId:D}", req2.PathAndQuery);

            // Request 3: POST validate-specification
            var req3 = server.CapturedRequests[2];
            Assert.Equal("POST", req3.Method);
            Assert.Equal($"/api/v1/catalog/equipment-types/{eqTypeId:D}/validate-specification", req3.PathAndQuery);

            using var specDoc = JsonDocument.Parse(req3.Body);
            Assert.Equal(1, specDoc.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("Full-Frame", specDoc.RootElement.GetProperty("specValues").GetProperty("sensor").GetString());
            Assert.Equal("4K", specDoc.RootElement.GetProperty("specValues").GetProperty("resolution").GetString());
        }

        // 2. Kiểm tra dữ liệu được persist vào PostgreSQL
        await using var db = CreateDbContext();
        var dbAsset = await db.Assets.SingleAsync(a => a.Id == created.Id);
        Assert.Equal(brandId, dbAsset.BrandId);
        Assert.Equal(1, dbAsset.SchemaVersion);
        Assert.Contains("\"Full-Frame\"", dbAsset.SpecValues);
    }

    [Fact]
    public async Task PatchAsset_EndToEnd_RealHttpClient_Success200_VerifiesUriBodyAndPersists()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, _) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var oldBrandId = Guid.NewGuid();
        var newBrandId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        // Seed asset ban đầu trong PostgreSQL
        await using (var db = CreateDbContext())
        {
            var asset = InventoryAsset.Create(
                assetId, ownerId, eqTypeId, oldBrandId,
                "Old Camera", 1, "{\"lens\":\"24-70\"}", FixedNow).Value;
            db.Assets.Add(asset);
            await db.SaveChangesAsync();
        }

        // Cấu hình mock responses
        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (req.HttpMethod == "GET" && path.Contains($"brands/{newBrandId:D}"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = newBrandId,
                    code = "BR-CANON",
                    name = "Canon",
                    status = "Active"
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            if (req.HttpMethod == "POST" && path.Contains($"equipment-types/{eqTypeId:D}/validate-specification"))
            {
                return Task.FromResult(new MockHttpResponse(204));
            }

            return Task.FromResult(new MockHttpResponse(404, "Not Found"));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var patch = new
        {
            brandId = newBrandId,
            schemaVersion = 2,
            specValues = new { lens = "70-200" }
        };

        var response = await client.PatchAsync($"api/v1/inventory/assets/{assetId:D}",
            new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(updated);
        Assert.Equal(newBrandId, updated.BrandId);
        Assert.Equal(2, updated.SchemaVersion);

        // Kiểm tra request gửi tới server
        lock (server.CapturedRequests)
        {
            Assert.Equal(2, server.CapturedRequests.Count);
            Assert.Contains(server.CapturedRequests, r => r.Method == "GET" && r.PathAndQuery.Contains($"brands/{newBrandId:D}"));
            Assert.Contains(server.CapturedRequests, r => r.Method == "POST" && r.PathAndQuery.Contains("validate-specification"));
        }

        // Kiểm tra persist trong PostgreSQL
        await using var verifyDb = CreateDbContext();
        var reloaded = await verifyDb.Assets.SingleAsync(a => a.Id == assetId);
        Assert.Equal(newBrandId, reloaded.BrandId);
        Assert.Equal(2, reloaded.SchemaVersion);
        Assert.Contains("70-200", reloaded.SpecValues);
    }

    [Fact]
    public async Task CreateAsset_BrandMissing404_Returns400_DoesNotWriteAsset_DoesNotCallMedia()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-FX3",
                    name = "Sony FX3",
                    status = "Published"
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            if (path.Contains($"brands/{brandId:D}"))
            {
                var problem = JsonSerializer.Serialize(new
                {
                    title = "Brand.NotFound",
                    detail = "Thương hiệu không tồn tại trong Catalog.",
                    status = 404
                });
                return Task.FromResult(new MockHttpResponse(404, problem, "application/problem+json"));
            }

            return Task.FromResult(new MockHttpResponse(404, "Not Found"));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            model = "Sony FX3",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            thumbnailMediaId = Guid.NewGuid()
        };

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problemResp = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problemResp);
        Assert.Equal("Brand.NotFound", problemResp.Title);

        // Xác nhận MediaService KHÔNG được gọi
        Assert.Equal(0, mediaClient.VerifyCalls);

        // Xác nhận không có Asset nào được tạo trong DB
        await using var db = CreateDbContext();
        Assert.Null(await db.Assets.SingleOrDefaultAsync(a => a.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task CreateAsset_BrandInactive200_Returns400_DoesNotWriteAsset_DoesNotCallMedia()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-FX3",
                    name = "Sony FX3",
                    status = "Published"
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            if (path.Contains($"brands/{brandId:D}"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = brandId,
                    code = "BR-OLD",
                    name = "Old Brand",
                    status = "Inactive"
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            return Task.FromResult(new MockHttpResponse(404));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            model = "Sony FX3",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            thumbnailMediaId = Guid.NewGuid()
        };

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problemResp = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problemResp);
        Assert.Equal("Brand.Inactive", problemResp.Title);

        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.Assets.SingleOrDefaultAsync(a => a.OwnerUserId == ownerId));
    }

    [Theory]
    [InlineData("EquipmentType.InvalidAssetSchemaVersion", "Schema version 3 chưa được publish cho loại thiết bị.")]
    [InlineData("EquipmentType.SpecRequired", "Trường 'sensor' là bắt buộc.")]
    [InlineData("EquipmentType.SpecDatatype", "Trường 'resolution' phải là chuỗi ký tự.")]
    [InlineData("EquipmentType.SpecRange", "Giá trị 'fps' phải từ 24 đến 240.")]
    [InlineData("EquipmentType.SpecEnum", "Giá trị 'mount' phải thuộc danh sách hỗ trợ.")]
    public async Task CreateAsset_FiveSpecificationErrorCodes_Returns400_PreservesTitleAndDetail_DoesNotWriteAsset(
        string errorCode,
        string errorDetail)
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains($"equipment-types/{eqTypeId:D}/validate-specification"))
            {
                var problem = JsonSerializer.Serialize(new
                {
                    title = errorCode,
                    detail = errorDetail,
                    status = 400
                });
                return Task.FromResult(new MockHttpResponse(400, problem, "application/problem+json"));
            }

            if (path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-01",
                    name = "Type",
                    status = "Published"
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            if (path.Contains($"brands/{brandId:D}"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = brandId,
                    code = "BR-01",
                    name = "Brand",
                    status = "Active"
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            return Task.FromResult(new MockHttpResponse(404));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            model = "Sony FX3",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            thumbnailMediaId = Guid.NewGuid()
        };

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(errorCode, problem.Title);
        Assert.Equal(errorDetail, problem.Detail);

        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.Assets.SingleOrDefaultAsync(a => a.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task CreateAsset_MalformedResponse_Returns503_DoesNotWriteAsset()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                // Trả về JSON hỏng
                return Task.FromResult(new MockHttpResponse(200, "malformed-not-json-content"));
            }

            return Task.FromResult(new MockHttpResponse(404));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            model = "Sony FX3",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" }
        };

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.Assets.SingleOrDefaultAsync(a => a.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task CreateAsset_EndpointMissing404_Returns503_DoesNotWriteAsset()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        // Server trả về 404 cho endpoint brand (không có ProblemDetails Brand.NotFound)
        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-01",
                    name = "Type",
                    status = "Published"
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            // Endpoint brands không tồn tại
            return Task.FromResult(new MockHttpResponse(404, string.Empty));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            model = "Sony FX3",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" }
        };

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.Assets.SingleOrDefaultAsync(a => a.OwnerUserId == ownerId));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    [InlineData(501)] // NotSupported / Not Implemented
    [InlineData(502)]
    [InlineData(503)]
    public async Task CreateAsset_UpstreamErrorCodes_Returns503_DoesNotWriteAsset(int upstreamStatus)
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        server.Handler = req =>
        {
            return Task.FromResult(new MockHttpResponse(upstreamStatus));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            model = "Sony FX3",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" }
        };

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.Assets.SingleOrDefaultAsync(a => a.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task CreateAsset_RealTimeout_Returns503_DoesNotWriteAsset()
    {
        await using var server = new LocalCatalogServer();
        // Cấu hình client timeout ngắn (1s), server delay 2.5s để tạo timeout thực tế
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl, catalogTimeoutSeconds: 1);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        server.Handler = req =>
        {
            return Task.FromResult(new MockHttpResponse(200, "{}", DelayMs: 2500));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            model = "Sony FX3",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" }
        };

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("CATALOG_SERVICE_TIMEOUT", body);

        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.Assets.SingleOrDefaultAsync(a => a.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task CreateAsset_ClientCancellation_DoesNotWriteAsset()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, _) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Hủy yêu cầu ngay lập tức

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            model = "Sony FX3",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.PostAsync("api/v1/inventory/assets",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                cts.Token));

        await using var db = CreateDbContext();
        Assert.Null(await db.Assets.SingleOrDefaultAsync(a => a.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task PatchAsset_ModelOnly_DoesNotSendCatalogRequests()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, _) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        // Seed asset trong PostgreSQL
        await using (var db = CreateDbContext())
        {
            var asset = InventoryAsset.Create(
                assetId, ownerId, Guid.NewGuid(), brandId,
                "Model Before", 1, "{}", FixedNow).Value;
            db.Assets.Add(asset);
            await db.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var patch = new
        {
            model = "Model After"
        };

        var response = await client.PatchAsync($"api/v1/inventory/assets/{assetId:D}",
            new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Server hoàn toàn KHÔNG nhận request nào từ Inventory!
        lock (server.CapturedRequests)
        {
            Assert.Empty(server.CapturedRequests);
        }

        await using var verifyDb = CreateDbContext();
        var reloaded = await verifyDb.Assets.SingleAsync(a => a.Id == assetId);
        Assert.Equal("Model After", reloaded.Model);
    }
}
