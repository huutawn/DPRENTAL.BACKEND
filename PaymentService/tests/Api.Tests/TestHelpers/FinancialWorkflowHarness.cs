using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.DTOs.SePay;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Repositories;
using Dprental.PaymentService.Domain.Repositories.Integrity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
namespace Dprental.PaymentService.Api.Tests.TestHelpers;

/// <summary>Real financial services and evidence pipeline on two disposable PostgreSQL databases.</summary>
public sealed class FinancialWorkflowHarness : IAsyncDisposable
{
    private readonly string _paymentName;
    private readonly string _integrityName;
    private readonly IntegrityKeyManager _keys;
    public DbContextOptions<PaymentServiceDbContext> PaymentOptions { get; }
    public DbContextOptions<IntegrityDbContext> IntegrityOptions { get; }
    public IPaymentDataEncryptor Encryptor { get; } = new PaymentDataEncryptor(new byte[32]);
    public string PublicKey => _keys.PublicKeyBase64;
    public string KeyId => _keys.KeyId;

    private FinancialWorkflowHarness(string payment, string paymentName, string integrity, string integrityName, string? privateKey = null)
    {
        _paymentName = paymentName; _integrityName = integrityName;
        PaymentOptions = PostgresTestDatabaseHelper.CreateOptions(payment);
        IntegrityOptions = new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(integrity).Options;
        using var key = EcdsaAttestation.CreateEphemeralP256KeyPair();
        _keys = new IntegrityKeyManager(Options.Create(new IntegrityHostOptions { PrivateKeyBase64 = privateKey ?? EcdsaAttestation.ExportPrivateKeyBase64(key) }));
    }

    public static FinancialWorkflowHarness Attach(string payment, string integrity, ECDsa key) =>
        new(payment, string.Empty, integrity, string.Empty, EcdsaAttestation.ExportPrivateKeyBase64(key));

    public static async Task<FinancialWorkflowHarness> AttachPaymentDatabaseAsync(string payment)
    {
        var (integrity, name) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var h = new FinancialWorkflowHarness(payment, string.Empty, integrity, name);
        await using var idb = new IntegrityDbContext(h.IntegrityOptions);
        await idb.Database.MigrateAsync();
        return h;
    }

    public static async Task<FinancialWorkflowHarness> CreateAsync()
    {
        var (p, pn) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var (i, inode) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var h = new FinancialWorkflowHarness(p, pn, i, inode);
        await using var pdb = new PaymentServiceDbContext(h.PaymentOptions);
        await using var idb = new IntegrityDbContext(h.IntegrityOptions);
        await pdb.Database.MigrateAsync(); await idb.Database.MigrateAsync();
        return h;
    }

    public SettlementService Settlement(PaymentServiceDbContext db, Action? afterVerification = null) =>
        new(new EfInvoiceRepository(db), new EfSettlementLedger(db), Checker(afterVerification),
            new ManualRefundRepository(db), new UnitOfWork(db), Encryptor, TimeProvider.System);

    public ManualRefundService Refund(PaymentServiceDbContext db) => new(new ManualRefundRepository(db), Checker(),
        new EfInvoiceRepository(db), new EfSettlementLedger(db), new UnitOfWork(db), Encryptor, TimeProvider.System);

