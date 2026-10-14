<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->
# Evidence-bound release readiness

The readiness index is a source-bound inventory and operational handoff. It uses the existing Stable-readiness, release-evidence, packaged-candidate, staged mixed-load and physical renderer verifiers. It does not allocate a version, sign a package, publish, change production state or promote DEV to STABLE.

## Current source inventory

The assessment started from master `bfdabf6d3baef5b60d7e73897a37e6c22f3bfff8` on 2026-10-14. The preceding renderer, consumer, recovery and bounded-soak changes are integrated. There were no remaining branches, open pull requests or closed unmerged pull requests. This inventory is historical; a release must regenerate the index for its own final source.

The successful PR #253 run on `c3f3d2fd1eee3f3ecfa29fd7277469f616bdd073` is historical evidence for that source. Its 1,426 tests, packaged lifecycle and two-cycle software smoke cannot silently qualify the subsequent master merge commit or another release build. At assessment start, master's Required Gates run 38603021388 was still running. No workflow-dispatch hardware runs or published releases were available. Live master protection was disabled and no rulesets were returned. Repository policy therefore continues to record administrative enforcement as UNVERIFIED.

| Domain | Initial assessment | Release blocker / retained location |
| --- | --- | --- |
| Five Required Gates | UNVERIFIED until completed on assessed source | Actions run and attempt-specific jobs snapshot |
| Packaging, offline install, update/rollback and schema recovery | Revalidate on assessed source | Packaged E2E artifacts and coordinated-state-upgrade qualification |
| Independent byte-identical build reproducibility | UNVERIFIED | One checksummed package is insufficient |
| Physical CUDA GPU/driver, professional Media I/O, timing and measured A/V sync | UNVERIFIED | Existing authorized qualification bindings and payloads |
| Staged simultaneous workload smoke | Source-specific software scope only | Staged summary.json; simulated network consumers |
| 8h / 24h / 72h staged mixed-load profiles | UNVERIFIED | Separate completed exact-source summaries for each duration |
| Physical renderer 8h / 24h / 72h total profiles | UNVERIFIED | Manifest, execution, software scenarios and baseline comparison |
| Uninterrupted 72h production instance | UNVERIFIED | Neither repeated software sessions nor 36h per renderer format proves it |
| External controlled production signing and immutable publication | UNVERIFIED | Production candidate, trust store and signed release record |
| Security assessment, compliance, known issues | UNVERIFIED unless explicitly bound and verified | Release evidence domains and their existing assessment bindings |
| Stable support, remediation and deprecation commitments | UNVERIFIED | Approved ProductSupportPolicy and evidence-backed platform matrix |
| Live administrative branch protection | UNVERIFIED | Repository administration must activate and reverify enforcement |

The resulting decision is **NO-GO for production release** while any required domain is FAIL or UNVERIFIED. Successful implementation checks do not declare a supported hardware tuple or an approved support obligation. FAIL means a supplied result was rejected or an observed criterion failed; UNVERIFIED means the required proof is missing. DEV remains DEV.

## Generate and retain an index

```powershell
$source = (git rev-parse HEAD).Trim()
./build/release/New-ReleaseReadinessIndex.ps1 `
  -ExpectedSourceCommit $source `
  -RequiredGatesPath ./artifacts/readiness/required-gates.json `
  -ReleaseEvidencePath ./artifacts/release-evidence `
  -QualificationCandidatePath ./artifacts/release-pipeline/<candidate-directory> `
  -CoordinatedUpgradeQualificationPath ./artifacts/<coordinated-qualification.json> `
  -MixedLoadEvidencePaths @("./artifacts/<run>/summary.json") `
  -OutputPath ./artifacts/readiness/<unique-index-name>.json
```

Use actual generated paths. A supplied candidate first passes the existing package verifier and must match the assessed source/build. Its build version and stage are used only for this assessment, with temporary environment values restored afterward; tracked product identity is unchanged. Every supplied file must exist. An existing output is refused to preserve retained evidence. The index records the exact source, product stage, per-domain status, blockers, input SHA-256 digests and operational handoff. Preserve referenced bytes together with the index. Absolute paths identify the original location; after moving evidence, remap paths and revalidate hashes. The index is an assessment record, not a substitute for the signed offline bundle.

Required Gates input contains `repository`, `selection: latest`, the unmodified GitHub Actions `run` object and its latest `jobs` objects from `jobs?filter=latest`. Capture from the authenticated repository API, not a handwritten success declaration. The verifier requires the governed five unique job names, exact source SHA, repository/workflow identity, run ID, each job’s actual attempt, URLs, successful completion and ordered timestamps. A running containing workflow is permitted only because index collection follows the completed five jobs; pending, cancelled, failed, stale-source, cross-run or future-attempt required jobs are rejected. A targeted rerun may retain completed successful jobs from earlier attempts of this same run and source; their actual attempts are preserved. The mixed-load artifact is selected using the successful CI job’s actual attempt. Offline snapshot integrity does not establish independent API authenticity or cryptographic signing trust.

The Required Gates workflow collects this index after CI, Quality, Security, Provider Smoke and Packaged E2E. It retains the API snapshot, packaged evidence and actual short software summary in `rtaime-release-readiness-<run>-<attempt>`. UNVERIFIED/NO-GO is expected in hosted qualification. A failed domain makes the index job fail while preserving its report. This collection job does not replace any of the five required jobs.

Stable readiness now requires `-RequiredGatesPath`; the authoritative release pipeline forwards that optional input when evaluating STABLE. Omitted input remains UNVERIFIED. Platform qualification references must name the exact assessed source; a syntactically valid SHA from another revision is rejected. Production candidates continue through the existing trust and candidate/evidence hash correlation checks.

## Qualification scope and operational handoff

Staged software evidence is checked by `Test-SustainedMixedLoadEvidence.ps1` against the source-controlled workload descriptor. Smoke cannot satisfy a long profile. Each 8h, 24h and 72h result must independently complete its declared duration, workload stages and resource windows. These are repeated managed sessions in one testhost. Native NDI/SRT, physical A/V synchronization and physical output remain UNVERIFIED.

Optional `-RendererRunsPath` accepts JSON with a `runs` array. Each item supplies `profile`, `EvidencePath`, `ManifestPath`, `SoftwareEvidencePath` and `BaselineComparisonPath`. Relative references resolve against that catalog's directory. The existing renderer verifier checks the complete set, detected device/driver, source, workload cases and actual duration. The `soak8h`, `soak24h` and `soak72h` profiles cover total duration across two formats: respectively 4h, 12h and 36h per format. They do not manufacture Media I/O or measured A/V evidence. Long physical runs currently require NVIDIA ordinal 0.

The applicable renderer matrix remains 1080p50 and 1080p59.94, CUT/DISSOLVE and 0/1/2/4/8 layers. Bounded whole-run histograms report upper bin bounds; CUDA event and pipeline summaries retain their bounded-window scope. No configured threshold or managed timing sample is a physical latency measurement.

Before release, collect missing evidence on the exact final source, preserve run/configuration/device identities and bytes, resolve failed criteria, and obtain approved support and security commitments. Bind physical, security and known-issue evidence through their existing mechanisms before signing. Revalidate offline deployment, coordinated recovery and the old 1.0 checkpoint without altering saved production. Verify external production trust, independent build reproducibility and live repository enforcement. Regenerate the index for the intended candidate and use the established channel/tag/publication policy; the report itself never authorizes a promotion.

Run `build/release/Test-ReleaseReadinessRegression.ps1` for correlation and no-false-Stable regressions. Its positive/negative fixtures are synthetic mechanism tests and must never enter release qualification as actual completed workloads.
