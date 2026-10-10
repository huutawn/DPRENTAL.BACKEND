using System.Text.Json;
using System.Text.Json.Nodes;
using Dprental.RentalService.Application.DTOs.Payment;
using Json.Schema;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Contracts;

public sealed class ContractJsonSchemaValidationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly string SchemasBaseDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "docs", "contracts", "schemas");

    private static EvaluationOptions SchemaEvaluationOptions => new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true
    };

    private static void AssertValidAgainstSchema(string schemaRelativePath, string jsonString)
    {
        string schemaPath = Path.Combine(Path.GetFullPath(SchemasBaseDirectory), schemaRelativePath);
        Assert.True(File.Exists(schemaPath), $"Schema file not found at: {schemaPath}");

        string schemaText = File.ReadAllText(schemaPath);
        JsonSchema schema = JsonSchema.FromText(schemaText);
        using var doc = JsonDocument.Parse(jsonString);
        EvaluationResults result = schema.Evaluate(doc.RootElement, SchemaEvaluationOptions);

        Assert.True(
            result.IsValid,
            $"JSON validation failed against '{schemaRelativePath}'.");
    }

    private static void AssertInvalidAgainstSchema(string schemaRelativePath, string invalidJsonString)
    {
        string schemaPath = Path.Combine(Path.GetFullPath(SchemasBaseDirectory), schemaRelativePath);
        Assert.True(File.Exists(schemaPath), $"Schema file not found at: {schemaPath}");

        string schemaText = File.ReadAllText(schemaPath);
        JsonSchema schema = JsonSchema.FromText(schemaText);
        using var doc = JsonDocument.Parse(invalidJsonString);
        EvaluationResults result = schema.Evaluate(doc.RootElement, SchemaEvaluationOptions);

        Assert.False(
            result.IsValid,
            $"Expected JSON validation to fail against '{schemaRelativePath}', but it unexpectedly succeeded.");
    }

    // 1. All 15 Schemas Integrity Check
    [Fact]
    public void All15SchemaFiles_ExistAndAreValidDraft202012()
    {
        string schemasDir = Path.GetFullPath(SchemasBaseDirectory);
        Assert.True(Directory.Exists(schemasDir), $"Schemas directory not found: {schemasDir}");

        string[] schemaFiles = Directory.GetFiles(schemasDir, "*.json", SearchOption.AllDirectories);
        Assert.Equal(15, schemaFiles.Length);

        foreach (string file in schemaFiles)
        {
            string content = File.ReadAllText(file);
            using JsonDocument doc = JsonDocument.Parse(content);
            JsonElement root = doc.RootElement;

            Assert.True(root.TryGetProperty("$schema", out _), $"File '{file}' missing $schema");
            Assert.True(root.TryGetProperty("title", out _), $"File '{file}' missing title");
            Assert.True(root.TryGetProperty("type", out JsonElement typeElem) && typeElem.GetString() == "object",
                $"File '{file}' type must be 'object'");
            Assert.True(root.TryGetProperty("required", out JsonElement reqElem) && reqElem.ValueKind == JsonValueKind.Array,
                $"File '{file}' missing required array");

            // Evaluate that JsonSchema.Net parses the schema definition itself
            JsonSchema parsed = JsonSchema.FromText(content);
            Assert.NotNull(parsed);
        }
    }

    // 2. Payment Operations Schema Evaluation (4 operations: 8 schemas)
    [Fact]
    public void CreateInvoice_RequestAndResponse_PassSchemaEvaluation()
    {
        string validReq = """
        {
          "rentalOrderId": "7b8979c3-7182-4147-920f-cb1c98126b85",
          "orderCode": "ORD-20261008-001",
          "renterId": "9f7a77e2-cf77-4df6-aef9-3a3f5a2a2222",
          "currency": "VND",
          "paymentDeadline": "2026-10-08T08:30:00+07:00",
          "items": [
            {
              "rentalItemId": "a1111111-1111-1111-1111-111111111111",
              "supplierId": "b2222222-2222-2222-2222-222222222222",
              "rentalAmount": 500000,
              "depositAmount": 1000000
            }
          ],
          "operationKey": "op-create-inv-001"
        }
        """;
        AssertValidAgainstSchema("payment/create-invoice.request.json", validReq);

        string validResp = """
        {
          "invoiceRef": "INV-20261008-001",
          "totalAmount": 1500000,
          "currency": "VND",
          "paymentDeadline": "2026-10-08T08:30:00+07:00",
          "isReplay": false
        }
        """;
        AssertValidAgainstSchema("payment/create-invoice.response.json", validResp);

        // Negative: fractional VND fails integer constraint
        string invalidFractional = validReq.Replace("500000", "500000.50");
        AssertInvalidAgainstSchema("payment/create-invoice.request.json", invalidFractional);
    }

    [Fact]
    public void CancelInvoice_RequestAndResponse_PassSchemaEvaluation()
    {
        string validReq = """
        {
          "rentalOrderId": "7b8979c3-7182-4147-920f-cb1c98126b85",
          "invoiceRef": "INV-001",
          "reason": "Cancelled by timeout",
          "operationKey": "op-cancel-01"
        }
        """;
        AssertValidAgainstSchema("payment/cancel-invoice.request.json", validReq);

        string validResp = """
        {
          "invoiceRef": "INV-001",
          "cancelled": true,
          "cancelledAt": "2026-10-08T08:31:00Z",
          "isReplay": false
        }
        """;
        AssertValidAgainstSchema("payment/cancel-invoice.response.json", validResp);
    }

    [Fact]
    public void RequestRefund_RequestAndResponse_PassSchemaEvaluation()
    {
        string validReq = """
        {
          "rentalOrderId": "7b8979c3-7182-4147-920f-cb1c98126b85",
          "invoiceRef": "INV-001",
          "paymentRef": "PAY-001",
          "items": [
            {
              "rentalItemId": "a1111111-1111-1111-1111-111111111111",
              "refundRentalAmount": 500000,
              "refundDepositAmount": 1000000
            }
          ],
          "reason": "Supplier rejected",
          "operationKey": "op-ref-01"
        }
        """;
        AssertValidAgainstSchema("payment/request-refund.request.json", validReq);

        string validResp = """
        {
          "refundRef": "REF-001",
          "operationKey": "op-ref-01",
          "status": "Accepted",
          "totalRefundAmount": 1500000,
          "currency": "VND",
          "isReplay": false
        }
        """;
        AssertValidAgainstSchema("payment/request-refund.response.json", validResp);
    }

    [Fact]
    public void SettleRental_RequestAndResponse_PassSchemaEvaluation()
    {
        string validReq = """
        {
          "rentalOrderId": "7b8979c3-7182-4147-920f-cb1c98126b85",
          "rentalItemId": "a1111111-1111-1111-1111-111111111111",
          "invoiceRef": "INV-001",
          "paymentRef": "PAY-001",
          "supplierId": "b2222222-2222-2222-2222-222222222222",
          "depositAmount": 1000000,
          "finalDeductionAmount": 200000,
          "refundedDepositAmount": 800000,
          "currency": "VND",
          "decisionRef": "DISP-RES-01",
          "operationKey": "op-settle-01"
        }
        """;
        AssertValidAgainstSchema("payment/settle-rental.request.json", validReq);

        string validResp = """
        {
          "settlementRef": "SETTLE-001",
          "operationKey": "op-settle-01",
          "status": "Accepted",
          "finalDeductionAmount": 200000,
          "refundedDepositAmount": 800000,
          "currency": "VND",
          "isReplay": false
        }
        """;
        AssertValidAgainstSchema("payment/settle-rental.response.json", validResp);
    }

    // 3. Payment Events Schema Evaluation (3 schemas)
    [Fact]
    public void PaymentEvents_PassSchemaEvaluation()
    {
        string invoicePaid = """
        {
          "messageId": "11111111-1111-1111-1111-111111111111",
          "messageType": "InvoicePaid",
          "version": 1,
          "producer": "payment-service",
          "aggregateId": "7b8979c3-7182-4147-920f-cb1c98126b85",
          "occurredAt": "2026-10-08T08:15:30Z",
          "correlationId": "corr-001",
          "causationId": null,
          "payload": {
            "rentalOrderId": "7b8979c3-7182-4147-920f-cb1c98126b85",
            "renterId": "9f7a77e2-cf77-4df6-aef9-3a3f5a2a2222",
            "invoiceRef": "INV-001",
            "paymentRef": "PAY-001",
            "paidAmount": 1500000,
            "currency": "VND",
            "paidAt": "2026-10-08T08:15:30Z"
          }
        }
        """;
        AssertValidAgainstSchema("payment/invoice-paid.event.json", invoicePaid);

        string refundCompleted = """
        {
          "messageId": "22222222-2222-2222-2222-222222222222",
          "messageType": "RefundCompleted",
          "version": 1,
          "producer": "payment-service",
          "aggregateId": "7b8979c3-7182-4147-920f-cb1c98126b85",
          "occurredAt": "2026-10-08T08:20:00Z",
          "correlationId": "corr-002",
          "causationId": "11111111-1111-1111-1111-111111111111",
          "payload": {
            "rentalOrderId": "7b8979c3-7182-4147-920f-cb1c98126b85",
            "invoiceRef": "INV-001",
            "paymentRef": "PAY-001",
            "refundRef": "REF-001",
            "operationKey": "op-ref-01",
            "totalRefundedAmount": 1500000,
            "currency": "VND",
            "completedAt": "2026-10-08T08:20:00Z",
            "items": [
              {
                "rentalItemId": "a1111111-1111-1111-1111-111111111111",
                "refundedRentalAmount": 500000,
                "refundedDepositAmount": 1000000
              }
            ]
          }
        }
        """;
        AssertValidAgainstSchema("payment/refund-completed.event.json", refundCompleted);

        string rentalSettled = """
        {
          "messageId": "33333333-3333-3333-3333-333333333333",
          "messageType": "RentalSettled",
          "version": 1,
          "producer": "payment-service",
          "aggregateId": "7b8979c3-7182-4147-920f-cb1c98126b85",
          "occurredAt": "2026-10-08T09:00:00Z",
          "correlationId": "corr-003",
          "causationId": null,
          "payload": {
            "rentalOrderId": "7b8979c3-7182-4147-920f-cb1c98126b85",
            "rentalItemId": "a1111111-1111-1111-1111-111111111111",
            "invoiceRef": "INV-001",
            "paymentRef": "PAY-001",
            "settlementRef": "SETTLE-001",
            "operationKey": "op-settle-01",
            "finalDeductionAmount": 200000,
            "refundedDepositAmount": 800000,
            "currency": "VND",
            "refundRef": "REF-002",
            "completedAt": "2026-10-08T09:00:00Z"
          }
        }
        """;
        AssertValidAgainstSchema("payment/rental-settled.event.json", rentalSettled);

        // Negative: Version as string "v1" instead of integer 1 fails
        string invalidVersion = invoicePaid.Replace("\"version\": 1", "\"version\": \"v1\"");
        AssertInvalidAgainstSchema("payment/invoice-paid.event.json", invalidVersion);
    }

    // 4. Media Operations Schema Evaluation from Raw Provider Fixtures (4 schemas)
    [Fact]
    public void MediaOperations_PassSchemaEvaluation_UsingRealProviderFixtures()
    {
        // 1. Verify endpoint response (GET /api/v1/media/{id}/verify)
        // Provider fixture strictly omits visibility
        string rawVerify = """
        {
          "mediaId": "12345678-1234-1234-1234-123456789abc",
          "uploadedByUserId": "9f7a77e2-cf77-4df6-aef9-3a3f5a2a2222",
          "status": "Ready",
          "originalFileName": "evidence-01.jpg",
          "contentType": "image/jpeg",
          "sizeBytes": 2048576,
          "createdAtUtc": "2026-10-08T07:30:00Z"
        }
        """;
        AssertValidAgainstSchema("media/media-verify.response.json", rawVerify);

        // 2. Metadata endpoint response (GET /api/v1/media/{id})
        // Provider fixture strictly omits uploader
        string rawMetadata = """
        {
          "mediaId": "12345678-1234-1234-1234-123456789abc",
          "visibility": "Private",
          "status": "Ready",
          "createdAtUtc": "2026-10-08T07:30:00Z"
        }
        """;
        AssertValidAgainstSchema("media/media-metadata.response.json", rawMetadata);

        // 3. Create Access request (POST /api/v1/media/{id}/access)
        string rawAccessReq = """
        {
          "action": "read",
          "download": false
        }
        """;
        AssertValidAgainstSchema("media/create-media-access.request.json", rawAccessReq);

        // 4. Access response
        string rawAccessResp = """
        {
          "mediaId": "12345678-1234-1234-1234-123456789abc",
          "url": "https://storage.dprental.vn/private/12345678-1234-1234-1234-123456789abc?token=signed",
          "expiresAtUtc": "2026-10-08T08:00:00Z"
        }
        """;
        AssertValidAgainstSchema("media/media-access.response.json", rawAccessResp);

        // Negative: Invalid status enum fails schema
        string invalidStatus = rawVerify.Replace("\"Ready\"", "\"NonExistentStatus\"");
        AssertInvalidAgainstSchema("media/media-verify.response.json", invalidStatus);
    }

    // 5. Computed Totals Omitted from Serialized DTOs
    [Fact]
    public void CreateInvoiceRequest_ExcludesComputedTotalAmount()
    {
        CreateInvoiceRequest request = new(
            RentalOrderId: Guid.NewGuid(),
            OrderCode: "ORD-001",
            RenterId: Guid.NewGuid(),
            Currency: "VND",
            PaymentDeadline: DateTimeOffset.UtcNow.AddMinutes(30),
            Items: [new(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 1_000_000m)],
            OperationKey: "op-create-inv");

        string json = JsonSerializer.Serialize(request, JsonOptions);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.False(root.TryGetProperty("totalAmount", out _));
        JsonElement firstItem = root.GetProperty("items")[0];
        Assert.False(firstItem.TryGetProperty("totalAmount", out _));
    }
}
