using System.Text.Json;
using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.DTOs.Events;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Repositories;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class PostgresMigrationAndPersistenceTests
{
    [Fact]
    public async Task Migrations_ApplyCleanly_AndVerifySchemaConstraints()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var ctx = new PaymentServiceDbContext(options))
            {
                await ctx.Database.MigrateAsync();
            }

            // Verify tables and columns in PostgreSQL information_schema
            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync();

            await using var cmdTables = conn.CreateCommand();
            cmdTables.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' ORDER BY table_name;";
            var tables = new List<string>();
            await using (var reader = await cmdTables.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            Assert.Contains("invoices", tables);
            Assert.Contains("invoice_items", tables);
            Assert.Contains("operation_receipts", tables);
            Assert.Contains("outbox_messages", tables);
            Assert.Contains("rental_item_settlements", tables);
            Assert.Contains("settlement_ledger_entries", tables);
            Assert.Contains("transaction_journal", tables);

            // Verify numeric(18,0) column data type
            await using var cmdNumeric = conn.CreateCommand();
            cmdNumeric.CommandText = @"
                SELECT column_name, data_type, numeric_precision, numeric_scale
                FROM information_schema.columns
                WHERE table_name = 'invoices' AND column_name IN ('TotalRentalAmount', 'TotalDepositAmount', 'TotalAmount');";

            await using (var reader = await cmdNumeric.ExecuteReaderAsync())
            {
                int count = 0;
                while (await reader.ReadAsync())
                {
                    count++;
                    Assert.Equal("numeric", reader.GetString(1));
                    Assert.Equal(18, reader.GetInt32(2));
                    Assert.Equal(0, reader.GetInt32(3));
                }
                Assert.Equal(3, count);
            }

            // Verify unique indexes in pg_indexes
            await using var cmdIndexes = conn.CreateCommand();
            cmdIndexes.CommandText = "SELECT indexname FROM pg_indexes WHERE schemaname = 'public';";
            var indexes = new List<string>();
            await using (var reader = await cmdIndexes.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    indexes.Add(reader.GetString(0));
                }
            }

            Assert.Contains("IX_invoices_InvoiceRef", indexes);
            Assert.Contains("IX_invoices_RentalOrderId", indexes);
            Assert.Contains("IX_invoices_PaymentCode", indexes);
            Assert.Contains("IX_invoices_PaymentRef", indexes);
            Assert.Contains("IX_operation_receipts_OperationKey", indexes);
            Assert.Contains("IX_invoice_items_InvoiceId_RentalItemId", indexes);
            Assert.Contains("IX_rental_item_settlements_OperationKey", indexes);
            Assert.Contains("IX_rental_item_settlements_RentalItemId", indexes);
            Assert.Contains("IX_settlement_ledger_entries_SettlementId_EntryType", indexes);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Migration_StopsAndReportsInvoices_WhenLegacyDuplicatePaymentRefExists()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new PaymentServiceDbContext(options);
            var migrator = ctx.Database.GetService<IMigrator>();

            // 1. Migrate only up to InitialCreate
            await migrator.MigrateAsync("20261012094959_InitialCreate");

            // 2. Seed two paid invoices sharing the same PaymentRef
            await using (var conn = new NpgsqlConnection(connString))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO invoices (
                        ""Id"", ""InvoiceRef"", ""RentalOrderId"", ""OrderCode"", ""RenterId"", ""Currency"",
                        ""TotalRentalAmount"", ""TotalDepositAmount"", ""TotalAmount"", ""Status"",
                        ""PaymentCode"", ""MerchantBank"", ""MerchantAccount"",
                        ""PaymentDeadline"", ""CreatedAt"", ""PaidAt"", ""PaymentRef""
                    ) VALUES
                    (
                        gen_random_uuid(), 'INV-LEGACY-DUP-1', gen_random_uuid(), 'ORD-DUP-1', gen_random_uuid(), 'VND',
                        100000, 0, 100000, 'PAID',
                        'DPR 23456789AA', 'MBBank', '0383799632',
                        NOW() + INTERVAL '30 minutes', NOW(), NOW(), 'PAY-DUP-LEGACY'
                    ),
                    (
                        gen_random_uuid(), 'INV-LEGACY-DUP-2', gen_random_uuid(), 'ORD-DUP-2', gen_random_uuid(), 'VND',
                        200000, 0, 200000, 'PAID',
                        'DPR 23456789BB', 'MBBank', '0383799632',
                        NOW() + INTERVAL '30 minutes', NOW(), NOW(), 'PAY-DUP-LEGACY'
                    );";
                await cmd.ExecuteNonQueryAsync();
            }

            // 3. Attempting to migrate to latest must fail and report the conflicting PaymentRef and InvoiceRefs
            var ex = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());
            Assert.Contains("PAY-DUP-LEGACY", ex.MessageText, StringComparison.Ordinal);
            Assert.Contains("INV-LEGACY-DUP-1", ex.MessageText, StringComparison.Ordinal);
            Assert.Contains("INV-LEGACY-DUP-2", ex.MessageText, StringComparison.Ordinal);

            // 4. Verify financial data was not altered
            await using (var verifyConn = new NpgsqlConnection(connString))
            {
                await verifyConn.OpenAsync();
                await using var verifyCmd = verifyConn.CreateCommand();
                verifyCmd.CommandText = @"SELECT COUNT(*) FROM invoices WHERE ""PaymentRef"" = 'PAY-DUP-LEGACY';";
                long count = (long)(await verifyCmd.ExecuteScalarAsync())!;
                Assert.Equal(2L, count);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Migration_BackfillsNullCorrelationIdOnUnpublishedOutbox_PreservingFinancialData()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new PaymentServiceDbContext(options);
            var migrator = ctx.Database.GetService<IMigrator>();

            // 1. Migrate only up to InitialCreate
            await migrator.MigrateAsync("20261012094959_InitialCreate");

            Guid unpublishedMsgId = Guid.NewGuid();
            Guid publishedMsgId = Guid.NewGuid();
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();

            string legacyPayloadUnpublished = $$"""
            {
              "messageId": "{{unpublishedMsgId}}",
              "messageType": "InvoicePaid",
              "version": 1,
              "producer": "payment-service",
              "aggregateId": "{{orderId}}",
              "occurredAt": "2026-10-12T10:00:00Z",
              "correlationId": null,
              "causationId": null,
              "payload": {
                "rentalOrderId": "{{orderId}}",
                "renterId": "{{renterId}}",
                "invoiceRef": "INV-BACKFILL-001",
                "paymentRef": "PAY-BACKFILL-001",
                "paidAmount": 1500000,
                "currency": "VND",
                "paidAt": "2026-10-12T10:00:00Z"
              }
            }
            """;

            string legacyPayloadPublished = $$"""
            {
              "messageId": "{{publishedMsgId}}",
              "messageType": "InvoicePaid",
              "version": 1,
              "producer": "payment-service",
              "aggregateId": "{{orderId}}",
              "occurredAt": "2026-10-12T09:00:00Z",
              "correlationId": null,
              "causationId": null,
              "payload": {
                "rentalOrderId": "{{orderId}}",
                "renterId": "{{renterId}}",
                "invoiceRef": "INV-BACKFILL-002",
                "paymentRef": "PAY-BACKFILL-002",
                "paidAmount": 500000,
                "currency": "VND",
                "paidAt": "2026-10-12T09:00:00Z"
              }
            }
            """;

            await using (var conn = new NpgsqlConnection(connString))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO outbox_messages (""Id"", ""MessageId"", ""MessageType"", ""AggregateType"", ""AggregateId"", ""Payload"", ""CreatedAt"", ""PublishedAt"")
                    VALUES
                    (gen_random_uuid(), @uMsgId, 'InvoicePaid', 'RentalOrder', @aggId, @uPayload, NOW(), NULL),
                    (gen_random_uuid(), @pMsgId, 'InvoicePaid', 'RentalOrder', @aggId, @pPayload, NOW(), NOW());";
                cmd.Parameters.AddWithValue("uMsgId", unpublishedMsgId);
                cmd.Parameters.AddWithValue("pMsgId", publishedMsgId);
                cmd.Parameters.AddWithValue("aggId", orderId.ToString("D"));
                cmd.Parameters.AddWithValue("uPayload", legacyPayloadUnpublished);
                cmd.Parameters.AddWithValue("pPayload", legacyPayloadPublished);
                await cmd.ExecuteNonQueryAsync();
            }

            // 2. Apply latest migration
            await migrator.MigrateAsync();

            // 3. Verify unpublished outbox has correlationId == MessageId and financial payload intact
            var unpublishedRow = await ctx.OutboxMessages.AsNoTracking().SingleAsync(o => o.MessageId == unpublishedMsgId);
            PostgresIdempotencyAndConcurrencyTests.AssertValidInvoicePaidOutboxJson(unpublishedRow.Payload);
            var env = JsonSerializer.Deserialize<PaymentEventEnvelope<InvoicePaidPayload>>(unpublishedRow.Payload);
            Assert.NotNull(env);
            Assert.Equal(unpublishedMsgId.ToString("D"), env.CorrelationId);
            Assert.Equal(1_500_000m, env.Payload.PaidAmount);
            Assert.Equal("INV-BACKFILL-001", env.Payload.InvoiceRef);
            Assert.Equal("PAY-BACKFILL-001", env.Payload.PaymentRef);

            // 4. Verify already-published outbox was not modified
            var publishedRow = await ctx.OutboxMessages.AsNoTracking().SingleAsync(o => o.MessageId == publishedMsgId);
            using var doc = JsonDocument.Parse(publishedRow.Payload);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("correlationId").ValueKind);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task PersistedInvoice_SurvivesRestart_WithImmutableSnapshot()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            Guid invoiceId;
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid supplierId = Guid.NewGuid();
            DateTimeOffset now = DateTimeOffset.UtcNow;

            await using (var ctx = new PaymentServiceDbContext(options))
            {
                await ctx.Database.MigrateAsync();

                var item = InvoiceItem.Create(Guid.NewGuid(), supplierId, 500_000m, 1_000_000m).Value;
                var invoice = Invoice.Create(
                    orderId, "ORD-RESTART-001", renterId, "VND", now.AddMinutes(30),
                    [item], "DPR ABC234XYZ9", "MBBank", "0383799632", now).Value;

                ctx.Invoices.Add(invoice);
                await ctx.SaveChangesAsync();
                invoiceId = invoice.Id;
            }

            // Simulate application restart with a fresh DbContext instance
            await using (var freshCtx = new PaymentServiceDbContext(options))
            {
                var loaded = await freshCtx.Invoices
                    .Include(i => i.Items)
                    .FirstOrDefaultAsync(i => i.Id == invoiceId);

                Assert.NotNull(loaded);
                Assert.Equal(orderId, loaded.RentalOrderId);
                Assert.Equal(renterId, loaded.RenterId);
                Assert.Equal(500_000m, loaded.TotalRentalAmount);
                Assert.Equal(1_000_000m, loaded.TotalDepositAmount);
                Assert.Equal(1_500_000m, loaded.TotalAmount);
                Assert.Equal("MBBank", loaded.MerchantBank);
                Assert.Equal("0383799632", loaded.MerchantAccount);
                Assert.Equal("DPR ABC234XYZ9", loaded.PaymentCode);
                Assert.Single(loaded.Items);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }
}