    public ITransactionIntegrityChecker Checker(Action? afterVerification = null) => new IntegrityHostClient(
        new HttpClient(new EngineHandler(this, afterVerification)) { BaseAddress = new Uri("http://integrity.test") },
        new Monitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions { PublicKeys = new() { [_keys.KeyId] = _keys.PublicKeyBase64 }, ServerBudgetMilliseconds = 5000 }),
        NullLogger<IntegrityHostClient>.Instance);

    public void SignJournal(TransactionJournalEntry entry, DateTimeOffset confirmedAt)
    {
        string message = EcdsaAttestation.BuildAttestationMessageV2(_keys.KeyId, _keys.Environment,
            entry.StreamId, entry.Version, entry.EventId, entry.EntryHash, confirmedAt);
        entry.Confirm(_keys.Sign(message), _keys.KeyId, confirmedAt);
    }

    public async Task RecordJournalAttestationAsync(TransactionJournalEntry entry)
    {
        await using var db = new IntegrityDbContext(IntegrityOptions);
        db.Attestations.Add(AttestationRecord.Create(entry.StreamId, entry.Version, entry.EventId,
            entry.EntryHash, entry.PayloadHash, entry.PreviousHash, entry.ProofSignature!, entry.SigningKeyId!,
            entry.ConfirmedAt!.Value));
        db.Manifests.Add(StreamManifest.Create(entry.StreamId, entry.Version, entry.EntryHash, entry.EventId,
            entry.ConfirmedAt.Value));
        await db.SaveChangesAsync();
    }

    private sealed class EngineHandler(FinancialWorkflowHarness h, Action? afterVerification) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await using var p = new PaymentServiceDbContext(h.PaymentOptions);
            await using var i = new IntegrityDbContext(h.IntegrityOptions);
            var engine = new IntegrityVerificationEngine(i, p, h._keys, h.Encryptor,
                new Monitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions { ServerBudgetMilliseconds = 5000 }));
            var body = await request.Content!.ReadFromJsonAsync<TransactionVerificationRequest>(ct);
            var response = await engine.VerifyAsync(body!, ct);
            afterVerification?.Invoke();
            return response.IsSuccess ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response.Value) }
                : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(response.Error) };
        }
    }

    public async Task ConfirmAsync()
    {
        await using var p = new PaymentServiceDbContext(PaymentOptions);
        await using var i = new IntegrityDbContext(IntegrityOptions);
        var confirmer = new IntegrityConfirmationService(new EfIntegrityEvidenceRepository(i), new EfTransactionJournalRepository(p), _keys, Encryptor);
        for (int n = 0; n < 10 && await confirmer.ProcessConfirmationBatchAsync() > 0; n++) { }
        Assert.False(await i.Incidents.AnyAsync());
    }

    public async Task<Invoice> SeedAsync(bool paid = true, int count = 1, bool late = false)
    {
        await using var db = new PaymentServiceDbContext(PaymentOptions);
        var repo = new EfInvoiceRepository(db, Encryptor);
        var lifecycle = new InvoiceLifecycleService(repo, null, Encryptor, new ManualRefundRepository(db), Checker());
        var creation = new InvoiceCreationService(repo, Options.Create(new PaymentMerchantOptions { Bank = "MBBank", Account = "123456789" }), lifecycle, Encryptor);
        var request = new CreateInvoiceRequest(Guid.NewGuid(), "ORD-TEST", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(20),
            Enumerable.Range(0, count).Select(_ => new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 100_000, 90_000)).ToArray(), Guid.NewGuid().ToString("N"));
        var created = await creation.CreateInvoiceAsync(request);
        Assert.True(created.IsSuccess, created.Error.Message);
        var invoice = (await repo.GetInvoiceSnapshotAsync(created.Value.InvoiceRef))!;
        if (late)
        {
            var cancelled = await lifecycle.CancelInvoiceAsync(invoice.InvoiceRef,
                new CancelInvoiceRequest(invoice.RentalOrderId, invoice.InvoiceRef, "Expired checkout", Guid.NewGuid().ToString("N"), InvoiceCancellationOrigins.Timeout));
            Assert.True(cancelled.IsSuccess, cancelled.Error.Message);
        }
        if (paid)
        {
            var processor = new InvoicePaymentProcessor(repo,
                new Monitor<PaymentMerchantOptions>(new PaymentMerchantOptions { Bank = "MBBank", Account = "123456789" }),
                new Monitor<SePayOptions>(new SePayOptions { AccountNumber = "123456789", GatewayMapping = new() { ["MBBank"] = "MBBank" } }),
                null, Encryptor);
            var payment = await processor.ProcessAsync(new SePayWebhookPayload(RandomNumberGenerator.GetInt32(1, int.MaxValue), "MBBank",
                DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                "123456789", null, invoice.PaymentCode, invoice.PaymentCode, "in", null, invoice.TotalAmount, null, "BANK-IN"));
            Assert.True(payment.IsSuccess, payment.Error.Message);
        }
        await ConfirmAsync();
        return (await repo.GetInvoiceSnapshotAsync(invoice.InvoiceRef))!;
    }

    public static SettleRentalRequest SettlementRequest(Invoice invoice, decimal deduction = 20_000, string key = "settlement")
    {
        var item = invoice.Items[0];
        return new(invoice.RentalOrderId, item.RentalItemId, invoice.InvoiceRef, invoice.PaymentRef!, item.SupplierId,
            item.DepositAmount, deduction, item.DepositAmount - deduction, "VND", "DECISION", key);
    }

    public async ValueTask DisposeAsync()
    {
        _keys.Dispose();
        if (_paymentName.Length > 0) await PostgresTestDatabaseHelper.DropTestDatabaseAsync(_paymentName);
        if (_integrityName.Length > 0) await PostgresTestDatabaseHelper.DropTestDatabaseAsync(_integrityName);
    }

    public sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
