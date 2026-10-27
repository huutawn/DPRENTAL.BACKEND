using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.RentalService.Api.Hubs;
using Dprental.RentalService.Api.Hubs.Security;
using Dprental.RentalService.Api.Security;
using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.IServices.Payments;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Integration;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Application.Services.Payments;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Hubs;

public sealed class RentalHubRealClientIntegrationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 26, 14, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public sealed class KestrelRentalHubFactory : WebApplicationFactory<global::Program>
    {
        private readonly string _connectionString;
        private readonly RSA _rsa = RSA.Create(2048);
        private const string KeyId = "kestrel-rental-hub-key";
        public const string Issuer = "dprental-authservice-dev";
        public const string Audience = "dprental-clients";
        public const string ApiKey = "dev-internal-api-key-dprental-2026";
        public const string AllowedOrigin = "http://localhost:3000";

        public string ServerAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');
        public IServiceProvider RentalServices => Services;

        public KestrelRentalHubFactory(string connectionString)
        {
            _connectionString = connectionString;
            UseKestrel(0);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Postgres", _connectionString);
            builder.UseSetting("InternalAuthentication:ApiKey", ApiKey);
            builder.UseSetting("Authentication:Jwt:Issuer", Issuer);
            builder.UseSetting("Authentication:Jwt:Audience", Audience);
            builder.UseSetting("Authentication:Jwt:JwksUri", "http://localhost/test-jwks");
            builder.UseSetting("OrderHub:AllowedOrigins:0", AllowedOrigin);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = _connectionString,
                    ["Authentication:Jwt:Issuer"] = Issuer,
                    ["Authentication:Jwt:Audience"] = Audience,
                    ["Authentication:Jwt:JwksUri"] = "http://localhost/test-jwks",
                    ["InternalAuthentication:ApiKey"] = ApiKey,
                    ["OrderHub:AllowedOrigins:0"] = AllowedOrigin
                }));

            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.TokenValidationParameters.ValidIssuer = Issuer;
                    options.TokenValidationParameters.ValidAudience = Audience;
                    options.ConfigurationManager = new TestJwksConfigurationManager(_rsa, KeyId);
                });
            });

        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", ApiKey);
        }

        public string CreateToken(Guid userId, bool serviceToken = false, bool expired = false, string? customAudience = null)
        {
            var key = new RsaSecurityKey(_rsa) { KeyId = KeyId };
            var claims = new List<Claim> { new("sub", userId.ToString()) };
            if (serviceToken)
            {
                claims.Add(new Claim("u", "svc"));
            }

            var expires = expired ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(30);
            var notBefore = expired ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddSeconds(-5);

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Issuer = Issuer,
                Audience = customAudience ?? Audience,
                Expires = expires,
                NotBefore = notBefore,
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
            };
            var handler = new JsonWebTokenHandler();
            return handler.CreateToken(tokenDescriptor);
        }

        public HubConnection CreateHubConnection(string? token, string? origin = AllowedOrigin, string? apiKey = ApiKey)
        {
            var hubUrl = $"{ServerAddress}/api/v1/rental/hubs/orders";
            var builder = new HubConnectionBuilder()
                .WithUrl(hubUrl, options =>
                {
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        options.Cookies = new CookieContainer();
                        options.Cookies.Add(new Cookie(OrderHubSecurityConstants.AccessTokenCookieName, token, "/", new Uri(ServerAddress).Host));
                    }
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        options.Headers["X-Api-Key"] = apiKey;
                    }
                    if (!string.IsNullOrWhiteSpace(origin))
                    {
                        options.Headers["Origin"] = origin;
                    }
                });

            return builder.Build();
        }

        private sealed class TestJwksConfigurationManager(RSA rsa, string keyId)
            : IConfigurationManager<OpenIdConnectConfiguration>
        {
            private readonly OpenIdConnectConfiguration _configuration = CreateConfiguration(rsa, keyId);

            public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
                Task.FromResult(_configuration);

            public void RequestRefresh()
            {
            }

            private static OpenIdConnectConfiguration CreateConfiguration(RSA rsa, string keyId)
            {
                var config = new OpenIdConnectConfiguration();
                config.SigningKeys.Add(new RsaSecurityKey(rsa) { KeyId = keyId });
                return config;
            }
        }
    }

    private static RentalOrder SeedTwoItemOrder(RentalServiceDbContext db, Guid orderId, Guid renterId, Guid supplierA, Guid supplierB)
    {
        var itemA = new InitialRentalItemDto(
            Guid.NewGuid(), Guid.NewGuid(), supplierA, 1,
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5),
            "Máy xúc A", "Desc", "Kho 1", 1_000_000m, 5_000_000m,
            "grp-A", [new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
            TestListingSnapshots.Default);

        var itemB = new InitialRentalItemDto(
            Guid.NewGuid(), Guid.NewGuid(), supplierB, 1,
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5),
            "Máy ủi B", "Desc", "Kho 2", 1_000_000m, 5_000_000m,
            "grp-B", [new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
            TestListingSnapshots.Default);

        var order = RentalOrder.Create(
            orderId, "DH-REAL-001", renterId, "VND", "Pickup",
            "Nguyễn Văn A", "0901234567", "Hà Nội", null,
            "v1.0", "terms", "hash", "op-create", FixedNow,
            [itemA, itemB]).Value;

        order.AttachInvoice("INV-REAL-01", FixedNow);
        db.RentalOrders.Add(order);
        return order;
    }

    [Fact]
    public async Task RealClient_MultiUser_MultiConnection_MutationDelivery_AndPostgresResync()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
            throw new InvalidOperationException("PostgreSQL test database required.");

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            // Ensure schema created
            var dbOptions = new DbContextOptionsBuilder<RentalServiceDbContext>().UseNpgsql(connString).Options;
            Guid orderId = Guid.NewGuid();
            Guid renter1Id = Guid.NewGuid();
            Guid renter2Id = Guid.NewGuid();
            Guid supplierAId = Guid.NewGuid();
            Guid supplierBId = Guid.NewGuid();

            await using (var initDb = new RentalServiceDbContext(dbOptions))
            {
                await initDb.Database.EnsureCreatedAsync();
                SeedTwoItemOrder(initDb, orderId, renter1Id, supplierAId, supplierBId);
                await initDb.SaveChangesAsync();
            }

            await using var factory = new KestrelRentalHubFactory(connString);
            _ = factory.CreateClient(); // triggers Kestrel start

            // Scenario:
            // Renter 1 has 2 connections (e.g. 2 browser tabs)
            // Supplier A has 1 connection
            // Supplier B has 1 connection
            // Renter 2 (unrelated user) has 1 connection

            string tokenRenter1 = factory.CreateToken(renter1Id);
            string tokenRenter2 = factory.CreateToken(renter2Id);
            string tokenSupplierA = factory.CreateToken(supplierAId);
            string tokenSupplierB = factory.CreateToken(supplierBId);

            var r1Tab1Conn = factory.CreateHubConnection(tokenRenter1);
            var r1Tab2Conn = factory.CreateHubConnection(tokenRenter1);
            var supAConn = factory.CreateHubConnection(tokenSupplierA);
            var supBConn = factory.CreateHubConnection(tokenSupplierB);
            var r2Conn = factory.CreateHubConnection(tokenRenter2);

            var r1Tab1Signal = new TaskCompletionSource<RenterOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            var r1Tab2Signal = new TaskCompletionSource<RenterOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            var supASignal = new TaskCompletionSource<SupplierSubOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            var supBSignal = new TaskCompletionSource<SupplierSubOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            var r2Signal = new TaskCompletionSource<RenterOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Register handlers BEFORE connecting
            r1Tab1Conn.On<RenterOrderChangedNotification>("RenterOrderChanged", n => r1Tab1Signal.TrySetResult(n));
            r1Tab2Conn.On<RenterOrderChangedNotification>("RenterOrderChanged", n => r1Tab2Signal.TrySetResult(n));
            supAConn.On<SupplierSubOrderChangedNotification>("SupplierSubOrderChanged", n => supASignal.TrySetResult(n));
            supBConn.On<SupplierSubOrderChangedNotification>("SupplierSubOrderChanged", n => supBSignal.TrySetResult(n));
            r2Conn.On<RenterOrderChangedNotification>("RenterOrderChanged", n => r2Signal.TrySetResult(n));

            // Start connections
            await r1Tab1Conn.StartAsync();
            await r1Tab2Conn.StartAsync();
            await supAConn.StartAsync();
            await supBConn.StartAsync();
            await r2Conn.StartAsync();

            // Perform real mutation using Application service from factory.Services (outer transaction & commit):
            using (var scope = factory.Services.CreateScope())
            {
                var workflow = scope.ServiceProvider.GetRequiredService<IPaymentWorkflowService>();

                var paidPayload = new InvoicePaidPayload(
                    orderId, renter1Id, "INV-REAL-01", "PAY-REAL-01",
                    18_000_000m, "VND", FixedNow);

                var env = new KafkaMessageEnvelope<InvoicePaidPayload>(
                    Guid.NewGuid(), "InvoicePaid", 1, "payment-service",
                    orderId.ToString("D"), FixedNow, "corr-real-pay", null, paidPayload);

                var payResult = await workflow.ProcessInvoicePaidAsync(env);
                Assert.True(payResult.IsSuccess, payResult.IsFailure ? payResult.Error.Message : null);
            }

            // Await delivery with finite timeouts
            var r1Note1 = await r1Tab1Signal.Task.WaitAsync(ReceiveTimeout);
            var r1Note2 = await r1Tab2Signal.Task.WaitAsync(ReceiveTimeout);
            var supANote = await supASignal.Task.WaitAsync(ReceiveTimeout);
            var supBNote = await supBSignal.Task.WaitAsync(ReceiveTimeout);
            // Verify both Renter 1 tabs received the notification
            Assert.Equal(orderId, r1Note1.OrderId);
            Assert.Equal(OrderChangeTypes.PaymentConfirmed, r1Note1.ChangeType);
            Assert.Equal(r1Note1.Version, r1Note2.Version);

            // Verify both suppliers received notifications for their respective suborders
            Assert.Equal(orderId, supANote.OrderId);
            Assert.Equal(OrderChangeTypes.PaymentConfirmed, supANote.ChangeType);
            Assert.Equal(orderId, supBNote.OrderId);
            Assert.NotEqual(supANote.SubOrderId, supBNote.SubOrderId);

            // Unrelated Renter 2 MUST NOT receive any notification
            await Task.Delay(200);
            Assert.False(r2Signal.Task.IsCompleted, "Unrelated user Renter 2 must not receive notification");

            // REST Resync using fresh separate DbContext
            await using (var resyncDb = new RentalServiceDbContext(dbOptions))
            {
                var repo = new RentalOrderRepository(resyncDb);
                var reloadedOrder = await repo.GetByIdWithDetailsAsync(orderId);
                Assert.NotNull(reloadedOrder);
                Assert.Equal(RentalOrderStatus.Paid, reloadedOrder.Status);
                Assert.Equal(r1Note1.Version, reloadedOrder.Version);

                // Both suppliers can now read their respective suborders on REST
                var subOrderA = await repo.GetPaidSubOrderForSupplierAsync(supANote.SubOrderId, supplierAId);
                Assert.NotNull(subOrderA);

                var subOrderB = await repo.GetPaidSubOrderForSupplierAsync(supBNote.SubOrderId, supplierBId);
                Assert.NotNull(subOrderB);
            }

            // Cleanup connections
            await r1Tab1Conn.StopAsync();
            await r1Tab2Conn.StopAsync();
            await supAConn.StopAsync();
            await supBConn.StopAsync();
            await r2Conn.StopAsync();
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task DisconnectAndReconnect_ReEstablishesGroupMembership()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
            throw new InvalidOperationException("PostgreSQL test database required.");

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<RentalServiceDbContext>().UseNpgsql(connString).Options;
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid supplierId = Guid.NewGuid();

            await using (var initDb = new RentalServiceDbContext(dbOptions))
            {
                await initDb.Database.EnsureCreatedAsync();
                var order = CreateOrder(orderId, renterId, (Guid.NewGuid(), supplierId));
                order.AttachInvoice("INV-RECON-1", FixedNow);
                initDb.RentalOrders.Add(order);
                await initDb.SaveChangesAsync();
            }

            await using var factory = new KestrelRentalHubFactory(connString);
            _ = factory.CreateClient();

            string token = factory.CreateToken(renterId);
            var conn = factory.CreateHubConnection(token);

            var firstSignal = new TaskCompletionSource<RenterOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondSignal = new TaskCompletionSource<RenterOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);

            int callCount = 0;
            conn.On<RenterOrderChangedNotification>("RenterOrderChanged", n =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    firstSignal.TrySetResult(n);
                else
                    secondSignal.TrySetResult(n);
            });

            // Connect, disconnect, and reconnect
            await conn.StartAsync();
            await conn.StopAsync();
            await conn.StartAsync(); // Re-establish connection & group membership

            // Trigger mutation
            using (var scope = factory.Services.CreateScope())
            {
                var workflow = scope.ServiceProvider.GetRequiredService<IPaymentWorkflowService>();
                var env = new KafkaMessageEnvelope<InvoicePaidPayload>(
                    Guid.NewGuid(), "InvoicePaid", 1, "payment-service",
                    orderId.ToString("D"), FixedNow, "corr-recon", null,
                    new InvoicePaidPayload(orderId, renterId, "INV-RECON-1", "PAY-RECON-1", 9_000_000m, "VND", FixedNow));

                var res = await workflow.ProcessInvoicePaidAsync(env);
                Assert.True(res.IsSuccess);
            }

            // Verify event received after reconnection
            var note = await firstSignal.Task.WaitAsync(ReceiveTimeout);
            Assert.Equal(orderId, note.OrderId);

            await conn.StopAsync();
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task AuthenticationRejections_Anonymous_ServiceToken_ExpiredToken_InvalidOrigin()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
            throw new InvalidOperationException("PostgreSQL test database required.");

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<RentalServiceDbContext>().UseNpgsql(connString).Options;
            await using (var initDb = new RentalServiceDbContext(dbOptions))
            {
                await initDb.Database.EnsureCreatedAsync();
            }

            await using var factory = new KestrelRentalHubFactory(connString);
            _ = factory.CreateClient();

            // 1. Anonymous (no token) -> Rejected
            var anonConn = factory.CreateHubConnection(token: null);
            await Assert.ThrowsAnyAsync<Exception>(() => anonConn.StartAsync());

            // 2. Service token (u=svc claim) -> Aborted
            string serviceToken = factory.CreateToken(Guid.NewGuid(), serviceToken: true);
            var svcConn = factory.CreateHubConnection(serviceToken);
            await Assert.ThrowsAnyAsync<Exception>(() => svcConn.StartAsync());

            // 3. Expired token -> Rejected
            string expiredToken = factory.CreateToken(Guid.NewGuid(), expired: true);
            var expConn = factory.CreateHubConnection(expiredToken);
            await Assert.ThrowsAnyAsync<Exception>(() => expConn.StartAsync());

            // 4. Invalid Origin -> 403 Forbidden
            string validToken = factory.CreateToken(Guid.NewGuid());
            var invalidOriginConn = factory.CreateHubConnection(validToken, origin: "https://malicious-hacker.com");
            await Assert.ThrowsAnyAsync<Exception>(() => invalidOriginConn.StartAsync());

            // 5. Valid token & origin -> Succeeds
            var validConn = factory.CreateHubConnection(validToken, origin: KestrelRentalHubFactory.AllowedOrigin);
            await validConn.StartAsync();
            Assert.Equal(HubConnectionState.Connected, validConn.State);
            await validConn.StopAsync();
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Supplier_NotAuthorizedToRead_DoesNotReceiveNotification()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
            throw new InvalidOperationException("PostgreSQL test database required.");

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<RentalServiceDbContext>().UseNpgsql(connString).Options;
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid supplierId = Guid.NewGuid();
            Guid itemId = Guid.NewGuid();

            await using (var initDb = new RentalServiceDbContext(dbOptions))
            {
                await initDb.Database.EnsureCreatedAsync();
                var order = CreateOrder(orderId, renterId, (itemId, supplierId));
                string relOpKey = RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{orderId}:{itemId}");
                var relReceipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(), orderId, relOpKey, "ReleaseAllocation",
                    RentalOperationExecutor.ComputeFingerprint($"ReleaseAllocation:{order.Items.Single().AllocationGroupId}"),
                    FixedNow, itemId, "{}");
                order.AddOperationReceipt(relReceipt);
                initDb.RentalOrders.Add(order);
                await initDb.SaveChangesAsync();
            }

            await using var factory = new KestrelRentalHubFactory(connString);
            _ = factory.CreateClient();

            string tokenRenter = factory.CreateToken(renterId);
            string tokenSupplier = factory.CreateToken(supplierId);

            var renterConn = factory.CreateHubConnection(tokenRenter);
            var supplierConn = factory.CreateHubConnection(tokenSupplier);

            var renterSignal = new TaskCompletionSource<RenterOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            var supplierSignal = new TaskCompletionSource<SupplierSubOrderChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);

            renterConn.On<RenterOrderChangedNotification>("RenterOrderChanged", n => renterSignal.TrySetResult(n));
            supplierConn.On<SupplierSubOrderChangedNotification>("SupplierSubOrderChanged", n => supplierSignal.TrySetResult(n));

            await renterConn.StartAsync();
            await supplierConn.StartAsync();

            // Mutate unpaid order: Release Allocation
            string relOp = RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{orderId}:{itemId}");
            var relBody = JsonSerializer.Serialize(new { receiptRef = "rcpt-unpaid-rel" });
            var callbackPayload = new
            {
                commandType = "ReleaseAllocation",
                operationKey = relOp,
                rentalOrderId = orderId,
                rentalItemId = itemId,
                success = true,
                payload = JsonDocument.Parse(relBody).RootElement
            };
            var env = new
            {
                messageId = Guid.NewGuid(),
                messageType = KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
                version = 1,
                producer = KafkaContractSpec.Producers.InventoryService,
                aggregateId = orderId.ToString("D"),
                occurredAt = FixedNow.AddMinutes(5),
                correlationId = "corr-unpaid-rel",
                causationId = (Guid?)null,
                payload = callbackPayload
            };
            string envJson = JsonSerializer.Serialize(env, JsonOptions);

            Guid inboxId = Guid.NewGuid();
            await using (var seedInboxDb = new RentalServiceDbContext(dbOptions))
            {
                var inbox = new RentalInboxMessage(
                    inboxId, env.messageId, KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
                    KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), "corr-unpaid-rel", null,
                    envJson, FixedNow.AddMinutes(5), "fp-unpaid-rel");
                seedInboxDb.RentalInboxMessages.Add(inbox);
                await seedInboxDb.SaveChangesAsync();
            }

            using (var scope = factory.Services.CreateScope())
            {
                var dispatcher = scope.ServiceProvider.GetRequiredService<IPaymentEventsDispatcher>();
                var res = await dispatcher.ProcessInboxMessageAsync(inboxId);
                Assert.True(res.IsSuccess, res.IsFailure ? res.Error.Message : null);
            }

            // Renter receives ALLOCATION_CHANGED
            var rNote = await renterSignal.Task.WaitAsync(ReceiveTimeout);
            Assert.Equal(OrderChangeTypes.AllocationChanged, rNote.ChangeType);
            // Supplier is NOT authorized to read (order not yet paid) -> receives NOTHING!
            await Task.Delay(200);
            Assert.False(supplierSignal.Task.IsCompleted, "Supplier not authorized to read unpaid order must not receive event");

            await renterConn.StopAsync();
            await supplierConn.StopAsync();
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    private static RentalOrder CreateOrder(Guid orderId, Guid renterId, params (Guid ItemId, Guid SupplierId)[] items)
    {
        var itemDtos = items.Select((it, idx) =>
        {
            var assign = new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            return new InitialRentalItemDto(
                it.ItemId,
                Guid.NewGuid(),
                it.SupplierId,
                1,
                new DateOnly(2026, 11, 1),
                new DateOnly(2026, 11, 5),
                $"Thiết bị {idx + 1}",
                "Mô tả",
                "Kho 1",
                1_000_000m,
                5_000_000m,
                $"grp-item-{idx + 1}",
                [assign],
                TestListingSnapshots.Default);
        }).ToList();

        var order = RentalOrder.Create(
            orderId,
            $"DH-REAL-{orderId:N}"[..16],
            renterId,
            "VND",
            "Pickup",
            "Nguyễn Văn A",
            "0901234567",
            "Địa chỉ",
            null,
            "v1.0",
            "terms",
            "hash",
            $"op-create-{orderId:N}"[..20],
            FixedNow,
            itemDtos).Value;

        return order;
    }
}
