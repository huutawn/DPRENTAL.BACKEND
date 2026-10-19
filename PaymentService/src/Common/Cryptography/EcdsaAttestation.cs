using System.Security.Cryptography;
using System.Text;

namespace Dprental.PaymentService.Common.Cryptography;

public static class EcdsaAttestation
{
    public const string AttestationPrefix = "DPRENTAL:ATTESTATION:V1";
    public const string ReceiptDomainSeparator = "DPRENTAL:RECEIPT:V1";

    public static string BuildAttestationMessage(
        string keyId,
        string streamId,
        long version,
        Guid eventId,
        string entryHash,
        DateTimeOffset confirmedAt)
    {
        return $"{AttestationPrefix}:{keyId.Trim()}:{streamId.Trim()}:{version}:{eventId:D}:{entryHash.Trim().ToLowerInvariant()}:{confirmedAt.ToUnixTimeMilliseconds()}";
    }
    public static string BuildVerificationReceiptMessage(
        string keyId,
        string requestHash,
        string callerNonce,
        string integrityStatus,
        bool isUsable,
        string reason,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        return $"{ReceiptDomainSeparator}:{keyId.Trim()}:{requestHash.Trim().ToLowerInvariant()}:{callerNonce.Trim()}:{integrityStatus.Trim().ToUpperInvariant()}:{isUsable}:{reason.Trim()}:{issuedAt.ToUnixTimeMilliseconds()}:{expiresAt.ToUnixTimeMilliseconds()}";
    }


    public static string Sign(ECDsa privateKey, string message)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        byte[] messageBytes = Encoding.UTF8.GetBytes(message);
        byte[] signatureBytes = privateKey.SignData(messageBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(signatureBytes);
    }

    public static bool Verify(ECDsa publicKey, string message, string base64Signature)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Signature);

        try
        {
            byte[] signatureBytes = Convert.FromBase64String(base64Signature);
            byte[] messageBytes = Encoding.UTF8.GetBytes(message);
            return publicKey.VerifyData(messageBytes, signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch
        {
            return false;
        }
    }

    public static ECDsa CreateEphemeralP256KeyPair()
    {
        return ECDsa.Create(ECCurve.NamedCurves.nistP256);
    }

    public static string ExportPublicKeyBase64(ECDsa ecdsa)
    {
        return Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    }

    public static ECDsa ImportPublicKeyBase64(string base64Spki)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64Spki), out _);
        return ecdsa;
    }

    public static string ExportPrivateKeyBase64(ECDsa ecdsa)
    {
        return Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
    }

    public static ECDsa ImportPrivateKeyBase64(string base64Pkcs8)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(base64Pkcs8), out _);
        return ecdsa;
    }
}
