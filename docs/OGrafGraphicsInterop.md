<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Open Broadcast Graphics v1 interoperability boundary

## Status and scope

**IMPLEMENTED: read-only, bounded OGraf v1 manifest/package inspection.**
**UNVERIFIED/UNAVAILABLE: HTML/Web Component rendering, isolated frame provider, graphics action execution, Preview frame admission, Program/Clean/Aux output, Server API.**

This is **not** OGraf graphics-renderer conformance or OGraf Server API compatibility. It is deliberately fail-closed because the present Runtime/Operator do not contain a browser renderer meeting the required security and output-provenance boundaries. A recognized manifest is **not** an admitted Program graphic.

Normative edition: OGraf Graphics v1, published 2025-09-17; OGraf Server API v1, published 2026-08-13. EBU TR 105 (2026) is a separate technical report. The OGraf metadata license is not a grant to redistribute user-supplied fonts, graphics, photos or template media.

## Existing architecture and unchanged authority

Production CG and bitmap graphics already flow through:

\`\`\`text
Operator → Client → ControlHost → RuntimeHost → existing bounded GPU compositor
\`\`\`

The new \`OGrafPackageInspector\` resides in \`rtaime.Client\`, examines selected on-disk assets and **never** uses the existing \`LoadGraphicsOverlayAsync\`, \`ApplyProductionCgTextAsync\`, \`SetGraphicsOverlayAsync\`, Prepare/Commit, media decoder, native provider, or output path. It does not spawn processes, evaluate JavaScript, open sockets, load HTML, or mutate Show state. The Operator Graphics/Overlay workspace exposes a clearly labeled **INSPECT MANIFEST** action, a read-only summary, and an optional locally decoded **static PNG thumbnail**. The image is **not** a render, Program/Preview frame or approval.

## Implemented inspection subset

| Area | Supported behavior | Unsupported behavior |
| --- | --- | --- |
| OGraf manifest | Local file ending \`.ograf.json\`, exact v1 \`$schema\`, \`id\`, \`name\`, \`main\`, real-time/non-real-time mode flags | Other OGraf versions, ZIP/untrusted archive extraction, web-hosted imports |
| Identity | Graphic ID, optional version/name and SHA-256 hashes of inspected manifest + main module | Trust/identity signing, signature verification, license assignment |
| Main | Strictly local relative \`.mjs\`/\`.js\`, SHA-256 digest, at most 2 MiB | JS execution, static JS security claims, CSS/DOM imports, Web Components |
| Data | At most 16 flat scalar \`string\`/\`integer\`/\`number\`/\`boolean\` properties, required/default presence and basic type checks | Full JSON Schema draft 2020-12 validation, nested/composite schemas, expression rules, live data bindings |
| Actions | At most 16 custom action identities and action timing entries; bounded step metadata, explicit \`-1\` unknown duration | \`load\`, \`dispose\`, \`playAction\`, \`stopAction\`, \`updateAction\`, \`customAction\` execution |
| Resources | Local relative paths with traversal/URI/reparse-point rejection; at most four thumbnails of 1 MiB each; Operator displays PNG only within 384×384 pixels | Remote assets, CSS references, font resource qualification, arbitrary media resource graph |
| Rendering requirements | Up to eight requirement alternatives, public-network demand rejected conservatively | Engine/format/refresh-rate admission or performance qualification |
| Interop transport | None | OGraf REST Server API v1, renderers, layer instances, statuses, Events |

The manifest has a 64-KiB bounded JSON payload and a 20-level maximum nesting depth. Inspection is asynchronous and cancellable; no Production-frame polling or synchronous I/O is added to Runtime/Program callbacks. The selected module bytes are read to calculate source identity, but **never interpreted**. An infinite loop or URL inside JavaScript cannot execute during this operation.

Unrecognized manifest extension fields are not interpreted as supported renderer features. A selected package may contain other resources that are not present in the inspection subset; the result remains **INSPECTED ONLY**, regardless of successful basic validation. Inspection alone does not establish a complete dependency graph or asset ownership.

## Security / admission decision

Do not embed arbitrary OGraf JavaScript in \`ControlHost\`, \`RuntimeHost\`, \`Operator\`, a shared WPF browser, or an in-process WebView control. A future renderer must use a separately isolated, minimum-privilege process/container with explicit:

- strict local manifest + dependency snapshot and immutable content identity;
- no default network egress, file access outside the approved package, external protocols, device privileges or ambient credentials;
- OS-enforced child process, CPU/GPU/memory limits, bounded worker concurrency and startup/action/disposal deadlines;
- explicit renderer epoch, source hash, frame ID, source timebase, role (Preview/Program), straight/premultiplied alpha, dimensions and bounded frame transfer;
- prepared, confirmed **real** Preview pixel samples and operator review before a separate revision-/epoch-fenced, ControlHost-authorized activation;
- stale-revision/renderer-crash/timeout/alpha-invalid/over-budget handling that leaves the **previous committed Program output unchanged** or uses a previously confirmed safe still. No synthetic success and no uncertain action replay;
- an independent, reproducible Windows software qualification profile for cold/warm latency, dropouts, CPU/GPU/memory, source hash and published edition; native hardware qualification must remain **UNVERIFIED** until measured.

**No such renderer is shipped in this change.** Therefore \`RendererQualified\`, \`PreviewFrameConfirmed\` and \`CanTakeToProgram\` are false by construction. This is an explicit admission blocker, not a runtime feature flag that may be casually enabled.

The built-in static PNG metadata thumbnail is never the renderer's last-good Program frame and is not a fallback source. Package author metadata is not a rights or license declaration. User-supplied assets must be separately cleared for live production and distribution.

## Validation and qualification

\`\`\`powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj --configuration Release --no-build --filter "FullyQualifiedName~OGrafPackageInspectorTests"
dotnet test rtaime.slnx --configuration Release --no-build -m:1
\`\`\`

\`OGrafPackageInspectorTests\` covers valid v1 minimal manifests, conservative single-schema parameter defaults, exact action durations, local resource security, remote/UNC/path-traversal rejection, malformed data, unsupported Internet requirements, oversized module/missing asset and cancelled reads, and static inspection of a module containing an infinite JavaScript loop. Neither this test nor CI proves that a Web Component was rendered or that a graphics output was safe on hardware. Operator rendering of a thumbnail is not an end-to-end OGraf graphics-path test.

Five Required Gates remain authoritative: CI, Quality, Security, Provider Smoke, Packaged E2E. No gate exemptions or new codec/browser dependencies are introduced.

## Next dependency to enable real OGraf playout

Implement a qualified external renderer provider with a documented browser isolation boundary and bounded alpha-frame transport, then add dedicated **off-air Preview → explicit operator approval → ControlHost Prepare/Commit → confirmed Runtime Program** integration and fault tests. Only then can individual OGraf Graphics functions and Server API operations be advertised as \`implemented\`, \`software-qualified\` or \`hardware-qualified\`. Do not treat the current manifest inspection as that dependency.

## References

- [EBU TR 105 (2026)](https://tech.ebu.ch/publications/tr105)
- [OGraf Graphics v1 specification](https://ograf.ebu.io/v1/specification/docs/Specification.html)
- [OGraf v1 graphics JSON Schema](https://ograf.ebu.io/v1/specification/json-schemas/graphics/schema.json)
- [OGraf Server API v1 OpenAPI](https://github.com/ebu/ograf/blob/main/v1/specification/open-api/server-api.yaml)
- [EBU R 143 v3 – Cybersecurity in media production](https://tech.ebu.ch/publications/r143)
- [Production CG Text Rendering](ProductionCgTextRendering.md)
- [Durable Show Project State](DurableShowProjectState.md)
- [Repository Governance](Governance/RepositoryGovernance.md)
