namespace Hiker.Pro.Trust;

public sealed record ActivationSlot(
    int Slot,
    string ActivationId,
    string InstallationPublicKeyHash,
    string DeviceBindingHash,
    string Status,
    DateTimeOffset UpdatedAt);

public sealed record ActivationRegistry(
    string LicenseId,
    long Revision,
    IReadOnlyList<ActivationSlot> Slots,
    string LastOperationId);

public sealed record ActivationChange(
    ActivationRegistry Registry,
    DeviceActivationCertificate? Certificate,
    string Status);

public static class DeviceActivationManager
{
    public static ActivationChange Activate(
        ActivationRegistry registry,
        CommercialEntitlement entitlement,
        string operationId,
        string installationPublicKeyHash,
        string deviceBindingHash,
        DateTimeOffset now,
        DateTimeOffset certificateExpiresAt)
    {
        Validate(registry, entitlement, operationId, installationPublicKeyHash, deviceBindingHash);
        if (registry.LastOperationId == operationId)
        {
            return new ActivationChange(registry, null, "IDEMPOTENT_REPLAY");
        }

        var existing = registry.Slots.SingleOrDefault(slot => slot.Status == "ACTIVE" &&
            slot.InstallationPublicKeyHash == installationPublicKeyHash && slot.DeviceBindingHash == deviceBindingHash);
        if (existing is not null)
        {
            var same = Certificate(existing, entitlement.LicenseId, now, certificateExpiresAt);
            return new ActivationChange(registry with { LastOperationId = operationId }, same, "ALREADY_ACTIVE");
        }

        var slots = registry.Slots.ToList();
        var reusable = slots.Where(static slot => slot.Status != "ACTIVE").OrderBy(static slot => slot.Slot).FirstOrDefault();
        var slotNumber = reusable?.Slot ?? Enumerable.Range(1, entitlement.DeviceLimit).FirstOrDefault(candidate => slots.All(slot => slot.Slot != candidate));
        if (slotNumber == 0)
        {
            throw new InvalidOperationException("DEVICE_LIMIT_REACHED");
        }

        var activation = new ActivationSlot(
            slotNumber,
            $"activation-{Guid.NewGuid():N}",
            installationPublicKeyHash,
            deviceBindingHash,
            "ACTIVE",
            now);
        slots.RemoveAll(slot => slot.Slot == slotNumber);
        slots.Add(activation);
        var updated = registry with
        {
            Revision = registry.Revision + 1,
            Slots = slots.OrderBy(static slot => slot.Slot).ToArray(),
            LastOperationId = operationId
        };
        return new ActivationChange(updated, Certificate(activation, entitlement.LicenseId, now, certificateExpiresAt), "ACTIVATED");
    }

    public static ActivationChange Deactivate(
        ActivationRegistry registry,
        string operationId,
        int slot,
        DateTimeOffset now)
    {
        if (registry.LastOperationId == operationId) return new ActivationChange(registry, null, "IDEMPOTENT_REPLAY");
        var current = registry.Slots.SingleOrDefault(item => item.Slot == slot && item.Status == "ACTIVE")
            ?? throw new InvalidOperationException("ACTIVE_DEVICE_SLOT_NOT_FOUND");
        var slots = registry.Slots.Select(item => item.Slot == slot ? current with { Status = "DEACTIVATED", UpdatedAt = now } : item).ToArray();
        return new ActivationChange(registry with { Revision = registry.Revision + 1, Slots = slots, LastOperationId = operationId }, null, "DEACTIVATED");
    }

    public static ActivationChange Transfer(
        ActivationRegistry registry,
        CommercialEntitlement entitlement,
        string operationId,
        int fromSlot,
        string newInstallationPublicKeyHash,
        string newDeviceBindingHash,
        DateTimeOffset now,
        DateTimeOffset certificateExpiresAt)
    {
        Validate(registry, entitlement, operationId, newInstallationPublicKeyHash, newDeviceBindingHash);
        if (registry.LastOperationId == operationId)
        {
            return new ActivationChange(registry, null, "IDEMPOTENT_REPLAY");
        }

        _ = registry.Slots.SingleOrDefault(item => item.Slot == fromSlot && item.Status == "ACTIVE")
            ?? throw new InvalidOperationException("ACTIVE_DEVICE_SLOT_NOT_FOUND");
        var replacement = new ActivationSlot(
            fromSlot,
            $"activation-{Guid.NewGuid():N}",
            newInstallationPublicKeyHash,
            newDeviceBindingHash,
            "ACTIVE",
            now);
        var slots = registry.Slots.Select(item => item.Slot == fromSlot ? replacement : item).ToArray();
        var updated = registry with { Revision = registry.Revision + 1, Slots = slots, LastOperationId = operationId };
        return new ActivationChange(updated, Certificate(replacement, entitlement.LicenseId, now, certificateExpiresAt), "TRANSFERRED");
    }

    private static DeviceActivationCertificate Certificate(ActivationSlot slot, string licenseId, DateTimeOffset issuedAt, DateTimeOffset expiresAt) => new(
        slot.ActivationId,
        licenseId,
        slot.InstallationPublicKeyHash,
        slot.DeviceBindingHash,
        slot.Slot,
        issuedAt,
        expiresAt);

    private static void Validate(
        ActivationRegistry registry,
        CommercialEntitlement entitlement,
        string operationId,
        string installationPublicKeyHash,
        string deviceBindingHash)
    {
        if (registry.LicenseId != entitlement.LicenseId || entitlement.DeviceLimit is < 1 or > 100 ||
            string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(installationPublicKeyHash) || string.IsNullOrWhiteSpace(deviceBindingHash) ||
            registry.Slots.GroupBy(static slot => slot.Slot).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Activation registry or request is invalid");
        }
    }
}
