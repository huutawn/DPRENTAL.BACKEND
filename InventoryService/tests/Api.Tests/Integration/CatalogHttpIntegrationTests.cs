using System.Net;
using System.Net.Sockets;
using System.Text;
using Dprental.InventoryService.Domain.Clients.Catalog;
using Dprental.InventoryService.Domain.Clients.Media;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class CatalogHttpIntegrationTests : IClassFixture<PostgresFixture>
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

    public sealed record CapturedHttpRequest(string Method, string PathAndQuery, string Body, string? Authorization = null);
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

                            string? authHeader = context.Request.Headers["Authorization"];

                            lock (CapturedRequests)
                            {
                                CapturedRequests.Add(new CapturedHttpRequest(
                                    context.Request.HttpMethod,
                                    context.Request.RawUrl ?? string.Empty,
                                    body,
                                    authHeader));
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
                services.Configure<CatalogServiceOptions>(options =>
                {
                    options.BaseUrl = catalogBaseUrl;
                    options.TimeoutSeconds = catalogTimeoutSeconds;
                });

                services.RemoveAll<IMediaServiceClient>();
                services.AddSingleton<IMediaServiceClient>(mediaClient);
            });
        });

        return (app, secFactory, mediaClient);
    }
}
