namespace Dprental.RentalService.Application.Kafka.Contracts;

public static class KafkaContractSpec
{
    public const int CurrentEnvelopeVersion = 1;

    public static class Producers
    {
        public const string RentalService = "rental-service";
        public const string PaymentService = "payment-service";
        public const string InventoryService = "inventory-service";
    }

    public static class Topics
    {
        public const string RentalEvents = "rental.events.v1";
        public const string PaymentEvents = "payment.events.v1";
        public const string PaymentDlq = "payment.events.v1.dlq";
        // Proposed return workflow topics; Inventory must confirm before enablement.
        public const string InventoryAllocationCommands = "inventory.allocation.commands.v1";
        public const string InventoryAllocationEvents = "inventory.allocation.events.v1";

        // PROPOSED topics (Unconfirmed by provider)
        public const string InventoryCommands = "inventory.commands.v1";
        public const string PaymentCommands = "payment.commands.v1";
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
        public const string LatePaymentRefundRequested = "LatePaymentRefundRequested";
        public const string InvoicePaid = "InvoicePaid";
        public const string RefundCompleted = "RefundCompleted";
        public const string ReturnedUnitsApplied = "ReturnedUnitsApplied";
        public const string DeadLetter = "DeadLetter";
    }

    public static class CommandTypes
    {
        // PROPOSED commands (Unconfirmed by provider)
        public const string CreateHold = "CreateHold";
        public const string ReleaseAllocation = "ReleaseAllocation";
        public const string CreateInvoice = "CreateInvoice";
        public const string ApplyReturnedUnits = "ApplyReturnedUnits";
    }
}
