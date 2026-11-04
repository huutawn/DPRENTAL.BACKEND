using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;

namespace Dprental.PaymentService.Api.Tests.TestHelpers;

public sealed class FakeManualRefundRepository : IManualRefundRepository
{
    private readonly List<ManualRefundRequirement> _refunds = [];

    public Task<ManualRefundRequirement?> FindByOperationKeyAsync(string operationKey, CancellationToken ct = default) =>
        Task.FromResult(_refunds.Find(r => r.OperationKey == operationKey));

    public Task<ManualRefundRequirement?> FindAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_refunds.Find(r => r.Id == id));

    public Task<ManualRefundRequirement?> FindWithRowLockAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_refunds.Find(r => r.Id == id));

    public Task<IReadOnlyList<ManualRefundRequirement>> GetForInvoiceAsync(Guid invoiceId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ManualRefundRequirement>>(_refunds.FindAll(r => r.InvoiceId == invoiceId));

    public Task<IReadOnlyList<ManualRefundRequirement>> ListAsync(ManualRefundStatus status, Guid? after, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ManualRefundRequirement>>(_refunds.FindAll(r => r.Status == status));

    public Task<OutgoingTransferTarget?> FindByTransferCodeAsync(string transferCode, CancellationToken ct = default) =>
        Task.FromResult(_refunds.Find(r => r.TransferCode == transferCode) is { } r
            ? new OutgoingTransferTarget(r.Id, r.Amount, r.Status == ManualRefundStatus.PendingManualRefund)
            : null);

    public Task<bool> TransferReferenceExistsAsync(string reference, Guid exceptId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task AddAsync(ManualRefundRequirement refund, CancellationToken ct = default)
    {
        _refunds.Add(refund);
        return Task.CompletedTask;
    }
}
