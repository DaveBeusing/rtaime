<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
All rights reserved.
-->

# Compositing Node Graph

## Purpose

The COMPOSITING workspace presents the currently observable production path as a compact, read-only-first node graph. It is an Operator projection over existing source, routing, graphics, Runtime health and recording state; it is not a second execution or routing authority.

## Projected topology

The graph uses stable node identities and projects the following existing product concepts:

- current production sources;
- Preview / Program routing;
- Preview monitoring;
- the loaded graphics layer and its existing X / Y / Scale transform;
- the GPU composite stage represented by the qualified Runtime composition path;
- Program output;
- Program recording.

Connections are directional and are rendered separately from node controls. Inactive optional paths, such as a hidden graphics layer or an idle recorder, remain visible with reduced emphasis so the Operator can understand the complete available path without mistaking it for an active signal route.

## Authority boundary

CompositingGraphProjector is a deterministic Client-side projection over already observed state. It does not own Runtime execution, routing, media processing, graphics composition or recording.

CompositingGraphViewModel consumes the existing OperatorViewModel projection and updates existing node instances by stable identity. Status refreshes therefore retain presentation positions. Source collection changes may trigger deterministic Auto Layout, while ordinary live status updates do not reposition nodes.

The current Runtime and Control contracts do not expose arbitrary topology rewiring. The workspace therefore exposes REWIRE as visibly disabled and all graph nodes report read-only capability to the shared Inspector. No local graph action is allowed to imply an authoritative topology mutation.

## Operator interaction

The graph toolbar provides:

- SELECT for node inspection;
- PAN for left-button canvas movement;
- FIT for fitting the current graph into the viewport;
- 100 % for presentation reset;
- AUTO LAYOUT for deterministic presentation layout;
- REWIRE as a disabled capability indicator.

Mouse-wheel zoom is centered on the current pointer. Middle-mouse panning is available independently of the active interaction mode.

Selecting a node projects that node into the existing shared Inspector. Selecting a source node also updates the existing non-destructive Operator source selection, but never performs Set Preview, CUT, AUTO or another routing mutation.

## Health presentation

Node health is derived from already exposed source, Runtime, GPU, commit, graphics and recording observations. Explicit failure/offline/error evidence is shown as an error state. Degraded, unavailable, unverified, stale or startup evidence is shown as degraded rather than falsely green.

## Testing

The projection is covered for node and connection mapping, stable identities across status updates, error/degraded state, read-only rewiring and a larger-graph performance smoke. Operator UI policy checks protect the presentation-only dependency boundary and the integration with the shared Inspector.
