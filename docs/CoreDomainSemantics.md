# Core Domain Semantics Foundation

## Scope

This document records the public, dependency-neutral semantics introduced by AP-01 in `rtaime.Core`.

`rtaime.Core` remains the innermost managed project. It has no `rtaime.*` project dependency, no package dependency, no direct assembly reference, and no knowledge of Control, Runtime, Media, AI, providers, persistence, transports, hosts, UI, hardware, or vendor SDKs.

No architecture boundary is changed by this work package; therefore no ADR is required.

## Identity

`Identity` is an opaque, stable GUID-based value.

Rules:

- `Guid.Empty` is invalid for constructed identities.
- canonical text format is lower-level .NET GUID `D` format (`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`).
- parsing is intentionally strict and accepts only the canonical `D` form.
- higher-level contracts should wrap `Identity` when stronger domain-specific type safety is needed.

## Revision and generation

`Revision` and `Generation` are unsigned 64-bit monotonic counters.

Rules:

- initial value is `0`.
- `Next()` increments exactly by one.
- incrementing `UInt64.MaxValue` fails instead of wrapping.
- invariant decimal formatting/parsing is used.

They deliberately do not decide which subsystem owns a revision. Ownership belongs to the higher-level contract/domain using the primitive.

## Time

`UtcTimestamp` represents wall-clock time and normalizes every constructed value to UTC. Its textual representation uses the invariant round-trip `O` format.

`Duration` represents a signed duration in .NET ticks, where one tick is exactly 100 nanoseconds. Its serialized scalar form is the invariant signed tick count.

Wall-clock timestamps are intentionally separate from media-clock timing.

## Rational, frame rate, and timebase

`Rational` stores a canonical numerator/denominator pair with a strictly positive denominator and reduces values by their greatest common divisor.

`FrameRate` expresses exact frames per second. The V1 reference rates are available as:

- `FrameRate.Fps50` = `50/1`
- `FrameRate.Fps59_94` = `60000/1001`

`Timebase` expresses exact seconds per media-clock tick. For example, `1/90000` means one clock tick equals one ninety-thousandth of a second.

All three types use canonical invariant `numerator/denominator` formatting. Floating-point conversion is convenience-only and is not the equality or serialization representation.

## Validation and failure

`ValidationIssue` contains:

- a required machine-readable code,
- a required human-readable message,
- an optional path.

`ValidationResult` snapshots issues at construction and exposes an immutable read-only view. A valid result contains no issues; an invalid result contains at least one issue.

`Failure` is a small dependency-neutral machine-readable failure value consisting of a required code and message. It does not encode transport status, retry policy, exception hierarchy, subsystem ownership, or presentation behavior.

## Compatibility version

`CompatibilityVersion` is a `major.minor` marker with deterministic invariant parsing, formatting, comparison, and same-major inspection.

It intentionally does **not** implement a compatibility policy. Contract-specific packages decide whether a producer/consumer version pair is compatible. This keeps AP-01 from silently deciding the fail-closed version behavior required later by the V1 contract work.

This type is not a replacement for product/package SemVer or the repository's layered versioning governance.

## Determinism and immutability

The public AP-01 value objects are immutable. Equality is value-based for the scalar value objects and canonical rational wrappers. Text representations are culture-invariant and deterministic.

No bulk media data, hardware terminology, IPC type, runtime service, production graph, command model, or execution-planning concern belongs in these primitives.

## Verification

AP-01 adds unit coverage for:

- value equality,
- parsing and formatting,
- invalid and boundary values,
- monotonic revision/generation behavior,
- UTC normalization,
- canonical rational reduction,
- V1 reference frame rates,
- validation snapshot immutability,
- failure semantics,
- compatibility-version parsing and comparison.

Architecture coverage explicitly verifies that `rtaime.Core` has no project, package, or direct assembly references.
