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

RuntimeHost rasterizes the bounded CG rectangle into straight RGBA pixels and owns a dedicated reusable dynamic RGBA source for that CG surface. Bitmap graphics retain their own Runtime-owned source. Both participate in the same bounded ordered compositor rather than overwriting one another.

The render order is:

1. resolve the Program CUT/DISSOLVE result;
2. materialize the currently visible governed layers;
3. sort them by confirmed Runtime order and stable layer identity;
4. composite the ordered layers over the transitioned background;
5. read the final Program pixels;
6. publish Program monitoring;
7. provide the same post-graphics pixels to recording.

The stable Production CG layer identity is `production-cg`; the bitmap graphics identity is `bitmap-graphics`. Dynamic CG text therefore shares the same Runtime/provider compositing truth as bitmap graphics while remaining independently visible and independently opaque.

Existing bitmap overlays remain fully supported and are not converted to text templates.

## Performance bounds

Text rasterization occurs only when a CG definition is applied or changed. It is not executed on every Program frame.

RuntimeHost maintains:

- reusable full-frame bitmap and Production CG buffers;
- reusable full-frame scratch storage for each prepared graphics source;
- a bounded least-recently-used cache of at most 16 rendered CG surfaces;
- a bounded compositing stack whose current maximum is eight active layers.

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

When Production CG is the only graphics source, the compatibility Show / Hide command continues to operate on it. When bitmap and CG are active concurrently, the COMPOSITING workspace exposes confirmed per-layer visibility, opacity and bounded ordering through the governed ControlHost/RuntimeHost path. Global Clear retains its historical meaning and clears the active graphics content.

Bitmap free-placement controls remain scoped to the bitmap layer. CG placement is changed by reapplying the CG definition so anchor semantics remain deterministic.

## Recovery and resynchronization

ControlHost retains the successfully confirmed graphics recovery state for the lifetime of the ControlHost process: bitmap RGBA content and placement, Production CG definition, per-layer visibility/opacity and confirmed layer order.

When RuntimeHost is replaced or restarted and ControlHost reconciles production execution, ControlHost reloads the retained bitmap, reapplies the CG definition, restores bounded layer state and finally reapplies the retained order against the layers that actually exist in the new Runtime. Operator then receives only the restored Runtime-confirmed snapshot through normal resynchronization.

This is bounded RuntimeHost recovery, not durable rundown persistence. A ControlHost restart does not currently persist or restore the graphics stack from durable storage.

## Validation

The qualified path is covered by:

- Client contract validation for text, fonts, geometry and layer bounds;
- Runtime integration for rendering, alpha composition, show/hide and surface reuse;
- deterministic explicit font-fallback and missing-font negative tests;
- bounded-cache regression coverage;
- real process-boundary Operator -> ControlHost -> RuntimeHost CG command/state projection;
- RuntimeHost restart recovery coverage for concurrent bitmap + CG state and layer order;
- Operator UI policy checks that require the CG editor and prohibit local WPF production text rendering;
- existing bitmap-overlay and recording regressions plus concurrent bitmap/CG composition coverage.

## Boundaries

This capability does not provide:

- motion-graphics animation or keyframing;
- HTML/browser graphics;
- arbitrary template authoring;
- multiple independent CG layers;
- durable CG rundown persistence across ControlHost restart;
- automatic migration of existing bitmap overlays.
