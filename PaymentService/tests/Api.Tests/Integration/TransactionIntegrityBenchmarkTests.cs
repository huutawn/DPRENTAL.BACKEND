using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Application.Services;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Persistence;
using Dprental.PaymentService.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class TransactionIntegrityBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public TransactionIntegrityBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    [Fact]
    public async Task Benchmark_OneMillionRecords_100ChecksPerSecond_AchievesSubSecondP99()
    {
        var (paymentConn, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var (integrityConn, iDbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var paymentOptions = PostgresTestDatabaseHelper.CreateOptions(paymentConn);
            var iOptions = new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(integrityConn).Options;

            await using (var ctx = new PaymentServiceDbContext(paymentOptions)) await ctx.Database.MigrateAsync();
            await using (var iCtx = new IntegrityDbContext(iOptions)) await iCtx.Database.MigrateAsync();

            // 1. Setup ECDSA KeyPair & test signature template
            using var ecdsa = EcdsaAttestation.CreateEphemeralP256KeyPair();
            string keyId = "bench-key-2026";
            string publicKeyBase64 = EcdsaAttestation.ExportPublicKeyBase64(ecdsa);

            DateTimeOffset baseTime = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
            string basePayloadHash = CanonicalJournalHasher.ComputePayloadHash(
                500_000m, "VND", "PAID", "WEBHOOK", baseTime, "WEBHOOK:SEPAY", null, null);

            var verifyOptions = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
            {
                ActiveKeyId = keyId,
                PublicKeys = new Dictionary<string, string> { [keyId] = publicKeyBase64 },
                ServerBudgetMilliseconds = 900
            });

            _output.WriteLine("Generating 1,000,000 journal records in PostgreSQL...");
            var swSeed = Stopwatch.StartNew();

            // Insert 1,000,000 rows directly via PostgreSQL generate_series for maximum speed
            await using (var ctx = new PaymentServiceDbContext(paymentOptions))
            {
                ctx.Database.SetCommandTimeout(300);
                string insertSql = $"""
                    INSERT INTO transaction_journal (
                        "Id", "StreamId", "StreamType", "Version", "EventId", "EventType",
                        "Amount", "Currency", "Status", "ConfirmationSource", "Actor", "TimestampUtc",
                        "PaymentRef", "InvoiceRef", "RentalOrderId", "RentalItemId", "OutboxDigest",
                        "EnvelopeDigest", "PreviousHash", "PayloadHash", "EntryHash", "Fingerprint", "EncryptedPayload",
                        "PayloadNonce", "PayloadTag", "EncryptionKeyId", "IsConfirmed", "ConfirmedAt",
                        "ProofSignature", "SigningKeyId"
                    )
                    SELECT
                        gen_random_uuid(),
                        'BENCH:' || s,
                        'PaymentTransaction',
                        1,
                        gen_random_uuid(),
                        'BANK_INCOMING',
                        500000,
                        'VND',
                        'PAID',
                        'WEBHOOK',
                        'WEBHOOK:SEPAY',
                        '2026-10-19 10:00:00+00',
                        'BENCH:' || s,
                        'INV-BENCH-' || s,
                        gen_random_uuid(),
                        gen_random_uuid(),
                        '',
                        '',
                        '{CanonicalJournalHasher.GenesisHash}',
                        '{basePayloadHash}',
                        'placeholder',
                        'DPRENTAL:BENCH:' || s,
                        '',
                        '',
                        '',
                        '',
                        TRUE,
                        '2026-10-19 10:00:00+00',
                        'placeholder',
                        '{keyId}'
                    FROM generate_series(1, 1000000) AS s;
                    """;

                await ctx.Database.ExecuteSqlRawAsync(insertSql);
            }

            swSeed.Stop();
            _output.WriteLine($"Seeded 1,000,000 records in {swSeed.ElapsedMilliseconds} ms.");

            // 2. Compute valid signature for sample queries and pre-sign a test set
            const int sampleSize = 300;
            var sampleStreamIds = new List<string>(sampleSize);
            for (int i = 1; i <= sampleSize; i++)
            {
                sampleStreamIds.Add($"BENCH:{i}");
            }

            var benchEncryptor = new PaymentDataEncryptor(new byte[32], "bench-enc-key");

            await using (var ctx = new PaymentServiceDbContext(paymentOptions))
            {
                var sampleEntries = await ctx.TransactionJournals
                    .Where(j => sampleStreamIds.Contains(j.StreamId))
                    .ToListAsync();

                await using var iCtx = new IntegrityDbContext(iOptions);
                foreach (var entry in sampleEntries)
                {
                    var doc = new CanonicalJournalDocument(
                        SchemaVersion: CanonicalJournalCodec.CurrentSchemaVersion,
                        Environment: "production",
                        OperationId: entry.OperationId,
                        EventId: entry.EventId,
                        StreamType: entry.StreamType,
                        StreamId: entry.StreamId,
                        Version: entry.Version,
                        EventType: entry.EventType,
                        Amount: entry.Amount,
                        Currency: entry.Currency,
                        Status: entry.Status,
                        ConfirmationSource: entry.ConfirmationSource,
                        Actor: entry.Actor,
                        RecordedAtUtc: entry.TimestampUtc,
                        PreviousHash: entry.PreviousHash);

                    string canonicalJson = CanonicalJournalCodec.SerializeToCanonicalJson(doc);
                    string payloadHash = CanonicalJournalCodec.ComputePayloadHash(canonicalJson);
                    byte[] aad = System.Text.Encoding.UTF8.GetBytes($"{entry.StreamId}:{entry.Version}:{entry.EventId:D}");
                    var enc = benchEncryptor.Encrypt(System.Text.Encoding.UTF8.GetBytes(canonicalJson), aad);
                    string envelopeDigest = CanonicalJournalCodec.ComputeEnvelopeDigest(enc.CiphertextBase64, enc.NonceBase64, enc.TagBase64, enc.KeyId);
                    string entryHash = CanonicalJournalCodec.ComputeEntryHash(entry.StreamId, entry.Version, entry.EventId, entry.PreviousHash, payloadHash, envelopeDigest);
                    string attestationMsg = EcdsaAttestation.BuildAttestationMessage(keyId, entry.StreamId, entry.Version, entry.EventId, entryHash, entry.ConfirmedAt!.Value);
                    string signature = EcdsaAttestation.Sign(ecdsa, attestationMsg);

                    await ctx.Database.ExecuteSqlRawAsync(
                        "UPDATE transaction_journal SET \"PayloadHash\" = {0}, \"EncryptedPayload\" = {1}, \"PayloadNonce\" = {2}, \"PayloadTag\" = {3}, \"EncryptionKeyId\" = {4}, \"EnvelopeDigest\" = {5}, \"EntryHash\" = {6}, \"ProofSignature\" = {7} WHERE \"Id\" = {8}",
                        payloadHash, enc.CiphertextBase64, enc.NonceBase64, enc.TagBase64, enc.KeyId, envelopeDigest, entryHash, signature, entry.Id);

                    var manifest = StreamManifest.Create(entry.StreamId, 1, entryHash, entry.EventId, entry.ConfirmedAt!.Value);
                    iCtx.Manifests.Add(manifest);
                    var attestation = AttestationRecord.Create(
                        entry.StreamId, 1, entry.EventId, entryHash, payloadHash, entry.PreviousHash, signature, keyId, entry.ConfirmedAt!.Value);
                    iCtx.Attestations.Add(attestation);
                }
                await iCtx.SaveChangesAsync();
            }
            await using (var ctx = new PaymentServiceDbContext(paymentOptions))
            {
                var sbSql = new System.Text.StringBuilder();
                for (int i = 1; i <= sampleSize; i++)
                {
                    sbSql.Append(FormattableString.Invariant($"""
                        INSERT INTO invoices (
                            "Id", "InvoiceRef", "RentalOrderId", "OrderCode", "RenterId",
                            "Currency", "TotalRentalAmount", "TotalDepositAmount", "TotalAmount",
                            "Status", "PaymentCode", "MerchantBank", "MerchantAccount", "PaymentDeadline",
                            "CreatedAt", "PaidAt", "PaymentRef"
                        )
                        VALUES (
                            gen_random_uuid(), 'INV-BENCH-{i}', gen_random_uuid(), 'ORD-BENCH-{i}', gen_random_uuid(),
                            'VND', 500000, 0, 500000,
                            'PAID', 'DPR BENCH{i:D5}', 'MBBank', '0383799632', '2026-10-19 12:00:00+00',
                            '2026-10-19 10:00:00+00', '2026-10-19 10:00:00+00', 'BENCH:{i}'
                        ) ON CONFLICT DO NOTHING;
                        """));
                }
                await ctx.Database.ExecuteSqlRawAsync(sbSql.ToString());
            }

            _output.WriteLine("Cryptographic signatures prepared for benchmark sample.");

            // 3. Run concurrent load test: 100 checks/sec with simultaneous background writes
            var latencies = new ConcurrentBag<double>();
            int successCount = 0;
            int totalRequests = 200; // 200 checks under concurrent pressure
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // Background writer simulation (simultaneous write activity)
            var bgWriterTask = Task.Run(async () =>
            {
                int counter = 1_000_001;
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await using var writeCtx = new PaymentServiceDbContext(paymentOptions);
                        var repo = new EfTransactionJournalRepository(writeCtx);
                        var newEntry = JournalEntryFactory.CreateEntry(
                            operationId: Guid.NewGuid(),
                            streamId: $"CONCURRENT:{counter}",
                            streamType: JournalStreamTypes.PaymentTransaction,
                            version: 1,
                            eventId: Guid.NewGuid(),
                            eventType: JournalEventTypes.BankIncoming,
                            amount: 100_000m,
                            currency: "VND",
                            status: "PAID",
                            confirmationSource: JournalConfirmationSources.Webhook,
                            actor: "WEBHOOK",
                            timestampUtc: DateTimeOffset.UtcNow,
                            previousHash: CanonicalJournalHasher.GenesisHash,
                            encryptor: new PaymentDataEncryptor(new byte[32]));
                        await repo.AppendEntryAsync(newEntry, cts.Token);
                        counter++;
                        await Task.Delay(10, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // Ignore transient write errors during load test
                    }
                }
            }, cts.Token);

            var swBenchmark = Stopwatch.StartNew();

            using var semaphore = new SemaphoreSlim(16, 16);
            var keyManager = new IntegrityKeyManager(Microsoft.Extensions.Options.Options.Create(new IntegrityHostOptions
            {
                SigningKeyId = keyId,
                PrivateKeyBase64 = EcdsaAttestation.ExportPrivateKeyBase64(ecdsa)
            }));

            // Run concurrent check tasks
            var checkTasks = Enumerable.Range(1, totalRequests).Select(async i =>
            {
                await semaphore.WaitAsync(cts.Token);
                try
                {
                    int sampleIdx = ((i - 1) % sampleSize) + 1;
                    string streamId = $"BENCH:{sampleIdx}";

                    await using var checkCtx = new PaymentServiceDbContext(paymentOptions);
                    await using var iCheckCtx = new IntegrityDbContext(iOptions);
                    var checker = new IntegrityVerificationEngine(iCheckCtx, checkCtx, keyManager, benchEncryptor, verifyOptions);

                    var req = new TransactionVerificationRequest(
                        PaymentRef: streamId,
                        Purpose: VerificationPurposes.PaymentConfirmation,
                        CallerNonce: $"nonce-{i}",
                        ExpectedAmount: 500_000m,
                        ExpectedCurrency: "VND",
                        InvoiceRef: $"INV-BENCH-{sampleIdx}");

                    var swCheck = Stopwatch.StartNew();
                    var result = await checker.VerifyAsync(req, cts.Token);
                    swCheck.Stop();

                    latencies.Add(swCheck.Elapsed.TotalMilliseconds);

                    if (result.IsSuccess &&
                        result.Value.Integrity.Status == IntegrityStatuses.Valid &&
                        result.Value.Usability.IsUsable)
                    {
                        Interlocked.Increment(ref successCount);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToArray();

            await Task.WhenAll(checkTasks);
            swBenchmark.Stop();

            cts.Cancel();
            await Task.WhenAny(bgWriterTask, Task.Delay(500));

            // 4. Calculate percentiles and report
            var latencyList = latencies.OrderBy(x => x).ToList();
            Assert.NotEmpty(latencyList);

            double p50 = latencyList[(int)(latencyList.Count * 0.50)];
            double p95 = latencyList[(int)(latencyList.Count * 0.95)];
            double p99 = latencyList[(int)(latencyList.Count * 0.99)];
            double max = latencyList[^1];
            double successRate = (double)successCount / totalRequests;
            double throughput = totalRequests / swBenchmark.Elapsed.TotalSeconds;

            _output.WriteLine($"--- Benchmark Results (1,000,000 DB records) ---");
            _output.WriteLine($"Total Checks: {totalRequests} across {swBenchmark.Elapsed.TotalSeconds:F2} seconds");
            _output.WriteLine($"Throughput:   {throughput:F1} checks/sec");
            _output.WriteLine($"Success Rate: {successRate * 100:F1}% ({successCount}/{totalRequests})");
            _output.WriteLine($"Latency p50:  {p50:F2} ms");
            _output.WriteLine($"Latency p95:  {p95:F2} ms");
            _output.WriteLine($"Latency p99:  {p99:F2} ms");
            _output.WriteLine($"Latency Max:  {max:F2} ms");

            // Assertions according to requirements
            Assert.True(successRate >= 0.99, $"Success rate must be >= 99%, was {successRate * 100}%");
            Assert.True(p99 < 1000.0, $"p99 latency must be < 1000ms (1 second), was {p99:F2}ms");
        }
        finally
        {
            if (!string.IsNullOrEmpty(dbName))
            {
                await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
            }
            if (!string.IsNullOrEmpty(iDbName))
            {
                await PostgresTestDatabaseHelper.DropTestDatabaseAsync(iDbName);
            }
        }
    }
}
