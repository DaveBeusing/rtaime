<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Governed Scene Activation

## Purpose

Governed Scene Activation adds a Scene contract to the existing Control-owned production state without creating a second production authority. Scene selection remains Operator presentation state; Scene activation is an explicit production mutation.

## Scene model

A Scene has:

- a stable `SceneId`;
- a human-readable name;
- a reproducible desired `ProductionRoutingState`;
- an optional versioned `ProductionCompositingState` containing the bounded ordered graphics/compositing layer state governed by the Scene.

The compositing state reuses the stable layer identities and property model already exposed by the multi-layer compositor: layer kind, order, visibility, opacity, transform and content identity. It is bounded to eight layers, requires unique contiguous ordering, validates the layer-id/kind pairing and rejects unsupported versions before planning.

A Scene without `CompositingState` remains a compatible routing-only Scene and preserves the currently authoritative compositing state when activated. A Scene with `CompositingState` recalls routing and the declared layer state in one authoritative transaction. Governed Aux output remains owned by the separate output-role contract and command path, so Scene activation does not silently change Aux routing.

A production without explicitly supplied Scene definitions receives a deterministic one-source Scene projection for each declared production source. This preserves existing production bootstraps while making Scene identity available through the Operator snapshot.

## Selection and activation

Selection and activation are deliberately separate:

```text
select Scene
-> Operator presentation state only
-> no Control command
-> no Production Revision change

TAKE SCENE
-> control.scene.activate
-> Control validation
-> proposed authoritative Scene state
-> PreparedExecutionContract
-> Runtime prepare/commit
-> Control commit confirmation
-> confirmed ActiveSceneId
```

The Operator never treats local Scene selection as production truth.

## Atomicity and authority

ControlHost remains the only production authority. RuntimeHost executes only the prepared state supplied by ControlHost.

A Scene activation becomes authoritative only after Runtime commit confirmation. Until then the prior authoritative `AuthoritativeProductionState`, Program routing and `ActiveSceneId` remain unchanged.

The activation validates:

- Control contract version;
- Production identity;
- optimistic expected Production Revision;
- Scene identity;
- every Scene routing dependency;
- the optional compositing contract version and canonical layer ordering;
- stable layer identities, kinds, transforms and content/resource identities;
- Runtime admission of every required active layer resource;
- revision exhaustion;
- existing provider/capability planning requirements.

The complete desired routing plus compositing state is included in the deterministic `PreparedExecutionContract` identity. RuntimeHost validates required compositing resources before its ordinary prepare/commit boundary. A missing layer, kind mismatch, content-identity mismatch or unsupported layer property rejects prepare before any Scene layer mutation is applied. After a successful Runtime commit the prevalidated layer state is applied inside the same host commit operation; Control promotes the staged authority and `ActiveSceneId` only after that commit is confirmed.

Unknown Scenes, stale revisions, invalid dependencies, Runtime prepare rejection, Runtime transport failure and Runtime commit rejection all fail closed.

Direct routing commands such as Set Preview, CUT or DISSOLVE remain valid and preserve the authoritative compositing snapshot while clearing `ActiveSceneId`. Confirmed direct graphics, CG, visibility, opacity, transform and layer-order mutations are incorporated into Control-owned production state and advance Production Revision. If a direct mutation changes any compositing property governed by the active Scene, `ActiveSceneId` is cleared. Scene identity is never inferred from similarity.

## Operator evidence

The Operator snapshot carries:

- the Scene catalog, including each Scene's optional declared compositing state;
- authoritative Preview and Program routing;
- authoritative compositing state;
- Runtime-confirmed compositing layer evidence;
- optional confirmed `ActiveSceneId`.

The LIVE Scene list therefore distinguishes:

- local selected Scene;
- Scene whose declared Preview source matches the confirmed Preview route;
- Scene explicitly confirmed as active by `ActiveSceneId`.

Only the last state is presented as ACTIVE/LIVE Scene evidence.

## Durability and recovery

Authored Scene definitions are now owned by the versioned durable show project. ControlHost loads that project first and rebuilds the production specification with the persisted ordered Scene catalog before authoritative checkpoint recovery begins. Scene IDs therefore survive full application restart and remain stable across rename/reorder operations.

The durable authoritative checkpoint continues to store only confirmed live authority: `ActiveSceneId`, routing, output-role state and the optional versioned compositing snapshot. Scene definitions are not duplicated into every checkpoint. The existing checkpoint format remains readable because the compositing field is additive and optional. An older checkpoint that cannot prove the compositing portion of a Scene that now declares one recovers the routing authority but drops the unprovable active-Scene evidence.

Recovery validates the checkpoint's Scene identity and, where declared, its exact compositing snapshot against the Scene definitions loaded from the durable show project before accepting active-Scene evidence. On RuntimeHost replacement or full ControlHost/RuntimeHost restart, durable graphics/CG resources are re-admitted first and the already committed authoritative execution is then reapplied without advancing Production Revision. Runtime alignment requires both the ordinary authority reference and exact authoritative compositing evidence whenever compositing is governed.

The separation remains explicit: a Scene existing in the durable project is authored intent, while `ActiveSceneId` is confirmed live evidence only after the normal Control/Runtime commit path succeeds.

## Compatibility boundary

Existing Set Preview, CUT, DISSOLVE/AUTO, cue, recording and graphics workflows remain functional. Graphics operations that affect Scene-governed layer state now update the same Control-owned compositing evidence after Runtime confirmation rather than leaving `ActiveSceneId` stale.

This contract does not introduce:

- a show-control sequencer;
- timed Scene playback;
- macros or scripting;
- activation from a selection-changed event;
- audio mixer/breakaway recall;
- generalized output-role recall;
- a second Scene, graphics or Runtime authority;
- Operator-owned production truth.

Additional production state may enter Scene recall only after it can be represented, validated, prepared, committed, persisted and recovered under the same authoritative transaction boundary.
