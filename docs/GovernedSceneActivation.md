<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Governed Scene Activation

## Purpose

Governed Scene Activation adds a Scene contract to the existing Control-owned production state without creating a second production authority. Scene selection remains Operator presentation state; Scene activation is an explicit production mutation.

## Scene model

A Scene has:

- a stable `SceneId`;
- a human-readable name;
- a reproducible desired `ProductionRoutingState`.

The Scene contract intentionally includes only Preview and Program routing. Governed Aux output is owned by the separate output-role contract and command path, so Scene activation does not silently change Aux routing. Graphics and other Runtime-managed presentation state are likewise not bundled into Scene activation without an explicit atomic contract.

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
- revision exhaustion;
- existing provider/capability planning requirements.

Unknown Scenes, stale revisions, invalid dependencies, Runtime prepare rejection, Runtime transport failure and Runtime commit rejection all fail closed.

Direct routing commands such as Set Preview, CUT or DISSOLVE remain valid. Because they create production state outside a Scene activation, they clear `ActiveSceneId`; the Operator must not continue presenting a Scene as active when its exact governed state is no longer the source of the confirmed authority.

## Operator evidence

The Operator snapshot carries:

- the Scene catalog;
- authoritative Preview and Program routing;
- optional confirmed `ActiveSceneId`.

The LIVE Scene list therefore distinguishes:

- local selected Scene;
- Scene whose declared Preview source matches the confirmed Preview route;
- Scene explicitly confirmed as active by `ActiveSceneId`.

Only the last state is presented as ACTIVE/LIVE Scene evidence.

## Durability and recovery

The durable authoritative checkpoint stores `ActiveSceneId` alongside the committed routing state. Older checkpoints without that field remain readable as having no confirmed active Scene.

Recovery validates the recovered Scene identity against the current production specification before accepting the checkpoint. Runtime restart/reconciliation continues to use the existing committed authority reference. Recovery does not synthesize Scene activation or advance Production Revision.

## Compatibility boundary

Existing Set Preview, CUT, DISSOLVE/AUTO, cue, recording and graphics workflows remain independent and functional.

This contract does not introduce:

- a show-control sequencer;
- timed Scene playback;
- activation from a selection-changed event;
- a new output-routing authority;
- partial graphics/output mutation presented as an atomic Scene;
- Operator-owned production truth.

Future Scene expansion must first bring additional production state into the same authoritative transactional contract before that state can be included in an atomic Scene.
