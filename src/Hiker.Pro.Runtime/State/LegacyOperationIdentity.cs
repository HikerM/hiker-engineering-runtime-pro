using System.Security.Cryptography;
using System.Text;

namespace Hiker.Pro.State;

public static class LegacyOperationIdentity
{
    public static string Create(
        string operationType,
        string projectId,
        string sourceFingerprint,
        long migrationGeneration,
        string? sourceRef = null)
    {
        var project = new StateKey(projectId, EntityKinds.Project, "legacy-operation").Normalize().ProjectId;
        if (string.IsNullOrWhiteSpace(operationType)) throw new ArgumentException("Operation type is required", nameof(operationType));
        if (string.IsNullOrWhiteSpace(sourceFingerprint)) throw new ArgumentException("Source fingerprint is required", nameof(sourceFingerprint));
        if (migrationGeneration < 0) throw new ArgumentOutOfRangeException(nameof(migrationGeneration));
        var canonical = string.Join('\n',
            operationType.Trim().ToUpperInvariant(),
            project,
            sourceFingerprint.Trim().ToLowerInvariant(),
            migrationGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sourceRef?.Trim().Replace('\\', '/') ?? string.Empty);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return $"legacy-{operationType.Trim().ToLowerInvariant().Replace('_', '-')}-{hash[..32]}";
    }
}
