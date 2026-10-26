using System.Text.Json;
using Dprental.AnalyticsService.Application.Events;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AnalyticsService.Application.Events.Rental;

/// <summary>Projects whitelisted Rental lifecycle facts. Raw payload and personal data are discarded.</summary>
public sealed class RentalEventIngestor(AnalyticsServiceDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
    {
        "RentalOrderCreated", "RentalOrderPaid", "RentalOrderCompleted", "RentalOrderCancelled",
        "RentalOrderExpired", "RentalItemConfirmed", "RentalItemRejected", "RentalItemExpired",
        "RentalItemHandoverCompleted", "RentalItemReturned", "RentalItemCompleted"
    };

    public async Task<bool> IngestAsync(JsonElement message, CancellationToken cancellationToken = default)
    {
        var envelope = message.Deserialize<AnalyticsEventEnvelope<JsonElement>>(JsonOptions)
            ?? throw new InvalidOperationException("Empty Rental event.");
        if (envelope.MessageId == Guid.Empty || envelope.Version != 1 || envelope.Payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Invalid Rental event envelope.");
        }

        if (!SupportedTypes.Contains(envelope.MessageType)) return false;

        if (await db.ProcessedRentalEvents.AnyAsync(x => x.MessageId == envelope.MessageId, cancellationToken))
        {
            return false;
        }

        var payload = envelope.Payload;
        Guid orderId = GuidField(payload, "rentalOrderId");
        var order = await db.RentalOrders.FindAsync([orderId], cancellationToken)
            ?? db.RentalOrders.Add(new RentalOrderFact { RentalOrderId = orderId }).Entity;
        long version = LongField(payload, "orderVersion");

        switch (envelope.MessageType)
        {
            case "RentalOrderCreated":
                if (OptionalString(payload, "currency") != "VND")
                    throw new InvalidOperationException("Rental amount must be VND.");
                order.CreatedAt ??= DateField(payload, "createdAt");
                UpdateStatus(order, version, "PENDING_PAYMENT");
                foreach (var source in payload.GetProperty("items").EnumerateArray())
                {
                    Guid itemId = GuidField(source, "rentalItemId");
                    var item = await db.RentalItems.FindAsync([itemId], cancellationToken)
                        ?? db.RentalItems.Add(new RentalItemFact { RentalItemId = itemId }).Entity;
                    item.RentalOrderId = orderId;
                    item.SupplierId = GuidField(source, "supplierId");
                    item.EquipmentListingId = GuidField(source, "equipmentListingId");
                    item.EquipmentTypeId = OptionalGuid(source, "equipmentTypeId");
                    item.EquipmentTypeName = OptionalString(source, "equipmentTypeName");
                    item.BrandId = OptionalGuid(source, "brandId");
                    item.BrandName = OptionalString(source, "brandName");
                    item.Model = OptionalString(source, "model");
                    item.Quantity = source.GetProperty("quantity").GetInt32();
                    item.RentalAmount = source.GetProperty("rentalAmount").GetDecimal();
                    item.DepositAmount = source.GetProperty("depositAmount").GetDecimal();
                    if (item.Quantity < 1 || item.RentalAmount < 0 || item.DepositAmount < 0 ||
                        decimal.Truncate(item.RentalAmount) != item.RentalAmount ||
                        decimal.Truncate(item.DepositAmount) != item.DepositAmount)
                        throw new InvalidOperationException("Invalid Rental amount or quantity.");
                }
                break;
            case "RentalOrderPaid":
                order.PaidAt ??= DateField(payload, "paidAt");
                UpdateStatus(order, version, "PAID");
                break;
            case "RentalOrderCompleted":
                order.CompletedAt ??= DateField(payload, "completedAt");
                UpdateStatus(order, version, "COMPLETED");
                break;
            case "RentalOrderCancelled":
                order.CancelledAt ??= DateField(payload, "cancelledAt");
                UpdateStatus(order, version, "CANCELLED");
                break;
            case "RentalOrderExpired":
                order.ExpiredAt ??= DateField(payload, "expiredAt");
                UpdateStatus(order, version, "EXPIRED");
                break;
            case "RentalItemConfirmed":
                (await ItemAsync(payload, orderId, cancellationToken)).ConfirmedAt ??= DateField(payload, "decisionAt");
                break;
            case "RentalItemRejected":
                var rejected = await ItemAsync(payload, orderId, cancellationToken);
                rejected.RejectedAt ??= DateField(payload, "decisionAt");
                rejected.RejectionReason ??= OptionalString(payload, "reason");
                break;
            case "RentalItemExpired":
                var expired = await ItemAsync(payload, orderId, cancellationToken);
                expired.ExpiredAt ??= DateField(payload, "expiredAt");
                expired.ExpiryReason ??= OptionalString(payload, "reason");
                break;
            case "RentalItemHandoverCompleted":
                (await ItemAsync(payload, orderId, cancellationToken)).HandoverAt ??= DateField(payload, "actualHandoverAt");
                break;
            case "RentalItemReturned":
                (await ItemAsync(payload, orderId, cancellationToken)).ReturnedAt ??= DateField(payload, "returnsCompletedAt");
                break;
            case "RentalItemCompleted":
                (await ItemAsync(payload, orderId, cancellationToken)).CompletedAt ??= envelope.OccurredAt.ToUniversalTime();
                break;
            default:
                return false;
        }

        db.ProcessedRentalEvents.Add(new ProcessedRentalEvent
        {
            MessageId = envelope.MessageId,
            ProcessedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<RentalItemFact> ItemAsync(JsonElement payload, Guid orderId, CancellationToken ct)
    {
        Guid itemId = GuidField(payload, "rentalItemId");
        var item = await db.RentalItems.FindAsync([itemId], ct)
            ?? db.RentalItems.Add(new RentalItemFact { RentalItemId = itemId }).Entity;
        item.RentalOrderId = orderId;
        if (payload.TryGetProperty("supplierId", out var supplier) && supplier.ValueKind == JsonValueKind.String)
        {
            item.SupplierId = supplier.GetGuid();
        }
        return item;
    }

    private static void UpdateStatus(RentalOrderFact order, long version, string status)
    {
        if (version > order.StatusVersion)
        {
            order.Status = status;
            order.StatusVersion = version;
        }
    }

    private static Guid GuidField(JsonElement value, string name) => value.GetProperty(name).GetGuid();
    private static long LongField(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number ? field.GetInt64() : 1;
    private static DateTimeOffset DateField(JsonElement value, string name) =>
        value.GetProperty(name).GetDateTimeOffset().ToUniversalTime();
    private static Guid? OptionalGuid(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetGuid() : null;
    private static string? OptionalString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
}
