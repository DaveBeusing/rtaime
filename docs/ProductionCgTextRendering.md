<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

# Production CG Text Rendering

## Purpose

rtaime provides a bounded Production CG text-rendering path for dynamic text graphics such as lower thirds. The capability extends the existing RuntimeHost-owned Program graphics layer; it does not introduce a second compositor or a WPF-owned production renderer.

## Authority and data flow

The command path is:

```text
Operator
  -> rtaime.Client
  -> ControlHost
  -> RuntimeHost
  -> existing Program graphics layer
  -> existing GPU compositor
  -> Program monitoring / recording
```

ControlHost remains the production command authority. Operator edits parameters and submits explicit commands. RuntimeHost is the only component that rasterizes CG text into authoritative Production pixels.

Operator never converts WPF text into Program graphics and never treats local presentation state as Production truth.

## Qualified contract

The qualified CG text definition contains:

- text content;
- primary typeface and optional explicit fallback typeface;
- font size in pixels;
- foreground RGBA color;
- normalized anchor position;
- one of nine explicit anchors;
- bounded pixel width and height;
- left, center or right alignment;
- optional RGBA panel with padding and corner radius;
- visibility;
- Program Graphics layer;
- Z-order.

The current qualified path intentionally supports one Program graphics layer at Z-order 0. A general motion-graphics stack, arbitrary template authoring and browser/HTML rendering remain outside this capability.

## Font determinism

RuntimeHost resolves the requested typeface against installed Windows font families.

Resolution is deterministic and fail-closed:

1. use the requested family only when its name matches an installed family;
2. otherwise use the explicitly supplied fallback family only when it is installed;
3. otherwise reject the CG command.

There is no silent fallback to an unrelated system font. The resolved family is returned in Runtime state so Operator can display the confirmed Production choice.

The V1 reference lower-third workflow uses `Segoe UI` with explicit `Arial` fallback.

## Rendering and compositing

RuntimeHost rasterizes the bounded CG rectangle into straight RGBA pixels. The result is assigned to the same Runtime-owned dynamic RGBA graphics source already used by bitmap overlays.

The existing render order remains:

1. resolve the Program CUT/DISSOLVE result;
2. composite the active Runtime graphics layer;
3. read the final Program pixels;
4. publish Program monitoring;
5. provide the same post-graphics pixels to recording.

Dynamic CG text therefore uses the same graphics/compositing truth as existing PNG/RGBA overlays.

Existing bitmap overlays remain fully supported and are not converted to text templates.

## Performance bounds

Text rasterization occurs only when a CG definition is applied or changed. It is not executed on every Program frame.

RuntimeHost maintains:

- a reusable full-frame graphics buffer;
- a reusable full-frame scratch buffer;
- a bounded least-recently-used cache of at most 16 rendered CG surfaces.

The cache key contains only rasterization-affecting values. Visibility and placement changes do not force text rasterization.

The confirmed CG snapshot exposes whether the current surface was a cache hit and the measured rasterization duration. These values are diagnostic observations, not timing authority.

## Lower-third workflow

The Operator Graphics / Overlay workspace exposes a Production Lower Third editor with:

- text;
- primary typeface;
- explicit fallback typeface;
- font size;
- explicit Render / Apply command.

The initial V1 layout uses a bounded 900 x 144 pixel lower-third rectangle, bottom-left anchor, white text and a translucent dark panel.

Applying the lower third sends only the definition through the governed command path. RuntimeHost renders the pixels and the Operator refreshes the confirmed snapshot.

Show / Hide and Clear continue to use the existing graphics commands.

Bitmap free-placement controls are disabled while Production CG is active. CG placement is changed by reapplying the CG definition so anchor semantics remain deterministic.

## Recovery and resynchronization

ControlHost retains the last successfully confirmed CG definition for the lifetime of the ControlHost process.

When RuntimeHost is replaced or restarted and ControlHost reconciles production execution, ControlHost reapplies the retained CG definition after Runtime authority is aligned. Operator then receives the restored Runtime snapshot through normal resynchronization.

This is bounded RuntimeHost recovery, not durable rundown persistence. A ControlHost restart does not currently persist or restore the CG definition from durable storage.

Bitmap overlays retain their existing behavior and are not newly persisted by this capability.

## Validation

The qualified path is covered by:

- Client contract validation for text, fonts, geometry and layer bounds;
- Runtime integration for rendering, alpha composition, show/hide and surface reuse;
- deterministic explicit font-fallback and missing-font negative tests;
- bounded-cache regression coverage;
- real process-boundary Operator -> ControlHost -> RuntimeHost CG command/state projection;
- RuntimeHost restart recovery coverage;
- Operator UI policy checks that require the CG editor and prohibit local WPF production text rendering;
- existing bitmap-overlay and recording regressions.

## Boundaries

This capability does not provide:

- motion-graphics animation or keyframing;
- HTML/browser graphics;
- arbitrary template authoring;
- multiple independent CG layers;
- durable CG rundown persistence across ControlHost restart;
- automatic migration of existing bitmap overlays.
