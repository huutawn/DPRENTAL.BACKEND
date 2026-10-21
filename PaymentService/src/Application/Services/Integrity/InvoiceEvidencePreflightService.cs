#pragma warning disable CA1848
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dprental.PaymentService.Application.Services.Integrity;

public enum InvoiceEvidenceClassification
{
    Version1,
    Version2,
    PendingConfirmation,
    Indeterminate
}

public sealed record InvoicePreflightItem(
    string InvoiceRef,
    long Revision,
    InvoiceEvidenceClassification Classification,
    int? AssignedVersion,
    string? Reason);

public sealed record InvoicePreflightReport(
    int TotalInvoices,
    int Version1Count,
    int Version2Count,
    int PendingConfirmationCount,
    int IndeterminateCount,
    IReadOnlyList<InvoicePreflightItem> Items);

public interface IInvoiceEvidencePreflightService
{
    Task<InvoicePreflightReport> RunPreflightClassificationAsync(
        bool applyVersionUpdates = false,
        CancellationToken cancellationToken = default);
}

public sealed class InvoiceEvidencePreflightService : IInvoiceEvidencePreflightService
{
    private readonly PaymentServiceDbContext _paymentDb;
    private readonly IntegrityDbContext _integrityDb;
    private readonly ILogger<InvoiceEvidencePreflightService> _logger;

    public InvoiceEvidencePreflightService(
        PaymentServiceDbContext paymentDb,
        IntegrityDbContext integrityDb,
        ILogger<InvoiceEvidencePreflightService> logger)
    {
        _paymentDb = paymentDb ?? throw new ArgumentNullException(nameof(paymentDb));
        _integrityDb = integrityDb ?? throw new ArgumentNullException(nameof(integrityDb));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<InvoicePreflightReport> RunPreflightClassificationAsync(
        bool applyVersionUpdates = false,
        CancellationToken cancellationToken = default)
    {
        var invoices = await _paymentDb.Invoices
            .Include(i => i.Items)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(cancellationToken);

        var items = new List<InvoicePreflightItem>();
        int v1Count = 0;
        int v2Count = 0;
        int pendingCount = 0;
        int indetCount = 0;

        foreach (var inv in invoices)
        {
            var journal = await _paymentDb.TransactionJournals
                .AsNoTracking()
                .Where(j => j.StreamId == inv.InvoiceRef && j.StreamType == JournalStreamTypes.Invoice)
                .OrderByDescending(j => j.Version)
                .FirstOrDefaultAsync(cancellationToken);

            if (journal is null || !journal.IsConfirmed || string.IsNullOrWhiteSpace(journal.ProofSignature))
            {
                pendingCount++;
                items.Add(new InvoicePreflightItem(inv.InvoiceRef, inv.Revision, InvoiceEvidenceClassification.PendingConfirmation, null, "Awaiting journal confirmation"));
                continue;
            }

            var head = await _integrityDb.EntityHeads
                .AsNoTracking()
                .FirstOrDefaultAsync(h => h.EntityType == "Invoice" && h.EntityId == inv.InvoiceRef, cancellationToken);

            if (head is null)
            {
                pendingCount++;
                items.Add(new InvoicePreflightItem(inv.InvoiceRef, inv.Revision, InvoiceEvidenceClassification.PendingConfirmation, null, "EntityHead not found in evidence store"));
                continue;
            }

            // Compute digests for v1 and v2 formats
            var v1Snap = OperationalSnapshotEngine.CreateInvoiceSnapshot(inv, inv.Revision, 1);
            string v1Digest = OperationalSnapshotEngine.ComputeSnapshotDigest(v1Snap);

            var v2Snap = OperationalSnapshotEngine.CreateInvoiceSnapshot(inv, inv.Revision, 2);
            string v2Digest = OperationalSnapshotEngine.ComputeSnapshotDigest(v2Snap);

            if (string.Equals(head.LatestDigest, v2Digest, StringComparison.OrdinalIgnoreCase))
            {
                v2Count++;
                items.Add(new InvoicePreflightItem(inv.InvoiceRef, inv.Revision, InvoiceEvidenceClassification.Version2, 2, "Matches evidence v2"));
                if (applyVersionUpdates && inv.IntegritySnapshotVersion != 2)
                {
                    typeof(Invoice).GetProperty(nameof(Invoice.IntegritySnapshotVersion))!.SetValue(inv, 2);
                }
            }
            else if (string.Equals(head.LatestDigest, v1Digest, StringComparison.OrdinalIgnoreCase))
            {
                v1Count++;
                items.Add(new InvoicePreflightItem(inv.InvoiceRef, inv.Revision, InvoiceEvidenceClassification.Version1, 1, "Matches evidence v1"));
                if (applyVersionUpdates && inv.IntegritySnapshotVersion != 1)
                {
                    typeof(Invoice).GetProperty(nameof(Invoice.IntegritySnapshotVersion))!.SetValue(inv, 1);
                }
            }
            else
            {
                indetCount++;
                _logger.LogWarning("Invoice {InvoiceRef} revision {Revision} has indeterminate snapshot evidence (head digest: {HeadDigest}, v1: {V1Digest}, v2: {V2Digest})",
                    inv.InvoiceRef, inv.Revision, head.LatestDigest, v1Digest, v2Digest);
                items.Add(new InvoicePreflightItem(inv.InvoiceRef, inv.Revision, InvoiceEvidenceClassification.Indeterminate, null,
                    $"Evidence digest mismatch (Head: {head.LatestDigest}, V1: {v1Digest}, V2: {v2Digest})"));
            }
        }

        if (applyVersionUpdates)
        {
            await _paymentDb.SaveChangesAsync(cancellationToken);
        }

        return new InvoicePreflightReport(
            invoices.Count,
            v1Count,
            v2Count,
            pendingCount,
            indetCount,
            items);
    }
}
