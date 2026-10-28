extern alias RentalApi;

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.IServices.Payments;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;
using static Dprental.Gateway.Tests.DownstreamGatewayTestHost;
using RentalProgram = RentalApi::Program;

namespace Dprental.Gateway.Tests;

public sealed class RentalRealtimeGatewayEndToEndTests
{
    private const string Issuer = "https://auth.example.test";
    private const string Audience = "dprental-clients";
    private const string InternalApiKey = "dev-internal-api-key-dprental-2026";
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<(string ConnectionString, string DatabaseName)> CreatePostgreSqlDatabaseAsync()
    {
        string dbName = $"gateway_rental_e2e_{Guid.NewGuid():N}";
        string baseConn = "Host=127.0.0.1;Port=5433;Database=postgres;Username=inventoryservice;Password=inventory_test_secret_123!";

        await using var masterConn = new NpgsqlConnection(baseConn);
        try
        {
            await masterConn.OpenAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Cannot connect to PostgreSQL at {baseConn}: {ex.Message}. Integration environment required.", ex);
        }

        await using var createCmd = masterConn.CreateCommand();
        createCmd.CommandText = $"CREATE DATABASE {dbName};";
        await createCmd.ExecuteNonQueryAsync();

        string connString = $"Host=127.0.0.1;Port=5433;Database={dbName};Username=inventoryservice;Password=inventory_test_secret_123!";
        return (connString, dbName);
    }

