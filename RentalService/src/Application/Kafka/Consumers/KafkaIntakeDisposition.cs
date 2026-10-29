namespace Dprental.RentalService.Application.Kafka.Consumers;

/// <remarks>
/// Kết quả tiếp nhận để consumer biết có thể commit offset; không mô tả trạng thái xử lý nghiệp vụ.
/// InboxStored là đã lưu để xử lý sau, Duplicate là bản tin đã nhận, DlqStored là đã lưu bản tin lỗi, Ignored là loại không xử lý.
/// </remarks>
public enum KafkaIntakeDisposition
{
    InboxStored,
    Duplicate,
    DlqStored,
    Ignored
}
