# Commercial Trust Architecture

## Repository and key boundary

The Community 5.18 repository remains public and contains no Pro runtime implementation. This private repository contains the Pro source, but no signing private key. Client runtime code contains verification only. The isolated `tools/Hiker.Pro.Issuer` executable reads a PEM private key from an operator-supplied external path; it never generates a production key, embeds it, prints it, or copies it into an output package.

Entitlement, activation, update, and module keys have distinct purposes and key IDs. A trust key has explicit validity dates and revocation state. An envelope signed for one purpose cannot be replayed as another.

## Signed entitlement and device activation

All envelopes use canonical JSON plus ECDSA P-256/SHA-256. Entitlement includes license ID, edition, exact features, semantic-version range, device limit, issue/expiry/update dates, offline grace, and an account subject hash. Activation separately binds a signed certificate to the entitlement, installation public-key hash, privacy-preserving device binding, and a bounded device slot.

The activation state supports deactivate, reuse, and transfer. Device identity requires at least two stable components but stores only their sorted SHA-256 binding. It does not rely on a single disk serial and therefore does not permanently lock a customer out after normal hardware replacement.

## Failure behavior

Signature, key-purpose, key-validity, revocation, version, device, slot, and clock-rollback checks fail closed for Pro features. They do not block `doctor`, backup, restore, export, or access to user `.ai`, Git, Worktrees, and Evidence. After offline grace, the mode becomes `RECOVERY_ONLY`, not destructive lockout.

`UpdatesUntil` controls installation of newly published versions; it does not retroactively disable an already entitled installed version. Update and module manifests are independently signed and every artifact is checked by relative-path confinement, byte size, and SHA-256.

## Release exclusion

Client packages must include only the Pro runtime, CLI, public trust keys, and signed manifests. They must exclude `tools/Hiker.Pro.Issuer`, source PEM files, test-generated keys, fixtures containing private material, and internal repository paths. Commercial release gates scan both Git and the final package for these conditions.