    private static async Task DropPostgreSqlDatabaseAsync(string dbName)
    {
        string baseConn = "Host=127.0.0.1;Port=5433;Database=postgres;Username=inventoryservice;Password=inventory_test_secret_123!";
        await using var masterConn = new NpgsqlConnection(baseConn);
        await masterConn.OpenAsync();

        await using var dropCmd = masterConn.CreateCommand();
        dropCmd.CommandText = $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE);";
        await dropCmd.ExecuteNonQueryAsync();
    }

    public sealed class KestrelRentalServer : WebApplicationFactory<RentalProgram>
    {
        private readonly string _connectionString;
        private readonly RSA _rsa;
        private readonly string _keyId;

        public string ServerAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');
        public IServiceProvider RentalServices => Services;

        public KestrelRentalServer(string connectionString, RSA rsa, string keyId)
        {
            _connectionString = connectionString;
            _rsa = rsa;
            _keyId = keyId;
            UseKestrel(0);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Postgres", _connectionString);
            builder.UseSetting("InternalAuthentication:ApiKey", InternalApiKey);
            builder.UseSetting("Authentication:Jwt:Issuer", Issuer);
            builder.UseSetting("Authentication:Jwt:Audience", Audience);
            builder.UseSetting("Authentication:Jwt:JwksUri", "http://localhost/test-jwks");
            builder.UseSetting("OrderHub:AllowedOrigins:0", "http://localhost:3000");

            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.TokenValidationParameters.ValidIssuer = Issuer;
                    options.TokenValidationParameters.ValidAudience = Audience;
                    options.ConfigurationManager = new TestJwksConfigManager(_rsa, _keyId);
                });
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", InternalApiKey);
        }

        private sealed class TestJwksConfigManager(RSA rsa, string keyId) : IConfigurationManager<OpenIdConnectConfiguration>
        {
            private readonly OpenIdConnectConfiguration _config = Create(rsa, keyId);
            public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) => Task.FromResult(_config);
            public void RequestRefresh() { }
            private static OpenIdConnectConfiguration Create(RSA rsa, string keyId)
            {
                var c = new OpenIdConnectConfiguration();
                c.SigningKeys.Add(new RsaSecurityKey(rsa) { KeyId = keyId });
                return c;
            }
        }
    }

    public sealed class KestrelGatewayServer : WebApplicationFactory<Program>
    {
        private readonly Uri _rentalAddress;
        private readonly string _jwksUri;

        public string ServerAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');

        public KestrelGatewayServer(Uri rentalAddress, string jwksUri)
        {
            _rentalAddress = rentalAddress;
            _jwksUri = jwksUri;
            UseKestrel(0);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                var config = configBuilder.Build();
                Dictionary<string, string?> overrides = new()
                {
                    ["Gateway:Authentication:Jwt:Issuer"] = Issuer,
                    ["Gateway:Authentication:Jwt:Audience"] = Audience,
                    ["Gateway:Authentication:Jwt:InventoryAudience"] = "inventory-api",
                    ["Gateway:Authentication:Jwt:JwksUri"] = _jwksUri,
                    ["InternalAuthentication:ApiKey"] = InternalApiKey,
                    ["Gateway:Security:Cors:AllowedOrigins:0"] = "http://localhost:3000"
                };

                foreach (var route in config.GetSection("Routes").GetChildren())
                {
                    string existingScheme = route["DownstreamScheme"] ?? "http";
                    // Retain downstream scheme ws for order hub websocket!
                    overrides[$"{route.Path}:DownstreamScheme"] = string.Equals(existingScheme, "ws", StringComparison.OrdinalIgnoreCase)
                        ? "ws"
                        : _rentalAddress.Scheme;
                    overrides[$"{route.Path}:DownstreamHostAndPorts:0:Host"] = _rentalAddress.Host;
                    overrides[$"{route.Path}:DownstreamHostAndPorts:0:Port"] = _rentalAddress.Port.ToString(CultureInfo.InvariantCulture);
                }
                configBuilder.AddInMemoryCollection(overrides);
            });
        }
    }

    private static string CreateJwtToken(RSA signingKey, string keyId, Guid userId, bool expired = false)
    {
        var securityKey = new RsaSecurityKey(signingKey) { KeyId = keyId };
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: [new Claim("sub", userId.ToString()), new Claim("r", "Member"), new Claim("sid", Guid.NewGuid().ToString("D"))],
            expires: expired ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(30),
            notBefore: expired ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddSeconds(-5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HubConnection CreateGatewayHubConnection(string gatewayBaseUrl, string? token, string? origin = "http://localhost:3000")
    {
        var hubUrl = $"{gatewayBaseUrl}/api/v1/rental/hubs/orders";
        var builder = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    options.Headers["Cookie"] = $"accessToken={token}; refreshToken=secret-refresh-token";
                }
                if (!string.IsNullOrWhiteSpace(origin))
                {
                    options.Headers["Origin"] = origin;
                }
                // NOTICE: SignalR client DOES NOT supply X-Api-Key! Gateway proxy injects it!
            });

        return builder.Build();
    }

    private static RentalOrder SeedOrder(RentalServiceDbContext db, Guid orderId, Guid renterId, Guid supplierA, Guid supplierB)
    {
        var itemA = new InitialRentalItemDto(
            Guid.NewGuid(), Guid.NewGuid(), supplierA, 1,
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5),
            "Máy xúc A", "Desc", "Kho 1", 1_000_000m, 5_000_000m,
            "grp-A", [new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
            new RentalItemListingSnapshotDto(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Máy xúc", Guid.Parse("22222222-2222-2222-2222-222222222222"), "Cat", "320D", 1, "{}", null));

        var itemB = new InitialRentalItemDto(
            Guid.NewGuid(), Guid.NewGuid(), supplierB, 1,
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5),
            "Máy ủi B", "Desc", "Kho 2", 1_000_000m, 5_000_000m,
            "grp-B", [new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
            new RentalItemListingSnapshotDto(Guid.Parse("33333333-3333-3333-3333-333333333333"), "Máy ủi", Guid.Parse("44444444-4444-4444-4444-444444444444"), "Komatsu", "D65", 1, "{}", null));

        var order = RentalOrder.Create(
            orderId, "DH-GW-E2E-01", renterId, "VND", "Pickup",
            "Trần Thị B", "0901234567", "Hà Nội", null,
            "v1.0", "terms", "hash", "op-gw-create", FixedNow,
            [itemA, itemB]).Value;

        order.AttachInvoice("INV-GW-01", FixedNow);
        db.RentalOrders.Add(order);
        return order;
    }

    [Fact]
    public async Task EndToEndNegotiateUpgradeHandshakeMutationEventAndRestResync()
    {
        var (connString, dbName) = await CreatePostgreSqlDatabaseAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<RentalServiceDbContext>().UseNpgsql(connString).Options;
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid supplierA = Guid.NewGuid();
            Guid supplierB = Guid.NewGuid();

            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                SeedOrder(seedDb, orderId, renterId, supplierA, supplierB);
                await seedDb.SaveChangesAsync();
            }

            using var rsa = RSA.Create(2048);
            string keyId = "gw-rental-key-01";
            await using var jwksUpstream = new LoopbackTestService(CreateJwks(rsa, keyId));

            // Start Rental Kestrel
            await using var rentalServer = new KestrelRentalServer(connString, rsa, keyId);
            _ = rentalServer.CreateClient();

            await using var gatewayServer = new KestrelGatewayServer(new Uri(rentalServer.ServerAddress), new Uri(jwksUpstream.BaseAddress, "/.well-known/jwks.json").ToString());
            _ = gatewayServer.CreateClient();

            string renterToken = CreateJwtToken(rsa, keyId, renterId);
            string supplierAToken = CreateJwtToken(rsa, keyId, supplierA);

            // Connect SignalR clients through GATEWAY public URL ONLY:
            var renterConn = CreateGatewayHubConnection(gatewayServer.ServerAddress, renterToken);
            var supplierConn = CreateGatewayHubConnection(gatewayServer.ServerAddress, supplierAToken);

            var renterSignal = new TaskCompletionSource<RenterOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            var supplierSignal = new TaskCompletionSource<SupplierSubOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);

            renterConn.On<RenterOrderChangedNotification>("RenterOrderChanged", n => renterSignal.TrySetResult(n));
            supplierConn.On<SupplierSubOrderChangedNotification>("SupplierSubOrderChanged", n => supplierSignal.TrySetResult(n));

            // 1. Negotiate -> Upgrade -> SignalR Handshake over Gateway
            await renterConn.StartAsync();
            await supplierConn.StartAsync();

            Assert.Equal(HubConnectionState.Connected, renterConn.State);
            Assert.Equal(HubConnectionState.Connected, supplierConn.State);

            // 2. Perform real mutation on Rental host (outer transaction & commit)
            using (var scope = rentalServer.RentalServices.CreateScope())
            {
                var workflow = scope.ServiceProvider.GetRequiredService<IPaymentWorkflowService>();
                var paidPayload = new InvoicePaidPayload(
                    orderId, renterId, "INV-GW-01", "PAY-GW-01",
                    18_000_000m, "VND", FixedNow);

                var env = new KafkaMessageEnvelope<InvoicePaidPayload>(
                    Guid.NewGuid(), "InvoicePaid", 1, "payment-service",
                    orderId.ToString("D"), FixedNow, "corr-gw-pay", null, paidPayload);

                var res = await workflow.ProcessInvoicePaidAsync(env);
                Assert.True(res.IsSuccess);
            }

            // 3. Receive event via Gateway WebSocket
            var rNote = await renterSignal.Task.WaitAsync(ReceiveTimeout);
            var sNote = await supplierSignal.Task.WaitAsync(ReceiveTimeout);

            Assert.Equal(orderId, rNote.OrderId);
            Assert.Equal(OrderChangeTypes.PaymentConfirmed, rNote.ChangeType);
            Assert.Equal(orderId, sNote.OrderId);
            Assert.Equal(OrderChangeTypes.PaymentConfirmed, sNote.ChangeType);

            // 4. REST resync through Gateway: GET /api/v1/rental/orders/{orderId}
            using var restClient = new HttpClient { BaseAddress = new Uri(gatewayServer.ServerAddress) };
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/rental/orders/{orderId}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", renterToken);
            using var resp = await restClient.SendAsync(req);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            long restVersion = doc.RootElement.GetProperty("version").GetInt64();
            string restStatus = doc.RootElement.GetProperty("status").GetString()!;

            Assert.Equal(rNote.Version, restVersion);
            Assert.Equal("PAID", restStatus);

            await renterConn.StopAsync();
            await supplierConn.StopAsync();
        }
        finally
        {
            await DropPostgreSqlDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task PaidOrderExtendHoldRepliesPersistsReceiptNoSecondPaidEvent()
    {
        var (connString, dbName) = await CreatePostgreSqlDatabaseAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<RentalServiceDbContext>().UseNpgsql(connString).Options;
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid supplierA = Guid.NewGuid();
            Guid supplierB = Guid.NewGuid();
            Guid itemAId;

            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var order = SeedOrder(seedDb, orderId, renterId, supplierA, supplierB);
                itemAId = order.Items.First(i => i.SupplierId == supplierA).Id;
                await seedDb.SaveChangesAsync();
            }

            using var rsa = RSA.Create(2048);
            string keyId = "gw-rental-key-02";
            await using var jwksUpstream = new LoopbackTestService(CreateJwks(rsa, keyId));
            await using var rentalServer = new KestrelRentalServer(connString, rsa, keyId);
            _ = rentalServer.CreateClient();
            await using var gatewayServer = new KestrelGatewayServer(new Uri(rentalServer.ServerAddress), new Uri(jwksUpstream.BaseAddress, "/.well-known/jwks.json").ToString());
            _ = gatewayServer.CreateClient();
            string renterToken = CreateJwtToken(rsa, keyId, renterId);
            var renterConn = CreateGatewayHubConnection(gatewayServer.ServerAddress, renterToken);

            var firstPaidSignal = new TaskCompletionSource<RenterOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondEventSignal = new TaskCompletionSource<RenterOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);

            int eventCount = 0;
            renterConn.On<RenterOrderChangedNotification>("RenterOrderChanged", n =>
            {
                if (n.ChangeType == OrderChangeTypes.PaymentConfirmed)
                {
                    if (Interlocked.Increment(ref eventCount) == 1)
                        firstPaidSignal.TrySetResult(n);
                    else
                        secondEventSignal.TrySetResult(n);
                }
            });

            await renterConn.StartAsync();

            // 1. Pay order
            using (var scope = rentalServer.RentalServices.CreateScope())
            {
                var workflow = scope.ServiceProvider.GetRequiredService<IPaymentWorkflowService>();
                var env = new KafkaMessageEnvelope<InvoicePaidPayload>(
                    Guid.NewGuid(), "InvoicePaid", 1, "payment-service",
                    orderId.ToString("D"), FixedNow, "corr-pay-eh", null,
                    new InvoicePaidPayload(orderId, renterId, "INV-GW-01", "PAY-GW-EH", 18_000_000m, "VND", FixedNow));

                var res = await workflow.ProcessInvoicePaidAsync(env);
                Assert.True(res.IsSuccess);
            }

            await firstPaidSignal.Task.WaitAsync(ReceiveTimeout);

            // 2. Dispatch ExtendHold callback
            string extendOpKey = RentalOperationExecutor.ComputeOperationKey("ExtendHold", $"{orderId}:{itemAId}");
            var body = JsonSerializer.Serialize(new { receiptRef = "rcpt-gw-eh", allocationGroupId = "g-a", newHoldExpiresAt = FixedNow.AddHours(24) });
            var callbackPayload = new { commandType = "ExtendHold", operationKey = extendOpKey, rentalOrderId = orderId, rentalItemId = itemAId, success = true, payload = JsonDocument.Parse(body).RootElement };
            var envCallback = new { messageId = Guid.NewGuid(), messageType = KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, version = 1, producer = KafkaContractSpec.Producers.InventoryService, aggregateId = orderId.ToString("D"), occurredAt = FixedNow.AddSeconds(10), correlationId = "corr-eh-gw", causationId = (Guid?)null, payload = callbackPayload };
            string envCallbackJson = JsonSerializer.Serialize(envCallback, JsonOptions);

            Guid inboxId = Guid.NewGuid();
            await using (var inboxDb = new RentalServiceDbContext(dbOptions))
            {
                var inbox = new RentalInboxMessage(inboxId, envCallback.messageId, KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), "corr-eh-gw", null, envCallbackJson, FixedNow.AddSeconds(10), "fp-eh-gw");
                inboxDb.RentalInboxMessages.Add(inbox);
                await inboxDb.SaveChangesAsync();
            }

            using (var scope = rentalServer.RentalServices.CreateScope())
            {
                var dispatcher = scope.ServiceProvider.GetRequiredService<IPaymentEventsDispatcher>();
                var res = await dispatcher.ProcessInboxMessageAsync(inboxId);
                Assert.True(res.IsSuccess);
            }

            // 3. Verify receipt is Succeeded and NO second Paid notification is emitted!
            await Task.Delay(200);
            Assert.False(secondEventSignal.Task.IsCompleted, "Callback must NOT emit duplicate payment event!");

            await using (var verifyDb = new RentalServiceDbContext(dbOptions))
            {
                var rcpt = await verifyDb.RentalOperationReceipts.SingleAsync(r => r.OperationId == extendOpKey);
                Assert.Equal(RentalOperationReceipt.StatusSucceeded, rcpt.Status);
            }

            await renterConn.StopAsync();
        }
        finally
        {
            await DropPostgreSqlDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task ReleaseUnpaidOrderSupplierReceivesNoNotification()
    {
        var (connString, dbName) = await CreatePostgreSqlDatabaseAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<RentalServiceDbContext>().UseNpgsql(connString).Options;
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid supplierA = Guid.NewGuid();
            Guid supplierB = Guid.NewGuid();
            Guid itemAId;

            string relOpKey;
            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var order = SeedOrder(seedDb, orderId, renterId, supplierA, supplierB);
                itemAId = order.Items.First(i => i.SupplierId == supplierA).Id;
                relOpKey = RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{orderId}:{itemAId}");

                var relReceipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(), orderId, relOpKey, "ReleaseAllocation",
                    RentalOperationExecutor.ComputeFingerprint($"ReleaseAllocation:{order.Items.First().AllocationGroupId}"),
                    FixedNow, itemAId, "{}");
                order.AddOperationReceipt(relReceipt);

                await seedDb.SaveChangesAsync();
            }

            using var rsa = RSA.Create(2048);
            string keyId = "gw-rental-key-03";
            await using var jwksUpstream = new LoopbackTestService(CreateJwks(rsa, keyId));
            await using var rentalServer = new KestrelRentalServer(connString, rsa, keyId);
            _ = rentalServer.CreateClient();
            await using var gatewayServer = new KestrelGatewayServer(new Uri(rentalServer.ServerAddress), new Uri(jwksUpstream.BaseAddress, "/.well-known/jwks.json").ToString());
            _ = gatewayServer.CreateClient();
            string renterToken = CreateJwtToken(rsa, keyId, renterId);
            string supplierToken = CreateJwtToken(rsa, keyId, supplierA);

            var renterConn = CreateGatewayHubConnection(gatewayServer.ServerAddress, renterToken);
            var supplierConn = CreateGatewayHubConnection(gatewayServer.ServerAddress, supplierToken);

            var renterSignal = new TaskCompletionSource<RenterOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            var supplierSignal = new TaskCompletionSource<SupplierSubOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);

            renterConn.On<RenterOrderChangedNotification>("RenterOrderChanged", n => renterSignal.TrySetResult(n));
            supplierConn.On<SupplierSubOrderChangedNotification>("SupplierSubOrderChanged", n => supplierSignal.TrySetResult(n));

            await renterConn.StartAsync();
            await supplierConn.StartAsync();

            // Dispatch Release callback on unpaid order
            var relBody = JsonSerializer.Serialize(new { receiptRef = "rcpt-rel-unpaid" });
            var callbackPayload = new { commandType = "ReleaseAllocation", operationKey = relOpKey, rentalOrderId = orderId, rentalItemId = itemAId, success = true, payload = JsonDocument.Parse(relBody).RootElement };
            var env = new { messageId = Guid.NewGuid(), messageType = KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, version = 1, producer = KafkaContractSpec.Producers.InventoryService, aggregateId = orderId.ToString("D"), occurredAt = FixedNow.AddMinutes(5), correlationId = "corr-rel-gw", causationId = (Guid?)null, payload = callbackPayload };
            string envJson = JsonSerializer.Serialize(env, JsonOptions);

            Guid inboxId = Guid.NewGuid();
            await using (var inboxDb = new RentalServiceDbContext(dbOptions))
            {
                var inbox = new RentalInboxMessage(inboxId, env.messageId, KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), "corr-rel-gw", null, envJson, FixedNow.AddMinutes(5), "fp-rel-gw");
                inboxDb.RentalInboxMessages.Add(inbox);
                await inboxDb.SaveChangesAsync();
            }

            using (var scope = rentalServer.RentalServices.CreateScope())
            {
                var dispatcher = scope.ServiceProvider.GetRequiredService<IPaymentEventsDispatcher>();
                var res = await dispatcher.ProcessInboxMessageAsync(inboxId);
                Assert.True(res.IsSuccess);
            }

            // Renter receives ALLOCATION_CHANGED
            var rNote = await renterSignal.Task.WaitAsync(ReceiveTimeout);
            Assert.Equal(OrderChangeTypes.AllocationChanged, rNote.ChangeType);

            // Supplier receives NOTHING (unpaid order is invisible to supplier)
            await Task.Delay(200);
            Assert.False(supplierSignal.Task.IsCompleted, "Supplier must NOT receive notification for unpaid order release!");

            await renterConn.StopAsync();
            await supplierConn.StopAsync();
        }
        finally
        {
            await DropPostgreSqlDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task LatePaymentRefundRenterReceivesNewVersionUnauthorizedSupplierReceivesNothing()
    {
        var (connString, dbName) = await CreatePostgreSqlDatabaseAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<RentalServiceDbContext>().UseNpgsql(connString).Options;
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid supplierA = Guid.NewGuid();
            Guid supplierB = Guid.NewGuid();
            DateTimeOffset pastCutoffTime;
            long versionBefore;

            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var order = SeedOrder(seedDb, orderId, renterId, supplierA, supplierB);
                versionBefore = order.Version;
                pastCutoffTime = order.PaymentDeadline.AddHours(2);
                await seedDb.SaveChangesAsync();
            }

            using var rsa = RSA.Create(2048);
            string keyId = "gw-rental-key-04";
            await using var jwksUpstream = new LoopbackTestService(CreateJwks(rsa, keyId));
            await using var rentalServer = new KestrelRentalServer(connString, rsa, keyId);
            _ = rentalServer.CreateClient();
            await using var gatewayServer = new KestrelGatewayServer(new Uri(rentalServer.ServerAddress), new Uri(jwksUpstream.BaseAddress, "/.well-known/jwks.json").ToString());
            _ = gatewayServer.CreateClient();
            string renterToken = CreateJwtToken(rsa, keyId, renterId);
            string supplierToken = CreateJwtToken(rsa, keyId, supplierA);

            var renterConn = CreateGatewayHubConnection(gatewayServer.ServerAddress, renterToken);
            var supplierConn = CreateGatewayHubConnection(gatewayServer.ServerAddress, supplierToken);

            var renterSignal = new TaskCompletionSource<RenterOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            var supplierSignal = new TaskCompletionSource<SupplierSubOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);

            renterConn.On<RenterOrderChangedNotification>("RenterOrderChanged", n => renterSignal.TrySetResult(n));
            supplierConn.On<SupplierSubOrderChangedNotification>("SupplierSubOrderChanged", n => supplierSignal.TrySetResult(n));

            await renterConn.StartAsync();
            await supplierConn.StartAsync();

            // Dispatch late payment past cutoff
            using (var scope = rentalServer.RentalServices.CreateScope())
            {
                var workflow = scope.ServiceProvider.GetRequiredService<IPaymentWorkflowService>();
                var env = new KafkaMessageEnvelope<InvoicePaidPayload>(
                    Guid.NewGuid(), "InvoicePaid", 1, "payment-service",
                    orderId.ToString("D"), pastCutoffTime, "corr-late-gw", null,
                    new InvoicePaidPayload(orderId, renterId, "INV-GW-01", "PAY-LATE-GW", 18_000_000m, "VND", pastCutoffTime));

                var res = await workflow.ProcessInvoicePaidAsync(env);
                Assert.True(res.IsSuccess);
            }

            // Renter receives PAYMENT_REFUND_REQUIRED with NEW incremented version
            var rNote = await renterSignal.Task.WaitAsync(ReceiveTimeout);
            Assert.Equal(OrderChangeTypes.PaymentRefundRequired, rNote.ChangeType);
            Assert.True(rNote.Version > versionBefore, "Renter must receive NEW incremented version!");

            // Supplier who never had visibility receives NOTHING
            await Task.Delay(200);
            Assert.False(supplierSignal.Task.IsCompleted, "Supplier must NOT receive event for order that was never paid/visible!");

            await renterConn.StopAsync();
            await supplierConn.StopAsync();
        }
        finally
        {
            await DropPostgreSqlDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task MultiTabSameUserBothReceiveOtherUserReceivesNothing()
    {
        var (connString, dbName) = await CreatePostgreSqlDatabaseAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<RentalServiceDbContext>().UseNpgsql(connString).Options;
            Guid orderId = Guid.NewGuid();
            Guid renter1Id = Guid.NewGuid();
            Guid renter2Id = Guid.NewGuid();
            Guid supplierA = Guid.NewGuid();
            Guid supplierB = Guid.NewGuid();

            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                SeedOrder(seedDb, orderId, renter1Id, supplierA, supplierB);
                await seedDb.SaveChangesAsync();
            }

            using var rsa = RSA.Create(2048);
            string keyId = "gw-rental-key-05";
            await using var jwksUpstream = new LoopbackTestService(CreateJwks(rsa, keyId));
            await using var rentalServer = new KestrelRentalServer(connString, rsa, keyId);
            _ = rentalServer.CreateClient();
            await using var gatewayServer = new KestrelGatewayServer(new Uri(rentalServer.ServerAddress), new Uri(jwksUpstream.BaseAddress, "/.well-known/jwks.json").ToString());
            _ = gatewayServer.CreateClient();
            string r1Token = CreateJwtToken(rsa, keyId, renter1Id);
            string r2Token = CreateJwtToken(rsa, keyId, renter2Id);

            var r1Tab1 = CreateGatewayHubConnection(gatewayServer.ServerAddress, r1Token);
            var r1Tab2 = CreateGatewayHubConnection(gatewayServer.ServerAddress, r1Token);
            var r2Tab = CreateGatewayHubConnection(gatewayServer.ServerAddress, r2Token);

            var r1Tab1Signal = new TaskCompletionSource<RenterOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            var r1Tab2Signal = new TaskCompletionSource<RenterOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            var r2Signal = new TaskCompletionSource<RenterOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);

            r1Tab1.On<RenterOrderChangedNotification>("RenterOrderChanged", n => r1Tab1Signal.TrySetResult(n));
            r1Tab2.On<RenterOrderChangedNotification>("RenterOrderChanged", n => r1Tab2Signal.TrySetResult(n));
            r2Tab.On<RenterOrderChangedNotification>("RenterOrderChanged", n => r2Signal.TrySetResult(n));

            await r1Tab1.StartAsync();
            await r1Tab2.StartAsync();
            await r2Tab.StartAsync();

            // Mutate Renter 1's order
            using (var scope = rentalServer.RentalServices.CreateScope())
            {
                var workflow = scope.ServiceProvider.GetRequiredService<IPaymentWorkflowService>();
                var env = new KafkaMessageEnvelope<InvoicePaidPayload>(
                    Guid.NewGuid(), "InvoicePaid", 1, "payment-service",
                    orderId.ToString("D"), FixedNow, "corr-multi-tab", null,
                    new InvoicePaidPayload(orderId, renter1Id, "INV-GW-01", "PAY-MULTI-TAB", 18_000_000m, "VND", FixedNow));

                var res = await workflow.ProcessInvoicePaidAsync(env);
                Assert.True(res.IsSuccess);
            }

            var noteTab1 = await r1Tab1Signal.Task.WaitAsync(ReceiveTimeout);
            var noteTab2 = await r1Tab2Signal.Task.WaitAsync(ReceiveTimeout);

            Assert.Equal(orderId, noteTab1.OrderId);
            Assert.Equal(orderId, noteTab2.OrderId);

            // User 2 must receive NOTHING
            await Task.Delay(200);
            Assert.False(r2Signal.Task.IsCompleted, "Renter 2 must NOT receive notification for Renter 1's order!");

            await r1Tab1.StopAsync();
            await r1Tab2.StopAsync();
            await r2Tab.StopAsync();
        }
        finally
        {
            await DropPostgreSqlDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task ReconnectAndTokenExpiryThroughGateway()
    {
        var (connString, dbName) = await CreatePostgreSqlDatabaseAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<RentalServiceDbContext>().UseNpgsql(connString).Options;
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid supplierA = Guid.NewGuid();
            Guid supplierB = Guid.NewGuid();

            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                SeedOrder(seedDb, orderId, renterId, supplierA, supplierB);
                await seedDb.SaveChangesAsync();
            }

            using var rsa = RSA.Create(2048);
            string keyId = "gw-rental-key-06";
            await using var jwksUpstream = new LoopbackTestService(CreateJwks(rsa, keyId));
            await using var rentalServer = new KestrelRentalServer(connString, rsa, keyId);
            _ = rentalServer.CreateClient();
            await using var gatewayServer = new KestrelGatewayServer(new Uri(rentalServer.ServerAddress), new Uri(jwksUpstream.BaseAddress, "/.well-known/jwks.json").ToString());
            _ = gatewayServer.CreateClient();
            // 1. Expired token is rejected through Gateway
            string expiredToken = CreateJwtToken(rsa, keyId, renterId, expired: true);
            var expConn = CreateGatewayHubConnection(gatewayServer.ServerAddress, expiredToken);
            await Assert.ThrowsAnyAsync<Exception>(() => expConn.StartAsync());

            // 2. Valid token connects, disconnects, reconnects through Gateway
            string validToken = CreateJwtToken(rsa, keyId, renterId);
            var validConn = CreateGatewayHubConnection(gatewayServer.ServerAddress, validToken);

            var eventSignal = new TaskCompletionSource<RenterOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            validConn.On<RenterOrderChangedNotification>("RenterOrderChanged", n => eventSignal.TrySetResult(n));

            await validConn.StartAsync();
            Assert.Equal(HubConnectionState.Connected, validConn.State);

            await validConn.StopAsync();
            Assert.Equal(HubConnectionState.Disconnected, validConn.State);

            // Reconnect
            await validConn.StartAsync();
            Assert.Equal(HubConnectionState.Connected, validConn.State);

            // Mutate and confirm delivery after reconnect
            using (var scope = rentalServer.RentalServices.CreateScope())
            {
                var workflow = scope.ServiceProvider.GetRequiredService<IPaymentWorkflowService>();
                var env = new KafkaMessageEnvelope<InvoicePaidPayload>(
                    Guid.NewGuid(), "InvoicePaid", 1, "payment-service",
                    orderId.ToString("D"), FixedNow, "corr-reconn", null,
                    new InvoicePaidPayload(orderId, renterId, "INV-GW-01", "PAY-RECONN", 18_000_000m, "VND", FixedNow));

                var res = await workflow.ProcessInvoicePaidAsync(env);
                Assert.True(res.IsSuccess);
            }

            var note = await eventSignal.Task.WaitAsync(ReceiveTimeout);
            Assert.Equal(orderId, note.OrderId);

            await validConn.StopAsync();
        }
        finally
        {
            await DropPostgreSqlDatabaseAsync(dbName);
        }
    }
}
