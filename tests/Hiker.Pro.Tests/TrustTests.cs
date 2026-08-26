using System.Security.Cryptography;
using System.Text;
using Hiker.Pro.Trust;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class TrustTests
{
    [Fact]
    public void EntitlementAndActivation_AreSignedDeviceBoundAndFailClosed()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var entitlementKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var activationKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = new[]
        {
            PublicKey("entitlement-1", TrustPurposes.Entitlement, entitlementKey, now),
            PublicKey("activation-1", TrustPurposes.Activation, activationKey, now)
        };
        var installationHash = Hash("installation-public-key");
        var deviceHash = LicensePolicyEngine.DeviceBinding(new[] { "os-installation-a", "disk-family-b" });
        var entitlement = new CommercialEntitlement(
            "license-1", "PRO", new[] { "runtime", "data-performance" }, "5.19.0-dev.1", "5.19.99",
            3, now.AddDays(-1), now.AddDays(10), now.AddDays(90), 7, Hash("account-1"));
        var activation = new DeviceActivationCertificate(
            "activation-1", "license-1", installationHash, deviceHash, 2, now.AddDays(-1), now.AddDays(30));
        var entitlementEnvelope = Sign(entitlement, "entitlement-1", TrustPurposes.Entitlement, entitlementKey);
        var activationEnvelope = Sign(activation, "activation-1", TrustPurposes.Activation, activationKey);
        var engine = new LicensePolicyEngine(new SignedEnvelopeVerifier(trust, new TestTimeProvider(now)), new TestTimeProvider(now));
        var decision = engine.Evaluate(
            entitlementEnvelope, activationEnvelope, "5.19.0-dev.1", installationHash, deviceHash,
            new TrustClockState(now.AddMinutes(-1), now.AddMinutes(-1), "clock-1"));
        Assert.Equal(LicenseMode.Licensed, decision.Mode);
        Assert.True(decision.ProFeaturesAllowed);
        Assert.True(decision.DataRecoveryAllowed);
        Assert.Contains("runtime", decision.Features);

        var tampered = entitlementEnvelope with { Payload = entitlementEnvelope.Payload[..^1] + (entitlementEnvelope.Payload[^1] == 'a' ? "b" : "a") };
        var invalid = engine.Evaluate(tampered, activationEnvelope, "5.19.0-dev.1", installationHash, deviceHash, new TrustClockState(now, now, "clock-1"));
        Assert.Equal(LicenseMode.Invalid, invalid.Mode);
        Assert.False(invalid.ProFeaturesAllowed);
        Assert.True(invalid.DataRecoveryAllowed);

        var wrongDevice = engine.Evaluate(entitlementEnvelope, activationEnvelope, "5.19.0-dev.1", installationHash, Hash("other-device"), new TrustClockState(now, now, "clock-1"));
        Assert.Equal(LicenseMode.Invalid, wrongDevice.Mode);
    }

    [Fact]
    public void ExpiryGraceClockRollbackAndUpdateEntitlement_DoNotLockUserData()
    {
        var issued = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = new[]
        {
            PublicKey("ent", TrustPurposes.Entitlement, key, issued),
            PublicKey("act", TrustPurposes.Activation, key, issued)
        };
        var entitlement = new CommercialEntitlement("license", "PRO", new[] { "runtime" }, "5.19.0", "5.19.9", 2, issued, issued.AddDays(1), issued.AddDays(30), 5, Hash("account"));
        var activation = new DeviceActivationCertificate("activation", "license", Hash("install"), Hash("device"), 1, issued, issued.AddDays(90));
        var ent = Sign(entitlement, "ent", TrustPurposes.Entitlement, key);
        var act = Sign(activation, "act", TrustPurposes.Activation, key);
        var graceTime = issued.AddDays(3);
        var graceEngine = new LicensePolicyEngine(new SignedEnvelopeVerifier(trust, new TestTimeProvider(graceTime)), new TestTimeProvider(graceTime));
        var grace = graceEngine.Evaluate(ent, act, "5.19.1", Hash("install"), Hash("device"), new TrustClockState(issued.AddDays(2), null, "clock"));
        Assert.Equal(LicenseMode.OfflineGrace, grace.Mode);
        Assert.True(grace.ProFeaturesAllowed);

        var expiredTime = issued.AddDays(10);
        var expiredEngine = new LicensePolicyEngine(new SignedEnvelopeVerifier(trust, new TestTimeProvider(expiredTime)), new TestTimeProvider(expiredTime));
        var expired = expiredEngine.Evaluate(ent, act, "5.19.1", Hash("install"), Hash("device"), new TrustClockState(issued.AddDays(9), null, "clock"));
        Assert.Equal(LicenseMode.RecoveryOnly, expired.Mode);
        Assert.True(expired.DataRecoveryAllowed);
        Assert.True(expired.StateExportAllowed);

        var rollback = graceEngine.Evaluate(ent, act, "5.19.1", Hash("install"), Hash("device"), new TrustClockState(graceTime.AddDays(1), null, "clock"));
        Assert.Equal(LicenseMode.RequiresOnlineValidation, rollback.Mode);
        Assert.True(rollback.DataRecoveryAllowed);

        var update = new SignedUpdateManifest("Hiker", "5.19.2", "stable", "0.1.0", issued.AddDays(20), Array.Empty<UpdateArtifact>());
        Assert.True(graceEngine.CanInstallUpdate(entitlement, update));
        Assert.False(graceEngine.CanInstallUpdate(entitlement, update with { PublishedAt = issued.AddDays(31) }));
    }

    [Fact]
    public async Task SignedUpdateAndModule_RejectTamperingAndPathEscape()
    {
        await using var project = await TestProject.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        using var updateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var moduleKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = new[]
        {
            PublicKey("update", TrustPurposes.Update, updateKey, now),
            PublicKey("module", TrustPurposes.Module, moduleKey, now)
        };
        var packageRoot = Path.Combine(project.Root, "package");
        Directory.CreateDirectory(packageRoot);
        var runtimePath = Path.Combine(packageRoot, "hiker.dll");
        await File.WriteAllTextAsync(runtimePath, "signed-runtime");
        var hash = await FileHashAsync(runtimePath);
        var manifest = new SignedUpdateManifest("Hiker", "5.19.0", "stable", "0.1.0", now,
            new[] { new UpdateArtifact("hiker.dll", hash, new FileInfo(runtimePath).Length, "win", "x64") });
        var verifier = new PackageIntegrityVerifier(new SignedEnvelopeVerifier(trust));
        var verified = await verifier.VerifyUpdateAsync(Sign(manifest, "update", TrustPurposes.Update, updateKey), packageRoot);
        Assert.Equal("VERIFIED", verified.Status);
        await File.WriteAllTextAsync(runtimePath, "tampered-runtime");
        var tampered = await verifier.VerifyUpdateAsync(Sign(manifest, "update", TrustPurposes.Update, updateKey), packageRoot);
        Assert.Equal("BLOCKED", tampered.Status);

        var escaped = manifest with { Artifacts = new[] { new UpdateArtifact("../outside", hash, 1, "win", "x64") } };
        await Assert.ThrowsAsync<InvalidDataException>(() => verifier.VerifyUpdateAsync(Sign(escaped, "update", TrustPurposes.Update, updateKey), packageRoot));

        await File.WriteAllTextAsync(runtimePath, "signed-module");
        var moduleHash = await FileHashAsync(runtimePath);
        var module = new SignedModuleManifest("data-engine", "1.0.0", "5.19.0", moduleHash, new[] { "data-performance" });
        var moduleResult = await verifier.VerifyModuleAsync(Sign(module, "module", TrustPurposes.Module, moduleKey), runtimePath,
            new HashSet<string>(new[] { "data-performance" }, StringComparer.Ordinal), "5.19.0");
        Assert.Equal("VERIFIED", moduleResult.Status);
    }

    [Fact]
    public void DeviceActivation_SupportsBoundedSlotsDeactivateAndTransfer()
    {
        var now = DateTimeOffset.UtcNow;
        var entitlement = new CommercialEntitlement(
            "license", "PRO", new[] { "runtime" }, "5.19.0", "5.19.9", 2,
            now, now.AddYears(1), now.AddYears(1), 7, Hash("account"));
        var registry = new ActivationRegistry("license", 0, Array.Empty<ActivationSlot>(), string.Empty);
        var first = DeviceActivationManager.Activate(registry, entitlement, "activate-1", Hash("install-1"), Hash("device-1"), now, now.AddMonths(3));
        var second = DeviceActivationManager.Activate(first.Registry, entitlement, "activate-2", Hash("install-2"), Hash("device-2"), now, now.AddMonths(3));
        Assert.Equal(2, second.Registry.Slots.Count(static slot => slot.Status == "ACTIVE"));
        Assert.Throws<InvalidOperationException>(() => DeviceActivationManager.Activate(
            second.Registry, entitlement, "activate-3", Hash("install-3"), Hash("device-3"), now, now.AddMonths(3)));
        var deactivated = DeviceActivationManager.Deactivate(second.Registry, "deactivate-1", 1, now.AddDays(1));
        Assert.Equal("DEACTIVATED", deactivated.Registry.Slots.Single(static slot => slot.Slot == 1).Status);
        var reused = DeviceActivationManager.Activate(deactivated.Registry, entitlement, "activate-3", Hash("install-3"), Hash("device-3"), now.AddDays(1), now.AddMonths(3));
        Assert.Equal(1, reused.Certificate?.DeviceSlot);
        var transferred = DeviceActivationManager.Transfer(reused.Registry, entitlement, "transfer-1", 2, Hash("install-4"), Hash("device-4"), now.AddDays(2), now.AddMonths(3));
        Assert.Equal("TRANSFERRED", transferred.Status);
        Assert.Equal(2, transferred.Certificate?.DeviceSlot);
        Assert.Equal(Hash("install-4"), transferred.Registry.Slots.Single(static slot => slot.Slot == 2).InstallationPublicKeyHash);
    }

    [Fact]
    public void RevokedTrustKey_IsRejectedBeforePayloadUse()
    {
        var now = DateTimeOffset.UtcNow;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = PublicKey("revoked", TrustPurposes.Entitlement, key, now) with { Revoked = true };
        var payload = new CommercialEntitlement("license", "PRO", Array.Empty<string>(), "5.19.0", "5.19.9", 1, now, now.AddDays(1), now.AddDays(1), 1, Hash("account"));
        var result = new SignedEnvelopeVerifier(new[] { trust }).Verify<CommercialEntitlement>(Sign(payload, "revoked", TrustPurposes.Entitlement, key), TrustPurposes.Entitlement);
        Assert.False(result.Valid);
        Assert.Equal("KEY_REVOKED", result.Status);
        Assert.Null(result.Payload);
    }

    private static TrustKey PublicKey(string keyId, string purpose, ECDsa key, DateTimeOffset now) => new(
        keyId,
        purpose,
        Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
        now.AddDays(-2),
        now.AddYears(10),
        false);

    private static SignedEnvelope Sign<T>(T payload, string keyId, string purpose, ECDsa key)
    {
        var bytes = CanonicalJson.Serialize(payload);
        return new SignedEnvelope(
            "hiker-signed-envelope/v1",
            SignedEnvelopeVerifier.Algorithm,
            keyId,
            purpose,
            Encode(bytes),
            Encode(key.SignData(bytes, HashAlgorithmName.SHA256)));
    }

    private static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task<string> FileHashAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }
}
