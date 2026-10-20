# Inventory → Rental listing snapshot

Rental reads `GET /equipment-listings/{id}/rental-snapshot` for Cart and `POST /equipment-listings/batch` for Checkout. Both responses must contain the same current commercial fields for a listing:

```json
{
  "id": "11111111-1111-1111-1111-111111111111",
  "ownerUserId": "22222222-2222-2222-2222-222222222222",
  "name": "Sony FX3",
  "equipmentTypeId": "33333333-3333-3333-3333-333333333333",
  "brandId": "44444444-4444-4444-4444-444444444444",
  "model": "FX3",
  "dailyRate": 500000,
  "depositPerUnit": 2000000,
  "currency": "VND",
  "thumbnailMediaId": null,
  "specSchemaVersion": 1,
  "specValues": { "sensor": "full-frame" },
  "warehouse": null,
  "status": "Active"
}
```

`equipmentTypeName` and `brandName` may also be supplied for historical display. Rental stores the IDs, names, model, specification schema/version, media ID, daily rate, and deposit on the created `RentalItem`. The order stores currency; pickup location is derived from the warehouse snapshot. Historical orders retain nullable listing metadata and their original deposit, including zero. Order reads use stored values and do not query Inventory again.

Checkout rejects a batch snapshot missing equipment type, brand, model, deposit, schema version, or an object `specValues`. Deposit must be a nonnegative whole VND amount. Cart uses the current deposit per unit and includes it in the client price version. Inventory must update both snapshot endpoints together; otherwise Cart and Checkout can display inconsistent quotes.

The Rental consumer fixture is in `tests/Api.Tests/Contracts/InventoryRentalConsumerContractTests.cs`. A provider conformance test against the running Inventory API is still needed when Inventory implements this payload. The current checkout cannot create new orders from the old Inventory response that omits these fields.
