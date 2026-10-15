namespace Dprental.RentalService.Application.Kafka.Consumers;

public enum KafkaIntakeDisposition
{
    InboxStored,
    Duplicate,
    DlqStored,
    Ignored
}
