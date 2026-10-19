using System.Security.Cryptography;
using System.Text;

namespace Dprental.PaymentService.Common.Cryptography;

public static class EcdsaAttestation
{
    public const string AttestationPrefix = "DPRENTAL:ATTESTATION:V1";
    public const string AttestationPrefixV2 = "DPRENTAL:ATTESTATION:V2";
    public const string ReceiptDomainSeparator = "DPRENTAL:RECEIPT:V1";
    public const string ReceiptDomainSeparatorV2 = "DPRENTAL:RECEIPT:V2";

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

    public static string BuildAttestationMessageV2(
        string keyId,
        string environment,
        string streamId,
        long version,
        Guid eventId,
        string entryHash,
        DateTimeOffset confirmedAt)
    {
        return $"{AttestationPrefixV2}:{keyId.Trim()}:{environment.Trim().ToLowerInvariant()}:{streamId.Trim()}:{version}:{eventId:D}:{entryHash.Trim().ToLowerInvariant()}:{confirmedAt.ToUniversalTime().ToUnixTimeMilliseconds()}";
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

    public static string BuildVerificationReceiptMessageV2(
        int schema,
        string issuer,
        string audience,
        string environment,
        Guid receiptId,
        string keyId,
        string requestHash,
        string callerIdentity,
        string callerNonce,
        string purpose,
        string integrityStatus,
        bool isUsable,
        string reason,
        decimal actualAmount,
        string actualCurrency,
        string actualStatus,
        string claimsDigest,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        var sb = new StringBuilder(512);
        sb.Append(ReceiptDomainSeparatorV2).Append(':');
        sb.Append(schema).Append(':');
        sb.Append(issuer.Trim()).Append(':');
        sb.Append(audience.Trim()).Append(':');
        sb.Append(environment.Trim()).Append(':');
        sb.Append(receiptId.ToString("D")).Append(':');
        sb.Append(keyId.Trim()).Append(':');
        sb.Append(requestHash.Trim().ToLowerInvariant()).Append(':');
        sb.Append(callerIdentity.Trim()).Append(':');
        sb.Append(callerNonce.Trim()).Append(':');
        sb.Append(purpose.Trim().ToUpperInvariant()).Append(':');
        sb.Append(integrityStatus.Trim().ToUpperInvariant()).Append(':');
        sb.Append(isUsable ? "true" : "false").Append(':');
        sb.Append(reason.Trim()).Append(':');
        sb.Append(Dprental.PaymentService.Common.Utils.VndAmountValidator.FormatVndAmount(actualAmount)).Append(':');
        sb.Append(actualCurrency.Trim().ToUpperInvariant()).Append(':');
        sb.Append(actualStatus.Trim().ToUpperInvariant()).Append(':');
        sb.Append(claimsDigest.Trim().ToLowerInvariant()).Append(':');
        sb.Append(issuedAt.ToUniversalTime().ToUnixTimeMilliseconds()).Append(':');
        sb.Append(expiresAt.ToUniversalTime().ToUnixTimeMilliseconds());
        return sb.ToString();
    }

    public static string ComputeClaimsDigest(IEnumerable<(string EntityType, string EntityId, long Revision, string Digest)>? claims)
    {
        if (claims is null)
        {
            return CanonicalJournalHasher.GenesisHash;
        }

        var list = claims.OrderBy(c => c.EntityType, StringComparer.Ordinal)
                         .ThenBy(c => c.EntityId, StringComparer.Ordinal)
                         .ToList();
        if (list.Count == 0)
        {
            return CanonicalJournalHasher.GenesisHash;
        }

        var sb = new StringBuilder();
        foreach (var c in list)
        {
            sb.Append(c.EntityType.Trim()).Append('|')
              .Append(c.EntityId.Trim()).Append('|')
              .Append(c.Revision).Append('|')
              .Append(c.Digest.Trim().ToLowerInvariant()).Append(';');
        }
        byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
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
