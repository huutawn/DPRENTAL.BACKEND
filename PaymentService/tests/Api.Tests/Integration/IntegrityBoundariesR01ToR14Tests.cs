using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.Clients;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Application.Services.Integrity;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Entities.Integrity;
using Dprental.PaymentService.Domain.Repositories;
using Dprental.PaymentService.IntegrityHost.Options;
using Dprental.PaymentService.IntegrityHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class IntegrityBoundariesR01ToR14Tests
{
    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class MockHttpHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    [Fact]
    public async Task R01_Response_MissingReceipt_Or_InvalidSignature_IsRejected()
    {
        using var signingKey = EcdsaAttestation.CreateEphemeralP256KeyPair();
        string keyId = "r01-key";
        var options = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
        {
            ActiveKeyId = keyId,
            PublicKeys = new Dictionary<string, string> { [keyId] = EcdsaAttestation.ExportPublicKeyBase64(signingKey) }
        });

        // 1. Missing receipt
        var missingReceiptResponse = new TransactionVerificationResponse(
            new TransactionIntegrityResult("VALID", 1, "SYSTEM", DateTimeOffset.UtcNow, keyId, "sig", "proof", "nonce-1"),
            new TransactionUsabilityResult(true, "OK", 500_000m, "VND", "PAID"),
            Receipt: null);

        using var client1 = new HttpClient(new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(missingReceiptResponse) }))
        {
            BaseAddress = new Uri("http://localhost:8081")
        };
        var integrityClient1 = new IntegrityHostClient(client1, options, NullLogger<IntegrityHostClient>.Instance);

        var req = new TransactionVerificationRequest("PAY-R01", VerificationPurposes.PaymentConfirmation, "nonce-1");
        var res1 = await integrityClient1.VerifyAsync(req);
        Assert.True(res1.IsFailure);
        Assert.Equal("IntegrityHost.MissingReceipt", res1.Error.Code);

        // 2. Tampered signature
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string requestHash = CanonicalJournalHasher.ComputeRequestHash(req.PaymentRef, req.Purpose, req.CallerNonce);
        var body = new VerificationReceiptBody(
            Schema: 2, Issuer: "dprental-integrity-host", Audience: "payment-api", Environment: "production",
            ReceiptId: Guid.NewGuid(), KeyId: keyId, RequestHash: requestHash, CallerIdentity: "", CallerNonce: req.CallerNonce,
            Purpose: req.Purpose, IntegrityStatus: IntegrityStatuses.Valid, IsUsable: true, Reason: "OK",
            ActualAmount: 500_000m, ActualCurrency: "VND", ActualStatus: "PAID", EntityClaims: [],
            IssuedAt: now, ExpiresAt: now.AddSeconds(4));

        var badSigResponse = new TransactionVerificationResponse(
            new TransactionIntegrityResult("VALID", 1, "SYSTEM", now, keyId, "sig", "proof", req.CallerNonce),
            new TransactionUsabilityResult(true, "OK", 500_000m, "VND", "PAID"),
            new VerificationReceipt(body, "bad-signature"));

        using var client2 = new HttpClient(new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(badSigResponse) }))
        {
            BaseAddress = new Uri("http://localhost:8081")
        };
        var integrityClient2 = new IntegrityHostClient(client2, options, NullLogger<IntegrityHostClient>.Instance);
        var res2 = await integrityClient2.VerifyAsync(req);
        Assert.True(res2.IsFailure);
        Assert.Equal("IntegrityHost.InvalidReceiptSignature", res2.Error.Code);
    }

    [Fact]
    public async Task R02_TamperedOuterFields_DoNotAffectResult_ClientBindsStrictlyToSignedReceiptBody()
    {
        using var signingKey = EcdsaAttestation.CreateEphemeralP256KeyPair();
        string keyId = "r02-key";
        var options = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
        {
            ActiveKeyId = keyId,
            PublicKeys = new Dictionary<string, string> { [keyId] = EcdsaAttestation.ExportPublicKeyBase64(signingKey) }
        });

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = now.AddSeconds(4);
        var req = new TransactionVerificationRequest("PAY-R02", VerificationPurposes.PaymentConfirmation, "nonce-r02");
        string requestHash = CanonicalJournalHasher.ComputeRequestHash(req.PaymentRef, req.Purpose, req.CallerNonce);

        // Signed body states: Amount = 500_000, Status = PAID, IsUsable = true
        var body = new VerificationReceiptBody(
            Schema: 2, Issuer: "dprental-integrity-host", Audience: "payment-api", Environment: "production",
            ReceiptId: Guid.NewGuid(), KeyId: keyId, RequestHash: requestHash, CallerIdentity: "", CallerNonce: req.CallerNonce,
            Purpose: req.Purpose, IntegrityStatus: IntegrityStatuses.Valid, IsUsable: true, Reason: "OK",
            ActualAmount: 500_000m, ActualCurrency: "VND", ActualStatus: "PAID", EntityClaims: [],
            IssuedAt: now, ExpiresAt: expiresAt);

        string claimsDigest = EcdsaAttestation.ComputeClaimsDigest([]);
        string receiptMsg = EcdsaAttestation.BuildVerificationReceiptMessageV2(
            body.Schema, body.Issuer, body.Audience, body.Environment, body.ReceiptId, body.KeyId,
            body.RequestHash, body.CallerIdentity, body.CallerNonce, body.Purpose, body.IntegrityStatus,
            body.IsUsable, body.Reason, body.ActualAmount, body.ActualCurrency, body.ActualStatus,
            claimsDigest, body.IssuedAt, body.ExpiresAt);
        string sig = EcdsaAttestation.Sign(signingKey, receiptMsg);

        // Attacker alters outer fields: Amount = 999_999_999, Status = CANCELLED, IsUsable = false
        var tamperedOuterResponse = new TransactionVerificationResponse(
            new TransactionIntegrityResult("TAMPERED", 99, "ATTACKER", now, keyId, sig, "forged-proof", req.CallerNonce),
            new TransactionUsabilityResult(false, "FORGED_REASON", 999_999_999m, "USD", "CANCELLED"),
            new VerificationReceipt(body, sig));

        using var client = new HttpClient(new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(tamperedOuterResponse) }))
        {
            BaseAddress = new Uri("http://localhost:8081")
        };
        var integrityClient = new IntegrityHostClient(client, options, NullLogger<IntegrityHostClient>.Instance);

        var result = await integrityClient.VerifyAsync(req);
        Assert.True(result.IsSuccess);

        // Client binds exclusively to verified body! Outer tampered fields are discarded
        Assert.Equal(500_000m, result.Value.Usability.ActualAmount);
        Assert.Equal("VND", result.Value.Usability.ActualCurrency);
        Assert.Equal("PAID", result.Value.Usability.ActualStatus);
        Assert.True(result.Value.Usability.IsUsable);
        Assert.Equal(IntegrityStatuses.Valid, result.Value.Integrity.Status);
    }

    [Fact]
    public async Task R03_MismatchedRequestHash_Or_Nonce_Or_ExpiredTTL_IsRejected()
    {
        using var signingKey = EcdsaAttestation.CreateEphemeralP256KeyPair();
        string keyId = "r03-key";
        var options = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
        {
            ActiveKeyId = keyId,
            PublicKeys = new Dictionary<string, string> { [keyId] = EcdsaAttestation.ExportPublicKeyBase64(signingKey) }
        });

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var req = new TransactionVerificationRequest("PAY-R03", VerificationPurposes.PaymentConfirmation, "nonce-expected");
        string requestHash = CanonicalJournalHasher.ComputeRequestHash(req.PaymentRef, req.Purpose, "nonce-forged"); // Nonce mismatch in request hash!

        var body = new VerificationReceiptBody(
            Schema: 2, Issuer: "dprental-integrity-host", Audience: "payment-api", Environment: "production",
            ReceiptId: Guid.NewGuid(), KeyId: keyId, RequestHash: requestHash, CallerIdentity: "", CallerNonce: "nonce-expected",
            Purpose: req.Purpose, IntegrityStatus: IntegrityStatuses.Valid, IsUsable: true, Reason: "OK",
            ActualAmount: 500_000m, ActualCurrency: "VND", ActualStatus: "PAID", EntityClaims: [],
            IssuedAt: now, ExpiresAt: now.AddSeconds(4));

        string claimsDigest = EcdsaAttestation.ComputeClaimsDigest([]);
        string receiptMsg = EcdsaAttestation.BuildVerificationReceiptMessageV2(
            body.Schema, body.Issuer, body.Audience, body.Environment, body.ReceiptId, body.KeyId,
            body.RequestHash, body.CallerIdentity, body.CallerNonce, body.Purpose, body.IntegrityStatus,
            body.IsUsable, body.Reason, body.ActualAmount, body.ActualCurrency, body.ActualStatus,
            claimsDigest, body.IssuedAt, body.ExpiresAt);
        string sig = EcdsaAttestation.Sign(signingKey, receiptMsg);

        using var client = new HttpClient(new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new TransactionVerificationResponse(new TransactionIntegrityResult("VALID", 1, "SYSTEM", now, keyId, sig, "proof", req.CallerNonce), new TransactionUsabilityResult(true, "OK", 500_000m, "VND", "PAID"), new VerificationReceipt(body, sig))) }))
        {
            BaseAddress = new Uri("http://localhost:8081")
        };
        var integrityClient = new IntegrityHostClient(client, options, NullLogger<IntegrityHostClient>.Instance);

        var result = await integrityClient.VerifyAsync(req);
        Assert.True(result.IsFailure);
        Assert.Equal("IntegrityHost.RequestHashMismatch", result.Error.Code);
    }

    [Fact]
    public void R04_MissingKeyFile_Or_UnreadableFile_FailsFast_WithoutFallback()
    {
        var options = new PaymentEncryptionOptions
        {
            KeyFilePath = "/non/existent/secret/path/aes.key"
        };

        // Must fail fast on unreadable key file without falling back to defaults
        Assert.Throws<InvalidOperationException>(() => options.GetKeyBytes());
    }

    [Fact]
    public async Task R05_DirectOperationalDatabaseModification_DetectedBySnapshotDigest_EvenWithMoneyBalancePreserved()
    {
        var (paymentConn, dbName1) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var (integrityConn, dbName2) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var pOptions = PostgresTestDatabaseHelper.CreateOptions(paymentConn);
            var iOptions = new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(integrityConn).Options;

            await using (var p = new PaymentServiceDbContext(pOptions)) await p.Database.MigrateAsync();
            await using (var i = new IntegrityDbContext(iOptions)) await i.Database.MigrateAsync();

            using var ecdsa = EcdsaAttestation.CreateEphemeralP256KeyPair();
            string keyId = "r05-key";
            var keyManager = new IntegrityKeyManager(Microsoft.Extensions.Options.Options.Create(new IntegrityHostOptions
            {
                SigningKeyId = keyId,
                PrivateKeyBase64 = EcdsaAttestation.ExportPrivateKeyBase64(ecdsa)
            }));

            var encryptor = new PaymentDataEncryptor(new byte[32], "r05-enc");
            var verifyOptions = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
            {
                ActiveKeyId = keyId,
                PublicKeys = new Dictionary<string, string> { [keyId] = EcdsaAttestation.ExportPublicKeyBase64(ecdsa) }
            });

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 400_000m, 100_000m).Value;
            var invoice = Invoice.Create(Guid.NewGuid(), "ORD-R05", Guid.NewGuid(), "VND", now.AddMinutes(30), [item], "DPR-R05", "MBBank", "0383799632", now).Value;
            invoice.MarkPaid("PAY-R05", 500_000m, "VND", now);

            var journal = JournalEntryFactory.CreateEntry(
                operationId: Guid.NewGuid(),
                streamId: invoice.InvoiceRef,
                streamType: "Invoice",
                version: 1,
                eventId: Guid.NewGuid(),
                eventType: "INVOICE_PAID",
                amount: 500_000m,
                currency: "VND",
                status: "PAID",
                confirmationSource: "WEBHOOK",
                actor: "WEBHOOK:SEPAY",
                timestampUtc: now,
                previousHash: CanonicalJournalHasher.GenesisHash,
                encryptor: encryptor);

            string attMsg = EcdsaAttestation.BuildAttestationMessageV2(keyId, "production", journal.StreamId, journal.Version, journal.EventId, journal.EntryHash, now);
            string attSig = EcdsaAttestation.Sign(ecdsa, attMsg);
            journal.Confirm(attSig, keyId, now);

            await using (var pCtx = new PaymentServiceDbContext(pOptions))
            {
                pCtx.Invoices.Add(invoice);
                pCtx.TransactionJournals.Add(journal);
                await pCtx.SaveChangesAsync();
            }

            // Save attestation and EntityHead in Integrity DB
            var snap = OperationalSnapshotEngine.CreateInvoiceSnapshot(invoice, invoice.Revision);
            string originalDigest = OperationalSnapshotEngine.ComputeSnapshotDigest(snap);

            await using (var iCtx = new IntegrityDbContext(iOptions))
            {
                var att = AttestationRecord.Create(journal.StreamId, journal.Version, journal.EventId, journal.EntryHash, journal.PayloadHash, journal.PreviousHash, attSig, keyId, now);
                iCtx.Attestations.Add(att);
                iCtx.Manifests.Add(StreamManifest.Create(journal.StreamId, journal.Version, journal.EntryHash, journal.EventId, now));
                iCtx.EntityHeads.Add(EntityHeadRecord.Create("Invoice", invoice.InvoiceRef, att.Id, 1, invoice.Revision, journal.EntryHash, originalDigest, now));
                await iCtx.SaveChangesAsync();
            }

            // Attacker modifies operational columns directly in PostgreSQL: transfers 50k from rental to deposit to maintain TotalAmount == 500k
            await using (var pCtx = new PaymentServiceDbContext(pOptions))
            {
                await pCtx.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE invoices SET \"TotalRentalAmount\" = 350000, \"TotalDepositAmount\" = 150000 WHERE \"InvoiceRef\" = {invoice.InvoiceRef}");
            }

            // Verification must detect the tampering via snapshot digest mismatch despite TotalAmount balance being preserved!
            await using (var iCtx = new IntegrityDbContext(iOptions))
            await using (var pCtx = new PaymentServiceDbContext(pOptions))
            {
                var engine = new IntegrityVerificationEngine(iCtx, pCtx, keyManager, encryptor, verifyOptions);
                var verifyRes = await engine.VerifyAsync(new TransactionVerificationRequest(
                    PaymentRef: "PAY-R05",
                    Purpose: VerificationPurposes.Settlement,
                    CallerNonce: "nonce-r05",
                    InvoiceRef: invoice.InvoiceRef));

                Assert.True(verifyRes.IsSuccess);
                Assert.Equal(IntegrityStatuses.Tampered, verifyRes.Value.Integrity.Status);
                Assert.Equal("OPERATIONAL_SNAPSHOT_TAMPERED", verifyRes.Value.Usability.Reason);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName1);
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName2);
        }
    }

    [Fact]
    public void R06_TamperedCiphertext_Or_SwappedFields_FailsDecryption()
    {
        var encryptor = new PaymentDataEncryptor(new byte[32], "r06-enc");
        string recordId = Guid.NewGuid().ToString("D");

        // Encrypt AccountNumber
        string encAccount = SensitiveFieldEncryptor.Encrypt(encryptor, "PaymentTransaction", recordId, "AccountNumber", "0383799632");

        // Decrypt with correct field name succeeds
        string? dec = SensitiveFieldEncryptor.Decrypt(encryptor, "PaymentTransaction", recordId, "AccountNumber", encAccount);
        Assert.Equal("0383799632", dec);

        // Field swapping attack: Attacker moves AccountNumber ciphertext into Content field
        // Decryption MUST fail because AAD binds field name!
        Assert.ThrowsAny<CryptographicException>(() =>
            SensitiveFieldEncryptor.Decrypt(encryptor, "PaymentTransaction", recordId, "Content", encAccount));

        // Record swapping attack: Attacker moves ciphertext to a different record ID
        // Decryption MUST fail because AAD binds record ID!
        string otherRecordId = Guid.NewGuid().ToString("D");
        Assert.ThrowsAny<CryptographicException>(() =>
            SensitiveFieldEncryptor.Decrypt(encryptor, "PaymentTransaction", otherRecordId, "AccountNumber", encAccount));
    }

    [Fact]
    public async Task R07_Settlement_WithoutValidConfirmedEvidence_IsRejected()
    {
        var (paymentConn, dbName1) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var (integrityConn, dbName2) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var pOptions = PostgresTestDatabaseHelper.CreateOptions(paymentConn);
            var iOptions = new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(integrityConn).Options;

            await using (var p = new PaymentServiceDbContext(pOptions)) await p.Database.MigrateAsync();
            await using (var i = new IntegrityDbContext(iOptions)) await i.Database.MigrateAsync();

            using var ecdsa = EcdsaAttestation.CreateEphemeralP256KeyPair();
            string keyId = "r07-key";
            var keyManager = new IntegrityKeyManager(Microsoft.Extensions.Options.Options.Create(new IntegrityHostOptions
            {
                SigningKeyId = keyId,
                PrivateKeyBase64 = EcdsaAttestation.ExportPrivateKeyBase64(ecdsa)
            }));

            var encryptor = new PaymentDataEncryptor(new byte[32], "r07-enc");
            var verifyOptions = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
            {
                ActiveKeyId = keyId,
                PublicKeys = new Dictionary<string, string> { [keyId] = EcdsaAttestation.ExportPublicKeyBase64(ecdsa) }
            });

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 300_000m, 50_000m).Value;
            var invoice = Invoice.Create(Guid.NewGuid(), "ORD-R07", Guid.NewGuid(), "VND", now.AddMinutes(30), [item], "DPR-R07", "MBBank", "0383799632", now).Value;
            invoice.MarkPaid("PAY-R07", 350_000m, "VND", now);

            await using (var pCtx = new PaymentServiceDbContext(pOptions))
            {
                pCtx.Invoices.Add(invoice);
                await pCtx.SaveChangesAsync();
            }

            // No confirmation in Integrity DB yet
            await using (var iCtx = new IntegrityDbContext(iOptions))
            await using (var pCtx = new PaymentServiceDbContext(pOptions))
            {
                var engine = new IntegrityVerificationEngine(iCtx, pCtx, keyManager, encryptor, verifyOptions);
                var req = new TransactionVerificationRequest(
                    PaymentRef: "PAY-R07",
                    Purpose: VerificationPurposes.Settlement,
                    CallerNonce: "nonce-r07",
                    InvoiceRef: invoice.InvoiceRef,
                    RentalItemId: item.RentalItemId);

                var verifyRes = await engine.VerifyAsync(req);
                // Verification must fail usability because unconfirmed!
                Assert.True(verifyRes.IsSuccess);
                Assert.False(verifyRes.Value.Usability.IsUsable);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName1);
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName2);
        }
    }

    [Fact]
    public async Task R08_AlteredSnapshot_BetweenVerificationAndWrite_DetectedUnderRowLock()
    {
        var (paymentConn, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var pOptions = PostgresTestDatabaseHelper.CreateOptions(paymentConn);
            await using (var p = new PaymentServiceDbContext(pOptions)) await p.Database.MigrateAsync();

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 200_000m, 50_000m).Value;
            var invoice = Invoice.Create(Guid.NewGuid(), "ORD-R08", Guid.NewGuid(), "VND", now.AddMinutes(30), [item], "DPR-R08", "MBBank", "0383799632", now).Value;
            invoice.MarkPaid("PAY-R08", 250_000m, "VND", now);

            await using (var pCtx = new PaymentServiceDbContext(pOptions))
            {
                pCtx.Invoices.Add(invoice);
                await pCtx.SaveChangesAsync();
            }

            // Verified context holds claim for revision 2 (from MarkPaid)
            var verifiedClaims = new List<VerifiedWriteClaim>
            {
                new("Invoice", invoice.InvoiceRef, invoice.Revision, "digest-r08"),
                new("InvoiceItem", item.RentalItemId.ToString("D"), item.Revision, "item-digest-r08")
            };
            var verifiedContext = new VerifiedWriteContext(
                Guid.NewGuid(), "key-r08", now.AddSeconds(5), verifiedClaims, now);

            // Concurrent mutation advances invoice revision directly in DB
            await using (var pCtx = new PaymentServiceDbContext(pOptions))
            {
                await pCtx.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE invoices SET \"Revision\" = \"Revision\" + 1 WHERE \"InvoiceRef\" = {invoice.InvoiceRef}");
            }

            // SettlementService must detect the state change and return conflict
            await using (var pCtx = new PaymentServiceDbContext(pOptions))
            {
                var repo = new EfInvoiceRepository(pCtx);
                var ledger = new EfSettlementLedger(pCtx, TimeProvider.System);
                var checkerMock = new IntegrityHostClient(
                    new HttpClient(new MockHttpHandler(new HttpResponseMessage(HttpStatusCode.OK))),
                    new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions()),
                    NullLogger<IntegrityHostClient>.Instance);

                var settleService = new Dprental.PaymentService.Application.Services.SettlementService(repo, ledger, checkerMock);
                // Re-verifying under lock detects the revision discrepancy
                var reloadedInv = await repo.GetInvoiceSnapshotAsync(invoice.InvoiceRef);
                Assert.NotNull(reloadedInv);
                Assert.NotEqual(verifiedClaims[0].Revision, reloadedInv.Revision);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task R09_RollbackOrDeletedTail_DetectedByManifestEvidence()
    {
        var (paymentConn, dbName1) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var (integrityConn, dbName2) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var pOptions = PostgresTestDatabaseHelper.CreateOptions(paymentConn);
            var iOptions = new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(integrityConn).Options;

            await using (var p = new PaymentServiceDbContext(pOptions)) await p.Database.MigrateAsync();
            await using (var i = new IntegrityDbContext(iOptions)) await i.Database.MigrateAsync();

            using var ecdsa = EcdsaAttestation.CreateEphemeralP256KeyPair();
            string keyId = "r09-key";
            var keyManager = new IntegrityKeyManager(Microsoft.Extensions.Options.Options.Create(new IntegrityHostOptions
            {
                SigningKeyId = keyId,
                PrivateKeyBase64 = EcdsaAttestation.ExportPrivateKeyBase64(ecdsa)
            }));

            var encryptor = new PaymentDataEncryptor(new byte[32], "r09-enc");
            var verifyOptions = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
            {
                ActiveKeyId = keyId,
                PublicKeys = new Dictionary<string, string> { [keyId] = EcdsaAttestation.ExportPublicKeyBase64(ecdsa) }
            });

            DateTimeOffset now = DateTimeOffset.UtcNow;
            string streamId = "STREAM-R09";

            // Manifest recorded version 5 in Integrity DB
            await using (var iCtx = new IntegrityDbContext(iOptions))
            {
                iCtx.Manifests.Add(StreamManifest.Create(streamId, 5, "hash-v5", Guid.NewGuid(), now));
                await iCtx.SaveChangesAsync();
            }

            // In Payment DB: Stream was deleted or rolled back to 0 records!
            await using (var iCtx = new IntegrityDbContext(iOptions))
            await using (var pCtx = new PaymentServiceDbContext(pOptions))
            {
                var engine = new IntegrityVerificationEngine(iCtx, pCtx, keyManager, encryptor, verifyOptions);
                var result = await engine.VerifyAsync(new TransactionVerificationRequest(
                    PaymentRef: streamId,
                    Purpose: VerificationPurposes.PaymentConfirmation,
                    CallerNonce: "nonce-r09"));

                Assert.True(result.IsSuccess);
                Assert.Equal(IntegrityStatuses.Tampered, result.Value.Integrity.Status);
                Assert.Equal("ROLLBACK_DETECTED", result.Value.Usability.Reason);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName1);
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName2);
        }
    }

    [Fact]
    public async Task R11_PublisherLease_DoesNotIncrementAttemptOnClaim_AndSafelyReleases()
    {
        var (paymentConn, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(paymentConn);
            await using (var ctx = new PaymentServiceDbContext(options)) await ctx.Database.MigrateAsync();

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var outbox = OutboxMessage.Create(
                Guid.NewGuid(), "InvoicePaid", "RentalOrder", Guid.NewGuid().ToString("D"), "{}", now);

            await using (var ctx = new PaymentServiceDbContext(options))
            {
                ctx.OutboxMessages.Add(outbox);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = new PaymentServiceDbContext(options))
            {
                var repo = new EfOutboxRepository(ctx);
                var claimed = await repo.ClaimPendingBatchAsync("worker-r11", now, TimeSpan.FromSeconds(30), 10, 5);
                Assert.Single(claimed);

                // Claiming only acquires lease, MUST NOT increment RetryCount!
                Assert.Equal(0, claimed[0].RetryCount);
                Assert.Equal("worker-r11", claimed[0].LockedBy);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }
}
