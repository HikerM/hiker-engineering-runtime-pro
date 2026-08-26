using System.Security.Cryptography;
using System.Text;

namespace Hiker.Pro.Trust;

public sealed class LicensePolicyEngine(
    SignedEnvelopeVerifier verifier,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public LicenseDecision Evaluate(
        SignedEnvelope entitlementEnvelope,
        SignedEnvelope activationEnvelope,
        string productVersion,
        string installationPublicKeyHash,
        string deviceBindingHash,
        TrustClockState clockState)
    {
        var now = _clock.GetUtcNow();
        if (now < clockState.LastSeenUtc.Subtract(TimeSpan.FromMinutes(5)))
        {
            return Decision(LicenseMode.RequiresOnlineValidation, false, "CLOCK_ROLLBACK_DETECTED", now);
        }

        var entitlementResult = verifier.Verify<CommercialEntitlement>(entitlementEnvelope, TrustPurposes.Entitlement);
        var activationResult = verifier.Verify<DeviceActivationCertificate>(activationEnvelope, TrustPurposes.Activation);
        if (!entitlementResult.Valid || !activationResult.Valid || entitlementResult.Payload is null || activationResult.Payload is null)
        {
            return Decision(LicenseMode.Invalid, false, $"{entitlementResult.Status}/{activationResult.Status}", now);
        }

        var entitlement = entitlementResult.Payload;
        var activation = activationResult.Payload;
        if (!string.Equals(entitlement.LicenseId, activation.LicenseId, StringComparison.Ordinal) ||
            !FixedEquals(activation.InstallationPublicKeyHash, installationPublicKeyHash) ||
            !FixedEquals(activation.DeviceBindingHash, deviceBindingHash) ||
            activation.DeviceSlot < 1 || activation.DeviceSlot > entitlement.DeviceLimit ||
            now > activation.ExpiresAt)
        {
            return Decision(LicenseMode.Invalid, false, "ACTIVATION_BINDING_INVALID", now);
        }

        if (!SemanticVersion.InRange(productVersion, entitlement.MinimumVersion, entitlement.MaximumVersion))
        {
            return Decision(LicenseMode.RecoveryOnly, false, "VERSION_NOT_ENTITLED", now);
        }

        var features = new HashSet<string>(entitlement.Features, StringComparer.Ordinal);
        if (now <= entitlement.ExpiresAt)
        {
            return new LicenseDecision(LicenseMode.Licensed, true, true, true, features, "LICENSED", now);
        }

        if (now <= entitlement.ExpiresAt.AddDays(entitlement.OfflineGraceDays))
        {
            return new LicenseDecision(LicenseMode.OfflineGrace, true, true, true, features, "OFFLINE_GRACE", now);
        }

        return Decision(LicenseMode.RecoveryOnly, false, "ENTITLEMENT_EXPIRED", now);
    }

    public bool CanInstallUpdate(CommercialEntitlement entitlement, SignedUpdateManifest update) =>
        update.PublishedAt <= entitlement.UpdatesUntil &&
        SemanticVersion.InRange(update.Version, entitlement.MinimumVersion, entitlement.MaximumVersion);

    public static string FingerprintInstallationPublicKey(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo)).ToLowerInvariant();

    public static string DeviceBinding(IEnumerable<string> stableComponents)
    {
        var values = stableComponents.Select(static value => value.Trim()).Where(static value => value.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        if (values.Length < 2)
        {
            throw new ArgumentException("Device binding requires at least two independent stable components", nameof(stableComponents));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values)))).ToLowerInvariant();
    }

    private static LicenseDecision Decision(LicenseMode mode, bool proAllowed, string status, DateTimeOffset now) =>
        new(mode, proAllowed, true, true, new HashSet<string>(StringComparer.Ordinal), status, now);

    private static bool FixedEquals(string left, string right)
    {
        if (left.Length != right.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    }
}

public static class SemanticVersion
{
    public static bool InRange(string value, string minimum, string maximum) => Compare(value, minimum) >= 0 && Compare(value, maximum) <= 0;

    public static int Compare(string left, string right)
    {
        var a = Parse(left);
        var b = Parse(right);
        var core = a.Major.CompareTo(b.Major);
        if (core == 0) core = a.Minor.CompareTo(b.Minor);
        if (core == 0) core = a.Patch.CompareTo(b.Patch);
        if (core != 0) return core;
        if (a.PreRelease is null && b.PreRelease is null) return 0;
        if (a.PreRelease is null) return 1;
        if (b.PreRelease is null) return -1;
        return ComparePrerelease(a.PreRelease, b.PreRelease);
    }

    private static (int Major, int Minor, int Patch, string? PreRelease) Parse(string value)
    {
        var withoutBuild = value.Split('+', 2)[0];
        var parts = withoutBuild.Split('-', 2);
        var core = parts[0].Split('.');
        if (core.Length != 3 || !int.TryParse(core[0], out var major) || !int.TryParse(core[1], out var minor) || !int.TryParse(core[2], out var patch))
            throw new FormatException($"Invalid semantic version {value}");
        return (major, minor, patch, parts.Length == 2 ? parts[1] : null);
    }

    private static int ComparePrerelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            if (i >= a.Length) return -1;
            if (i >= b.Length) return 1;
            var an = int.TryParse(a[i], out var ai);
            var bn = int.TryParse(b[i], out var bi);
            var compare = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (compare != 0) return compare;
        }

        return 0;
    }
}
