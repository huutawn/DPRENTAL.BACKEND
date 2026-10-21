namespace Dprental.RentalService.Application.Kafka.Contracts;

public static class KafkaContractSpec
{
    public const int CurrentEnvelopeVersion = 1;

    public static class Producers
    {
        public const string RentalService = "RentalService";
        public const string PaymentService = "PaymentService";
        public const string InventoryService = "InventoryService";
    }

    public static class Topics
    {
        public const string RentalEvents = "rental.events.v1";
        public const string PaymentEvents = "payment.events.v1";
        public const string PaymentDlq = "payment.events.v1.dlq";
        public const string InventoryCommands = "inventory.allocation.commands.v1";
        public const string InventoryAllocationEvents = "inventory.allocation.events.v1";
        public const string InventoryAllocationEventsDlq = "inventory.allocation.events.v1.dlq";
    }

    public static class EventTypes
    {
        public const string RentalOrderCreated = "RentalOrderCreated";
        public const string RentalOrderPaid = "RentalOrderPaid";
        public const string RentalSupplierConfirmationRequested = "RentalSupplierConfirmationRequested";
        public const string RentalItemConfirmed = "RentalItemConfirmed";
        public const string RentalItemRejected = "RentalItemRejected";
        public const string RentalItemExpired = "RentalItemExpired";
        public const string RentalOrderExpired = "RentalOrderExpired";
        public const string RentalOrderCancelled = "RentalOrderCancelled";
        public const string RentalOrderCompleted = "RentalOrderCompleted";
        public const string LatePaymentRefundRequested = "LatePaymentRefundRequested";
        public const string InvoicePaid = "InvoicePaid";
        public const string RefundCompleted = "RefundCompleted";
        public const string RentalSettled = "RentalSettled";
        public const string ReturnedUnitsApplied = "ReturnedUnitsApplied";
        public const string ReturnedUnitsRejected = "ReturnedUnitsRejected";
        public const string DeadLetter = "DeadLetter";
        public const string InventoryAllocationCommandSucceeded = "InventoryAllocationCommandSucceeded";
        public const string InventoryAllocationCommandRejected = "InventoryAllocationCommandRejected";
        public const string RentalItemCancelled = "RentalItemCancelled";
        public const string RentalItemHandoverCompleted = "RentalItemHandoverCompleted";
        public const string RentalItemReturned = "RentalItemReturned";
        public const string RentalUnitsReturned = "RentalUnitsReturned";
        public const string RentalHandoverOverdue = "RentalHandoverOverdue";
        public const string CartItemAdded = "CartItemAdded";
        public const string RentalItemPaid = "RentalItemPaid";
        public const string RentalItemCompleted = "RentalItemCompleted";
    }

    public static class CommandTypes
    {
        public const string CreateHold = "CreateHold";
        public const string ExtendHold = "ExtendHold";
        public const string ConfirmAllocation = "ConfirmAllocation";
        public const string ReleaseAllocation = "ReleaseAllocation";
        public const string CreateInvoice = "CreateInvoice";
        public const string ApplyReturnedUnits = "ApplyReturnedUnits";
        public const string MarkAssetsHandedOver = "MarkAssetsHandedOver";
        public const string MarkAssetsOverdue = "MarkAssetsOverdue";
    }
}
