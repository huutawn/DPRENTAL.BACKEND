using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.PaymentService.Api.Controllers;
using Dprental.PaymentService.Api.Security;
using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.DTOs.Bank;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class PaymentWebhooksHttpTests : IAsyncLifetime
{
    private const string ConfiguredApiKey = "test-sepay-secret-key-12345";
    private string _connString = string.Empty;
    private string _dbName = string.Empty;
    private WebApplicationFactory<Program> _factory = null!;
    private readonly TestLogSink _logSink = new();

    public async Task InitializeAsync()
    {
        (_connString, _dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var options = PostgresTestDatabaseHelper.CreateOptions(_connString);
        await using (var ctx = new PaymentServiceDbContext(options))
        {
            await ctx.Database.MigrateAsync();
        }

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", _connString);
                builder.UseSetting("Payment:Merchant:Bank", "MBBank");
                builder.UseSetting("Payment:Merchant:Account", "0383799632");
                builder.UseSetting("Payment:SePay:ApiKey", ConfiguredApiKey);
                builder.UseSetting("Payment:SePay:Gateway", "MBBank");
                builder.UseSetting("Payment:SePay:AccountNumber", "0383799632");

                builder.ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddProvider(new TestLoggerProvider(_logSink));
                });
            });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await PostgresTestDatabaseHelper.DropTestDatabaseAsync(_dbName);
    }

    [Fact]
    public async Task ReceiveWebhook_MissingAuthorizationHeader_Returns401_ProcessorNotCalled()
    {
        bool processorCalled = false;
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInvoicePaymentProcessor>();
                services.AddScoped<IInvoicePaymentProcessor>(_ => new CallbackProcessor(_ =>
                {
                    processorCalled = true;
                    return Task.FromResult(Result.Success());
                }));
            });
        }).CreateClient();

        var payload = CreateValidPayload();
        var response = await client.PostAsJsonAsync("/api/v1/payment/webhooks", payload);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(processorCalled);
    }

    [Fact]
    public async Task ReceiveWebhook_WrongApiKey_Returns401_ProcessorNotCalled_DoesNotLogSecret()
    {
        bool processorCalled = false;
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInvoicePaymentProcessor>();
                services.AddScoped<IInvoicePaymentProcessor>(_ => new CallbackProcessor(_ =>
                {
                    processorCalled = true;
                    return Task.FromResult(Result.Success());
                }));
            });
        }).CreateClient();

        const string wrongKey = "wrong-secret-key-99999";
        client.DefaultRequestHeaders.Add("Authorization", $"Apikey {wrongKey}");

        var payload = CreateValidPayload();
        var response = await client.PostAsJsonAsync("/api/v1/payment/webhooks", payload);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(processorCalled);

        // Verify logs do not leak configured key or provided key
        Assert.DoesNotContain(ConfiguredApiKey, _logSink.GetAllLogs(), StringComparison.Ordinal);
        Assert.DoesNotContain(wrongKey, _logSink.GetAllLogs(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceiveWebhook_JwtBearerInsteadOfApiKey_Returns401_ProcessorNotCalled()
    {
        bool processorCalled = false;
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInvoicePaymentProcessor>();
                services.AddScoped<IInvoicePaymentProcessor>(_ => new CallbackProcessor(_ =>
                {
                    processorCalled = true;
                    return Task.FromResult(Result.Success());
                }));
            });
        }).CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.dummy");

        var payload = CreateValidPayload();
        var response = await client.PostAsJsonAsync("/api/v1/payment/webhooks", payload);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(processorCalled);
    }

    [Fact]
    public async Task ReceiveWebhook_MalformedJson_Returns400()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Apikey {ConfiguredApiKey}");

        using var content = new StringContent("{ invalid json ...", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/v1/payment/webhooks", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-999)]
    public async Task ReceiveWebhook_InvalidId_ZeroOrNegative_Returns400(long invalidId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Apikey {ConfiguredApiKey}");

        var payload = CreateValidPayload() with { Id = invalidId };
        var response = await client.PostAsJsonAsync("/api/v1/payment/webhooks", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    [InlineData(100.5)]
    public async Task ReceiveWebhook_InvalidAmount_ZeroOrNegativeOrNonInteger_Returns400(decimal invalidAmount)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Apikey {ConfiguredApiKey}");

        var payload = CreateValidPayload() with { TransferAmount = invalidAmount };
        var response = await client.PostAsJsonAsync("/api/v1/payment/webhooks", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("", "0383799632", "in", "2024-07-02 11:08:33")]
    [InlineData("MBBank", "", "in", "2024-07-02 11:08:33")]
    [InlineData("MBBank", "0383799632", "", "2024-07-02 11:08:33")]
    [InlineData("MBBank", "0383799632", "in", "")]
    [InlineData("MBBank", "0383799632", "in", "not-a-date")]
    public async Task ReceiveWebhook_MissingRequiredFields_Returns400(
        string gateway, string account, string transferType, string txDate)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Apikey {ConfiguredApiKey}");

        var payload = CreateValidPayload() with
        {
            Gateway = gateway,
            AccountNumber = account,
            TransferType = transferType,
            TransactionDate = txDate
        };

        var response = await client.PostAsJsonAsync("/api/v1/payment/webhooks", payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReceiveWebhook_UnsupportedContentType_Returns415()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Apikey {ConfiguredApiKey}");

        using var content = new StringContent("<xml>not json</xml>", Encoding.UTF8, "application/xml");
        var response = await client.PostAsync("/api/v1/payment/webhooks", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task ReceiveWebhook_ValidWebhook_Returns200WithSuccessTrue()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Apikey {ConfiguredApiKey}");

        var payload = CreateValidPayload();
        var response = await client.PostAsJsonAsync("/api/v1/payment/webhooks", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task ReceiveWebhook_DuplicateSameData_Returns200WithSuccessTrue()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Apikey {ConfiguredApiKey}");

        var payload = CreateValidPayload();
        var res1 = await client.PostAsJsonAsync("/api/v1/payment/webhooks", payload);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        // Replay same payload
        var res2 = await client.PostAsJsonAsync("/api/v1/payment/webhooks", payload);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        var json = await res2.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task ReceiveWebhook_DuplicateId_ConflictingData_Returns409()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Apikey {ConfiguredApiKey}");

        var payload = CreateValidPayload();
        var res1 = await client.PostAsJsonAsync("/api/v1/payment/webhooks", payload);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        // Same ID but different amount
        var conflictingPayload = payload with { TransferAmount = payload.TransferAmount + 100_000m };
        var res2 = await client.PostAsJsonAsync("/api/v1/payment/webhooks", conflictingPayload);
        Assert.Equal(HttpStatusCode.Conflict, res2.StatusCode);
    }

    private static SePayWebhookPayload CreateValidPayload() => new(
        Id: 92704,
        Gateway: "MBBank",
        TransactionDate: "2024-07-02 11:08:33",
        AccountNumber: "0383799632",
        SubAccount: null,
        Code: "DPR 23456789AB",
        Content: "DPR 23456789AB chuyen tien",
        TransferType: "in",
        Description: "NGUYEN VAN A chuyen tien",
        TransferAmount: 500_000m,
        Accumulated: 10_000_000m,
        ReferenceCode: "FT24012345678");

    private sealed class CallbackProcessor(Func<VerifiedBankTransaction, Task<Result>> callback) : IInvoicePaymentProcessor
    {
        public Task<Result> ProcessAsync(VerifiedBankTransaction transaction, CancellationToken ct = default) =>
            callback(transaction);
    }

    private sealed class TestLogSink
    {
        private readonly List<string> _messages = [];
        private readonly object _lock = new();

        public void Add(string message)
        {
            lock (_lock) _messages.Add(message);
        }

        public string GetAllLogs()
        {
            lock (_lock) return string.Join("\n", _messages);
        }
    }

    private sealed class TestLoggerProvider(TestLogSink sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new TestLogger(sink);
        public void Dispose() { }

        private sealed class TestLogger(TestLogSink sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                sink.Add(formatter(state, exception));
            }
        }
    }
}
