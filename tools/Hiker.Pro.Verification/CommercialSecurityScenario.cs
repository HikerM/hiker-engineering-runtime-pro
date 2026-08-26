using System.Security.Cryptography;
using System.Text;
using Hiker.Pro.Release;
using Hiker.Pro.State;
using Hiker.Pro.Trust;

namespace Hiker.Pro.Verification;

internal sealed class CommercialSecurityScenario
{
    public async Task<IReadOnlyList<VerificationCheck>> RunAsync()
    {
        var checks = new List<VerificationCheck>();
        var now = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        using var entitlementKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var activationKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var updateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var moduleKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = new[]
        {
            PublicKey("entitlement", TrustPurposes.Entitlement, entitlementKey, now),
            PublicKey("activation", TrustPurposes.Activation, activationKey, now),
            PublicKey("update", TrustPurposes.Update, updateKey, now),
            PublicKey("module", TrustPurposes.Module, moduleKey, now)
        };
        var verifier = new SignedEnvelopeVerifier(trust, new FixedTimeProvider(now));
        var engine = new LicensePolicyEngine(verifier, new FixedTimeProvider(now));
        var installation = Hash("installation-a");
        var device = LicensePolicyEngine.DeviceBinding(new[] { "os-a", "disk-a" });
        var entitlement = new CommercialEntitlement(
            "license-1",
            "PRO",
            new[] { "STATE_PLANE_PRO", "MULTI_AGENT", "ADVANCED_RECOVERY" },
            BuildVersionFacts.Current.Product,
            "5.19.99",
            2,
            now.AddDays(-10),
            now.AddDays(10),
            now.AddDays(60),
            7,
            Hash("account"));
        var activation = new DeviceActivationCertificate(
            "activation-1", "license-1", installation, device, 1, now.AddDays(-1), now.AddDays(30));
        var entitlementEnvelope = Sign(entitlement, "entitlement", TrustPurposes.Entitlement, entitlementKey);
        var activationEnvelope = Sign(activation, "activation", TrustPurposes.Activation, activationKey);

        var tamperedLicense = entitlementEnvelope with { Payload = Mutate(entitlementEnvelope.Payload) };
        var g4 = engine.Evaluate(tamperedLicense, activationEnvelope, BuildVersionFacts.Current.Product, installation, device, Clock(now));
        checks.Add(Check("COMM-G4", g4.Mode == LicenseMode.Invalid, "License payload tampering is rejected by real ECDSA verification"));

        var changedFeatures = entitlement with { Features = entitlement.Features.Append("PERFORMANCE_ENGINE").ToArray() };
        var changedFeatureEnvelope = entitlementEnvelope with { Payload = Encode(CanonicalJson.Serialize(changedFeatures)) };
        var g5 = engine.Evaluate(changedFeatureEnvelope, activationEnvelope, BuildVersionFacts.Current.Product, installation, device, Clock(now));
        checks.Add(Check("COMM-G5", g5.Mode == LicenseMode.Invalid && !g5.ProFeaturesAllowed, "Feature entitlement changes without a new signature are rejected"));

        var g6 = engine.Evaluate(entitlementEnvelope, activationEnvelope, BuildVersionFacts.Current.Product, Hash("installation-b"), device, Clock(now));
        checks.Add(Check("COMM-G6", g6.Mode == LicenseMode.Invalid, "A copied activation cannot run under another installation key"));

        var g7 = engine.Evaluate(entitlementEnvelope, activationEnvelope, BuildVersionFacts.Current.Product, installation, device, Clock(now));
        checks.Add(Check("COMM-G7", g7.Mode == LicenseMode.Licensed && g7.ProFeaturesAllowed, "A correctly signed and bound device activation is accepted"));

        var registry = new ActivationRegistry("license-1", 0, Array.Empty<ActivationSlot>(), string.Empty);
        var first = DeviceActivationManager.Activate(registry, entitlement, "activate-1", installation, device, now, now.AddDays(30));
        var second = DeviceActivationManager.Activate(first.Registry, entitlement, "activate-2", Hash("installation-2"), Hash("device-2"), now, now.AddDays(30));
        var deactivated = DeviceActivationManager.Deactivate(second.Registry, "deactivate-1", 1, now.AddHours(1));
        var transferred = DeviceActivationManager.Transfer(deactivated.Registry, entitlement, "transfer-1", 2, Hash("installation-3"), Hash("device-3"), now.AddHours(2), now.AddDays(30));
        checks.Add(Check(
            "COMM-G8",
            deactivated.Registry.Slots.Single(static slot => slot.Slot == 1).Status == "DEACTIVATED" && transferred.Status == "TRANSFERRED",
            "Bounded activation slots support deterministic deactivate and transfer"));

        var expiredEntitlement = entitlement with { ExpiresAt = now.AddDays(-2), OfflineGraceDays = 7 };
        var graceEnvelope = Sign(expiredEntitlement, "entitlement", TrustPurposes.Entitlement, entitlementKey);
        var g9 = engine.Evaluate(graceEnvelope, activationEnvelope, BuildVersionFacts.Current.Product, installation, device, Clock(now));
        checks.Add(Check("COMM-G9", g9.Mode == LicenseMode.OfflineGrace && g9.ProFeaturesAllowed, "An expired entitlement remains usable only inside its signed offline grace"));
        var g10 = engine.Evaluate(graceEnvelope, activationEnvelope, BuildVersionFacts.Current.Product, installation, device, new TrustClockState(now.AddMinutes(-1), null, "offline"));
        checks.Add(Check("COMM-G10", g10.Mode == LicenseMode.OfflineGrace, "Missing license-server contact does not immediately block development"));

        var packageRoot = Path.Combine(Path.GetTempPath(), $"hiker-commercial-gates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(packageRoot);
        try
        {
            var modulePath = Path.Combine(packageRoot, "module.dll");
            await File.WriteAllTextAsync(modulePath, "signed-module").ConfigureAwait(false);
            var module = new SignedModuleManifest("performance", "1.0.0", BuildVersionFacts.Current.Product, await FileHashAsync(modulePath).ConfigureAwait(false), new[] { "PERFORMANCE_ENGINE" });
            var moduleEnvelope = Sign(module, "module", TrustPurposes.Module, moduleKey);
            var packageVerifier = new PackageIntegrityVerifier(verifier);
            await File.WriteAllTextAsync(modulePath, "tampered-module").ConfigureAwait(false);
            var g11 = await packageVerifier.VerifyModuleAsync(moduleEnvelope, modulePath, new HashSet<string>(new[] { "PERFORMANCE_ENGINE" }, StringComparer.Ordinal), BuildVersionFacts.Current.Product).ConfigureAwait(false);
            checks.Add(Check("COMM-G11", g11.Status == "BLOCKED", "A signed module is rejected after its assembly bytes change"));

            var updateRoot = Path.Combine(packageRoot, "update");
            Directory.CreateDirectory(updateRoot);
            var runtimePath = Path.Combine(updateRoot, "runtime.bin");
            await File.WriteAllTextAsync(runtimePath, "signed-runtime").ConfigureAwait(false);
            var update = new SignedUpdateManifest(
                "Hiker Pro",
                "5.19.1",
                "stable",
                BuildVersionFacts.Current.Runtime,
                now,
                new[] { new UpdateArtifact("runtime.bin", await FileHashAsync(runtimePath).ConfigureAwait(false), new FileInfo(runtimePath).Length, "win", "x64") });
            var updateEnvelope = Sign(update, "update", TrustPurposes.Update, updateKey);
            await File.WriteAllTextAsync(runtimePath, "tampered-runtime").ConfigureAwait(false);
            var g12 = await packageVerifier.VerifyUpdateAsync(updateEnvelope, updateRoot).ConfigureAwait(false);
            checks.Add(Check("COMM-G12", g12.Status == "BLOCKED", "A signed update is rejected after an artifact changes"));

            const string installedVersion = "5.19.0";
            var rollback = update with { Version = BuildVersionFacts.Current.Product, RollbackFromVersions = new[] { installedVersion } };
            var signedRollback = verifier.Verify<SignedUpdateManifest>(Sign(rollback, "update", TrustPurposes.Update, updateKey), TrustPurposes.Update);
            var g13 = signedRollback.Valid && signedRollback.Payload is not null
                ? engine.EvaluateUpdate(entitlement, signedRollback.Payload, installedVersion)
                : new UpdatePolicyDecision(false, "SIGNATURE_INVALID");
            checks.Add(Check("COMM-G13", g13.Allowed && g13.Status == "SIGNED_ROLLBACK_ALLOWED", "Rollback is blocked by default and allowed only by a verified signed manifest policy"));

            checks.AddRange(await VerifyRecoveryAndExportAsync(now, entitlementKey, activationKey).ConfigureAwait(false));
        }
        finally
        {
            if (Directory.Exists(packageRoot)) Directory.Delete(packageRoot, recursive: true);
        }

        return checks;
    }

    private static async Task<IReadOnlyList<VerificationCheck>> VerifyRecoveryAndExportAsync(
        DateTimeOffset now,
        ECDsa entitlementKey,
        ECDsa activationKey)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hiker-license-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var layout = new ProjectLayout(root);
            await using var store = new SqliteStateStore(layout);
            await store.InitializeAsync().ConfigureAwait(false);
            var mutation = new StateMutation(
                new StateKey("project-1", EntityKinds.Task, "task-1"),
                0,
                1,
                StateLifecycle.Valid,
                "STATE_COORDINATOR",
                BuildVersionFacts.Current.StateSchema,
                Hash("source"),
                Hash("deps"),
                "{\"status\":\"SAFE\"}");
            var committed = await store.CompareAndSwapAsync(new TransactionRequest(
                "commercial-recovery-state",
                mutation,
                "TASK_CREATED",
                "TASK_CREATED",
                "{}")).ConfigureAwait(false);
            var installation = Hash("recovery-installation");
            var device = LicensePolicyEngine.DeviceBinding(new[] { "recovery-os", "recovery-disk" });
            var expired = new CommercialEntitlement(
                "recovery-license", "PRO", new[] { "STATE_PLANE_PRO" }, BuildVersionFacts.Current.Product, "5.19.99", 1,
                now.AddDays(-30), now.AddDays(-20), now.AddDays(-20), 1, Hash("recovery-account"));
            var activation = new DeviceActivationCertificate("recovery-activation", "recovery-license", installation, device, 1, now.AddDays(-30), now.AddDays(30));
            var trust = new[]
            {
                PublicKey("recovery-entitlement", TrustPurposes.Entitlement, entitlementKey, now),
                PublicKey("recovery-activation", TrustPurposes.Activation, activationKey, now)
            };
            var engine = new LicensePolicyEngine(new SignedEnvelopeVerifier(trust, new FixedTimeProvider(now)), new FixedTimeProvider(now));
            var decision = engine.Evaluate(
                Sign(expired, "recovery-entitlement", TrustPurposes.Entitlement, entitlementKey),
                Sign(activation, "recovery-activation", TrustPurposes.Activation, activationKey),
                BuildVersionFacts.Current.Product,
                installation,
                device,
                Clock(now));
            var after = await store.ReadAsync(mutation.Key).ConfigureAwait(false);
            var g14 = committed.Committed && decision.Mode == LicenseMode.RecoveryOnly && decision.DataRecoveryAllowed && after?.PayloadJson.Contains("SAFE", StringComparison.Ordinal) == true;
            var backup = await new StateBackupService(layout).CreateAsync().ConfigureAwait(false);
            var export = await store.ExportAsync("project-1").ConfigureAwait(false);
            using var exportDocument = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(export.OutputPath).ConfigureAwait(false));
            var g15 = decision.StateExportAllowed && File.Exists(backup.BackupPath) && File.Exists(export.OutputPath) &&
                      export.Entities == 1 && export.Operations == 1 && export.Events == 1 &&
                      exportDocument.RootElement.GetProperty("entities").GetArrayLength() == 1;
            return new[]
            {
                Check("COMM-G14", g14, "License expiry preserves authoritative .ai state and keeps deterministic recovery enabled"),
                Check("COMM-G15", g15, "Recovery-only mode permits real state read, SQLite backup and bounded JSON export")
            };
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static VerificationCheck Check(string id, bool pass, string detail) => new(
        id,
        pass ? "PASS" : "BLOCKED",
        "DETERMINISTIC_VERIFIED",
        detail,
        new Dictionary<string, object?>());

    private static TrustClockState Clock(DateTimeOffset now) => new(now.AddMinutes(-1), now.AddMinutes(-1), "clock");

    private static TrustKey PublicKey(string keyId, string purpose, ECDsa key, DateTimeOffset now) => new(
        keyId, purpose, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), now.AddDays(-2), now.AddYears(10), false);

    private static SignedEnvelope Sign<T>(T payload, string keyId, string purpose, ECDsa key)
    {
        var bytes = CanonicalJson.Serialize(payload);
        return new SignedEnvelope("hiker-signed-envelope/v1", SignedEnvelopeVerifier.Algorithm, keyId, purpose, Encode(bytes), Encode(key.SignData(bytes, HashAlgorithmName.SHA256)));
    }

    private static string Mutate(string value) => value[..^1] + (value[^1] == 'a' ? "b" : "a");
    private static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task<string> FileHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false)).ToLowerInvariant();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
