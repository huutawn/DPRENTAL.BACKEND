using System.Diagnostics.Metrics;

namespace Dprental.RentalService.Application.Observability;

public sealed class RentalMetrics
{
    public const string MeterName = "Dprental.RentalService";

    private readonly Counter<long> _operationsRetriedCounter;
    private readonly Counter<long> _operationsReconciledCounter;
    private readonly Counter<long> _dlqMessagesCounter;
    private readonly Counter<long> _releaseFailuresCounter;
    private readonly Counter<long> _refundFailuresCounter;
    private readonly Counter<long> _uncertainExhaustedCounter;

    public RentalMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _operationsRetriedCounter = meter.CreateCounter<long>(
            "rental.operations.retries",
            unit: "{retries}",
            description: "Số lần thử lại các tác vụ nghiệp vụ");

        _operationsReconciledCounter = meter.CreateCounter<long>(
            "rental.operations.reconciled",
            unit: "{reconciled}",
            description: "Số tác vụ được phục hồi/reconcile thành công");

        _dlqMessagesCounter = meter.CreateCounter<long>(
            "rental.dlq.messages",
            unit: "{messages}",
            description: "Số tin nhắn chuyển vào Dead Letter Queue");

        _releaseFailuresCounter = meter.CreateCounter<long>(
            "rental.release.failures",
            unit: "{failures}",
            description: "Số lần giải phóng allocation thất bại");

        _refundFailuresCounter = meter.CreateCounter<long>(
            "rental.refund.failures",
            unit: "{failures}",
            description: "Số lần yêu cầu hoàn tiền thất bại");

        _uncertainExhaustedCounter = meter.CreateCounter<long>(
            "rental.operations.uncertain_exhausted",
            unit: "{operations}",
            description: "Số tác vụ hết lượt thử lại nhưng trạng thái chưa rõ kết quả");
    }

    public void RecordRetry(string action) => _operationsRetriedCounter.Add(1, new KeyValuePair<string, object?>("action", action));
    public void RecordReconciliation(string action) => _operationsReconciledCounter.Add(1, new KeyValuePair<string, object?>("action", action));
    public void RecordDlqMessage(string topic) => _dlqMessagesCounter.Add(1, new KeyValuePair<string, object?>("topic", topic));
    public void RecordReleaseFailure() => _releaseFailuresCounter.Add(1);
    public void RecordRefundFailure() => _refundFailuresCounter.Add(1);
    public void RecordUncertainExhausted(string action) => _uncertainExhaustedCounter.Add(1, new KeyValuePair<string, object?>("action", action));
}
