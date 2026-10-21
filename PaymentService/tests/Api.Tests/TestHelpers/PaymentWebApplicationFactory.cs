using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dprental.PaymentService.Api.Tests.TestHelpers;

public class PaymentWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string DefaultInternalApiKey = "test-rental-service-key-for-payment-2026";
    public const string DefaultIntegrityHostApiKey = "test-payment-integrity-host-key-2026";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", "Host=127.0.0.1;Port=5432;Database=payment_test;Username=test;Password=test");
        builder.UseSetting("InternalAuthentication:RentalApiKey", DefaultInternalApiKey);
        builder.UseSetting("IntegrityHost:BaseUrl", "http://127.0.0.1:8081");
        builder.UseSetting("IntegrityHost:InternalApiKey", DefaultIntegrityHostApiKey);
        builder.UseSetting("PaymentEncryption:ActiveKeyId", "test-payment-encryption-key");
        builder.UseSetting("PaymentEncryption:DataEncryptionKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Payment:SePay:ApiKey", "test-sepay-api-key-for-payment");
        builder.UseSetting("Payment:Merchant:Bank", "MBBank");
        builder.UseSetting("Payment:Merchant:Account", "0383799632");
        builder.UseSetting("Kafka:Enabled", "false");
    }
}
