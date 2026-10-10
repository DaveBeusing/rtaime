<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# NDI Runtime Dependency

## Boundary

`rtaime.Provider.Ndi` implements the shared rtaime NDI output, discovery and input adapter boundary and compiles without an NDI SDK package or native binary in the repository.

The native NDI runtime is **not bundled** with rtaime. An installation that configures NDI output, discovery or input must provide a compatible Windows x64 NDI runtime separately.

Runtime discovery is explicit and limited to:

- `RTAIME_NDI_LIBRARY_PATH` for an exact `Processing.NDI.Lib.x64.dll` path;
- `NDI_RUNTIME_DIR_V6`;
- `NDI_RUNTIME_DIR_V5`.

rtaime does not silently download an SDK/runtime and does not fall back from NDI to SRT.

## Licensing and redistribution

The project currently makes no NDI binary redistribution grant or claim. No NDI SDK/runtime binary is committed to source control, included in the offline bundle, or added to release artifacts.

Any future decision to redistribute an NDI runtime must be reviewed against the then-current NDI SDK license/EULA and third-party notices before binaries are added. The release SBOM, dependency notices, packaging policy and qualification evidence must be updated in the same change.

Until that review is explicitly completed, `EXTERNAL_RUNTIME_NOT_BUNDLED` is the source-controlled redistribution status.

## Interop boundary

The provider adapter uses a narrow native boundary for:

- runtime initialization/shutdown;
- sender creation/destruction;
- NDI video v2 submission and receive;
- NDI audio v3 submission and receive;
- bounded NDI source discovery.

NDI native types remain inside `rtaime.Provider.Ndi`. Stable Provider Contracts contain only rtaime protocol/settings/media semantics.

Repository CI uses deterministic sender, discovery and receive test doubles for bounded queueing, expiry, timing, failure isolation and recovery. Real NDI interoperability against a declared runtime and peer environment is **UNVERIFIED** until separate environment evidence is captured.

## Preflight

Normal offline preflight does not require NDI because NDI is optional.

For a deployment that configures any NDI capability:

```powershell
./tools/Invoke-OfflinePreflight.ps1 -BundlePath . -InstallPath C:\rtaime -RequireNdiRuntime
```

The check verifies that the separately installed runtime can be located through the declared runtime environment variables. It does not treat runtime presence as interoperability certification.
