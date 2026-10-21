namespace Dprental.PaymentService.Application.IServices;

public interface IIntegritySigningAdapter
{
    string KeyId { get; }
    string Environment { get; }
    string Issuer { get; }
    string Audience { get; }
    string PublicKeyBase64 { get; }
    string? GetPublicKey(string keyId);
    string Sign(string message);
}
