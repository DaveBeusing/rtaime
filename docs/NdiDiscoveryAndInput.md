<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# NDI Discovery and Input

## Authority model

NDI discovery is observational provider state. A discovered NDI source is not a production source until an operator explicitly adopts it through ControlHost.

Discovery refresh:

- never changes Preview or Program;
- never creates Operator-local source authority;
- retains a fixed bounded result set with expiry;
- exposes safe source identity and provider availability only.

Adoption is an explicit ControlHost mutation. It assigns a stable production source identity derived from provider identity plus the stable discovered-source identity, persists the source in the durable show project and adds it to the authoritative production source catalog. Adoption itself does not route, preview or take the source.

Normal Preview, CUT, AUTO, Scene and output-role commands remain the only routing mechanisms.

## Runtime input path

RuntimeHost owns NDI receive execution. The shared `rtaime.Provider.Ndi` provider used for NDI output also owns NDI discovery, runtime loading, native ABI isolation and receive sessions.

An adopted NDI source enters the existing Runtime source boundary when a committed execution requires that source. The Runtime bridge reuses:

- the existing external RGBA8 source-frame seam;
- the existing source signal-health state;
- the existing stereo Float32 external-audio path;
- the existing Audio Follow Video / audio-production engine.

There is no second compositor, decoder graph, audio mixer or Operator-side receiver.

The qualified software baseline accepts compatible 1920×1080 progressive NDI video at 50 fps or 60000/1001 fps and normalizes stereo 48 kHz Float32 audio into the existing audio path. Unsupported cadence or media shape fails visibly rather than being silently converted.

## Bounded discovery and receive

Discovery retains at most the configured maximum number of current results. Missing sources first become unavailable and then expire; there is no unbounded discovery history.

Each receive session uses a bounded live queue. Saturation drops the oldest queued media rather than accumulating latency.

Source loss preserves the adopted production source identity. Runtime health moves through reconnecting/lost states and no alternate source is substituted. Recovery is valid only when the same adopted provider/source identity returns.

NDI provider or runtime failure is observational relative to production authority. A failure after a Runtime commit cannot roll back, reroute or invalidate the committed production state.

## Operator workflow

The MEDIA workspace exposes a custom NDI SOURCES panel backed exclusively by Client → ControlHost → Runtime evidence.

The Operator can:

1. REFRESH discovery;
2. inspect display name, safe source identity, format and availability;
3. select one discovered source;
4. choose ADOPT explicitly;
5. observe input lifecycle/health after Runtime receive activation.

Selection and discovery are presentation-only. ADOPT adds the source to the authoritative catalog but does not route it.

The Operator process contains no NDI SDK/native ABI types, NDI discovery thread or NDI receiver.

## Runtime dependency and interoperability

NDI output, discovery and input share the same separately installed Windows x64 NDI runtime requirement. The runtime is not bundled with rtaime and is discovered only through the declared runtime environment variables.

Offline preflight can require this shared runtime explicitly with `-RequireNdiRuntime`.

Repository tests use deterministic discovery and receive test doubles. Real NDI runtime/peer/network interoperability remains **UNVERIFIED** until evidence is captured in a declared compatible environment. No NDI HX, WAN, PTZ or certification claim is implied.
