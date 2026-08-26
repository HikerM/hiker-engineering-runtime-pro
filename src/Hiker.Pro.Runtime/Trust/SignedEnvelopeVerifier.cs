using System.Security.Cryptography;
using System.Text.Json;

namespace Hiker.Pro.Trust;

public sealed class SignedEnvelopeVerifier(IReadOnlyCollection<TrustKey> trustKeys, TimeProvider? timeProvider = null)
{
    public const string Algorithm = "ECDSA_P256_SHA256";
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public SignatureVerification<T> Verify<T>(SignedEnvelope envelope, string requiredPurpose)
    {
        if (envelope.Format != "hiker-signed-envelope/v1" || envelope.Algorithm != Algorithm || envelope.Purpose != requiredPurpose)
        {
            return new SignatureVerification<T>(false, "ENVELOPE_POLICY_MISMATCH", default, envelope.KeyId);
        }

        var key = trustKeys.SingleOrDefault(item => item.KeyId == envelope.KeyId && item.Purpose == requiredPurpose);
        var now = _clock.GetUtcNow();
        if (key is null || key.Revoked || now < key.NotBefore || now > key.NotAfter)
        {
            return new SignatureVerification<T>(false, key?.Revoked == true ? "KEY_REVOKED" : "KEY_NOT_TRUSTED", default, envelope.KeyId);
        }

        byte[] payload;
        byte[] signature;
        byte[] publicKey;
        try
        {
            payload = Base64Url.Decode(envelope.Payload);
            signature = Base64Url.Decode(envelope.Signature);
            publicKey = Convert.FromBase64String(key.SubjectPublicKeyInfoBase64);
            if (!string.Equals(Base64Url.Encode(payload), envelope.Payload, StringComparison.Ordinal) ||
                !string.Equals(Base64Url.Encode(signature), envelope.Signature, StringComparison.Ordinal))
            {
                return new SignatureVerification<T>(false, "NON_CANONICAL_ENCODING", default, envelope.KeyId);
            }
        }
        catch (FormatException)
        {
            return new SignatureVerification<T>(false, "INVALID_ENCODING", default, envelope.KeyId);
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            if (consumed != publicKey.Length || !ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256))
            {
                return new SignatureVerification<T>(false, "SIGNATURE_INVALID", default, envelope.KeyId);
            }

            return new SignatureVerification<T>(true, "VERIFIED", CanonicalJson.Deserialize<T>(payload), envelope.KeyId);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or InvalidDataException)
        {
            return new SignatureVerification<T>(false, "PAYLOAD_INVALID", default, envelope.KeyId);
        }
    }
}

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch { 2 => "==", 3 => "=", 0 => string.Empty, _ => throw new FormatException("Invalid base64url") };
        return Convert.FromBase64String(normalized);
    }
}
