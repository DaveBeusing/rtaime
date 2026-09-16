<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Release Signing, Attestation & Immutable Record

## Purpose

This document defines the V1 software-release signing and attestation foundation built on top of `docs/ReleaseEvidence.md`.

The trust flow is:

```text
Release Evidence Manifest
        ↓ SHA-256
ECDSA P-256 signature
        ↓
Release Attestation
        ↓ hash binding
Content-addressed Release Record
        ↓
Offline verification
```

The mechanism is designed so that private production signing material can remain outside the repository and outside the generated release bundle.

## Signing format v1

The versioned V1 signing profile is:

```text
Subject                 exact bytes of release-evidence.json
Subject hash            SHA-256
Signature algorithm     ECDSA on NIST P-256
Signature hash          SHA-256
Signature encoding      IEEE P1363 fixed-field concatenation
Signature length        64 bytes
Public key encoding     SubjectPublicKeyInfo DER, Base64 in JSON
Key fingerprint         SHA-256 over SubjectPublicKeyInfo DER
```

This is a release-trust format decision, not a Core, Runtime, Media, AI or Provider contract dependency.

A future signing profile can be introduced through an explicit format/schema version without leaking cryptographic vendor types into production contracts.

## Files

After signing, the evidence bundle contains the AP-17 files plus:

```text
release-attestation.json
release-record.json
release-record.sha256
```

Schemas are stored under:

```text
schemas/release/v1/release-attestation.schema.json
schemas/release/v1/release-record.schema.json
schemas/release/v1/trusted-release-keys.schema.json
```

## Private-key boundary

Private production keys must never be stored in:

- Git,
- repository configuration,
- source code,
- test fixtures,
- schemas,
- generated evidence bundles,
- release records,
- logs.

`New-ReleaseAttestation.ps1` rejects a private-key path located inside the repository or inside the evidence bundle.

The repository contains only public-key trust metadata.

## CI qualification key

`New-TestReleaseSigningKey.ps1` exists only to qualify the signing mechanism in CI.

It generates a fresh P-256 key outside the repository and the workflow deletes the key in a `finally` block after signing and verification.

The resulting attestation uses:

```text
signerClass = TEST_EPHEMERAL
```

A `TEST_EPHEMERAL` key:

- proves that signing and verification code works,
- proves tamper detection,
- proves release-record binding,
- must never be enrolled as a production release key,
- must never be represented as production signing trust.

## External controlled signer

An externally supplied signing key uses:

```text
signerClass = EXTERNAL_CONTROLLED
```

This class alone does not establish trust.

Production trust additionally requires that the public-key fingerprint is present as an active `SOFTWARE_RELEASE` key in:

```text
build/release/trusted-release-keys.json
```

The trust store contains public metadata only.

Enrolling, replacing or revoking a production release key is a `SECURITY + RELEASE` change and requires separate review/evidence.

The current trust store is intentionally empty, therefore production signing remains `UNVERIFIED`.

## Release attestation

`release-attestation.json` contains:

- attestation schema/type,
- exact subject path,
- subject SHA-256,
- signing algorithm and encoding,
- signer class,
- optional signer identity,
- public key,
- independent public-key fingerprint,
- detached signature,
- signing timestamp.

The verifier does not trust the fingerprint supplied by the attestation. It recalculates the fingerprint from the embedded public key and then verifies the ECDSA signature over the exact `release-evidence.json` bytes.

## Content-addressed release record

`release-record.json` binds:

- product identity,
- product version,
- release stage,
- source commit,
- build commit,
- build identity,
- release-evidence SHA-256,
- attestation SHA-256,
- signing-key fingerprint.

`recordId` is SHA-256 over the canonical V1 identity tuple:

```text
rtaime.release-record.v1
productName
productVersion
releaseStage
sourceCommit
buildCommit
buildId
releaseEvidenceSha256
attestationSha256
keyFingerprint
```

`release-record.sha256` additionally records the SHA-256 of the serialized `release-record.json` file.

This makes the record content-addressed and tamper-evident.

It does not by itself make a mutable storage service immutable. Durable immutable publication/retention of an official release record remains a separate operational release responsibility until a controlled publication backend is implemented and qualified.

## Offline verification

Run:

```powershell
./build/release/Test-ReleaseEvidence.ps1
./build/release/Test-ReleaseAttestation.ps1
```

For an official release verification that must be anchored in an enrolled production key:

```powershell
./build/release/Test-ReleaseAttestation.ps1 -RequireTrustedProductionKey
```

The verifier fails closed on at least:

- modified release evidence,
- invalid signature,
- unsupported algorithm or signature format,
- non-P-256 public key,
- public-key fingerprint mismatch,
- release-record identity mismatch,
- release-record content-id mismatch,
- attestation hash mismatch,
- release-record sidecar mismatch,
- private-key-like material inside the evidence bundle,
- duplicate active trusted-key entries,
- `TEST_EPHEMERAL` key enrolled as active production trust,
- untrusted key when production trust is explicitly required.

## Negative qualification

`Test-ReleaseSigningFailureCases.ps1` copies the completed evidence bundle into isolated temporary directories and proves fail-closed behavior for:

1. tampered `release-evidence.json`,
2. tampered detached signature,
3. forbidden private-key material inside the bundle.

The source evidence bundle remains unchanged.

## CI flow

The managed PR workflow becomes:

```text
Restore
Build
Test
Generate Release Evidence
Verify Release Evidence
Generate TEST_EPHEMERAL key outside repository
Sign Release Evidence
Verify Signature + Release Record
Run signing failure qualification
Delete ephemeral private key
Upload complete evidence bundle
```

## Trust states

Three statements must remain separate:

```text
Cryptographic mechanism valid
Production signing key trusted
Official immutable publication retained
```

For the AP-18 CI path:

```text
Cryptographic mechanism       PASS
Tamper detection              PASS
Content-addressed record      PASS
Production signing trust      UNVERIFIED
Official immutable storage    UNVERIFIED
```

A passing ephemeral signature must never upgrade either of the last two states.

## Scope boundary

This package does not claim:

- HSM or cloud-KMS integration,
- production private-key provisioning,
- production key ceremony completion,
- production public-key enrollment,
- timestamp-authority integration,
- transparency-log publication,
- immutable WORM/object-lock storage,
- GitHub Release publication as an official release process,
- STABLE, VALIDATED or CERTIFIED status,
- CRA conformity.

Those remain separate proof obligations.
