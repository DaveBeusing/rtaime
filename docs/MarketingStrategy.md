<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="220" />
</p>

# rtaime Marketing Strategy

## Purpose

This document defines the product-marketing strategy for **rtaime** — pronounced **“realtime”**, expanded as **Real Time AI Media Engine**.

The canonical slogan remains:

> **Production-grade real-time AI media platform.**

The strategy aligns product positioning, audience, messaging, proof, launch sequencing, content and brand usage with the actual product and qualification state.

## Strategic position

rtaime should not enter the market as “another media server with more layers”.

The strongest differentiated position is:

> **A production-control-first real-time media platform that combines deterministic execution, operator trust and governed AI without giving AI authority over Program.**

This position is grounded in the current product architecture:

- ControlHost remains Production Authority.
- RuntimeHost executes prepared production state.
- AIHost is optional to continuity and cannot own Program state.
- Preview/Program, CUT and DISSOLVE use governed production commands.
- media, graphics, audio, recording and outputs are integrated into the same production path.
- health, hardware telemetry, lifecycle, recovery and release evidence are visible operating concerns rather than hidden implementation details.
- the product is packaged and exercised through an authoritative release and Packaged E2E path.

## Market context

As of September 2026, established real-time media platforms commonly lead with combinations of show reliability, high-resolution playback, mapping, creative flexibility, distributed operation and increasingly generative or AI-assisted content workflows.

Examples of current public positioning include:

- Disguise: synchronized high-resolution playback, generative content workflows and production hardware/software integration.
- Hippotizer: show-oriented reliability, real-time media manipulation, mapping, media management and multi-system control.
- PIXERA: distributed media-server architecture, synchronized high-resolution canvases and large-scale show operation.
- TouchDesigner: real-time interactive and reactive media creation.

Reference sources:

- [Disguise](https://www.disguise.one/)
- [Hippotizer](https://www.green-hippo.com/hippotizer-media-servers/)
- [PIXERA](https://pixera.one/)
- [TouchDesigner](https://derivative.ca/)

rtaime should therefore avoid competing only on generic playback or creative-tool language. Its marketing advantage is the combination of **production authority, real-time media execution, governed inference, recovery, observability and evidence-driven readiness**.

## Category

Primary category:

**Real-time AI media platform**

Secondary descriptors:

- production media platform
- real-time production engine
- software-defined media-server platform
- governed AI media runtime

Use **media server** when it helps buyers map rtaime to an established category, but do not reduce the brand to a playback appliance. The product architecture is broader than playback alone.

## Primary audiences

### Production technology leaders

Examples include broadcast engineering leads, heads of production technology, technical directors, systems architects and innovation leaders.

Primary need:

A credible path to introduce AI and software-defined workflows without weakening production authority, recovery or operational trust.

### Media-server and show-system specialists

Examples include media-server programmers, video engineers, live-event system engineers, experiential technologists and virtual-production engineers.

Primary need:

Fast, understandable control of media, graphics, timing, outputs and system state under show conditions.

### Integrators and solution partners

Examples include broadcast system integrators, live-event integrators, experiential production companies and specialist AV engineering teams.

Primary need:

A modular platform with explicit lifecycle, health, packaging and operational boundaries that can be evaluated and integrated methodically.

### Technical evaluators and design partners

Primary need:

Evidence. They need to see what is implemented, what is qualified, what remains unverified and how the system behaves when components fail.

## Positioning statement

For production teams building the next generation of live, broadcast and experiential workflows, **rtaime** is a production-grade real-time AI media platform that unifies deterministic control, media execution, operator workflows and governed inference.

Unlike creative tools that add AI as an unconstrained effect path, or conventional media-server messaging centered mainly on playback capacity, rtaime is designed around explicit production authority, observable health, bounded recovery and evidence-backed readiness.

## Elevator pitch

**rtaime brings video, audio, graphics, recording, outputs and governed AI into one production-shaped real-time platform. Its architecture keeps Program authority deterministic, makes runtime health visible to the operator and treats AI as a controlled capability rather than an unchecked production owner.**

## Message pillars

### 1. Trust the Program

Core message:

**Production state has one authority. Runtime execution is explicit, transactional and observable.**

Supporting proof:

- ControlHost authority
- Runtime prepare/commit boundary
- authoritative revision handling
- fail-closed mutation behavior
- process recovery and resynchronization
- Preview/Program production workflow

### 2. Operate the whole media path

Core message:

**One operator experience spans media, timing, graphics, audio, recording, output and health.**

Supporting proof:

- media library and local media source
- transport, timeline, IN/OUT and Cue points
- Preview/Program with CUT and DISSOLVE
- compositing and graphics
- Audio Follow Video
- Program Recording
- output routing and monitoring
- production health and performance surfaces

### 3. AI without surrendering control

Core message:

**AI can influence media. It does not own Program.**

Supporting proof:

- governed inference
- bounded request context and resource budget
- timed inference results
- freshness and fallback policy
- AIHost optional to production continuity
- visible reference effect through the normal composition path

### 4. Operational trust is a feature

Core message:

**The operator should know what is ready, degraded, recovering, unavailable or unverified.**

Supporting proof:

- unified AppHost lifecycle
- startup readiness
- health center
- CPU, GPU and memory telemetry
- notifications and session recovery
- process supervision
- evidence-aware release qualification

## Product narrative

The recommended external narrative is:

1. **Real-time media is becoming software-defined.**
2. **AI is becoming part of the production chain.**
3. **Production cannot hand authority to probabilistic systems.**
4. **rtaime separates authority, execution and inference by design.**
5. **The result is a platform that can become more intelligent without becoming less controllable.**

This narrative should be used consistently across README, presentations, demo scripts, website copy and launch content.

## Current proof story

The current public story should emphasize implemented software capabilities rather than future commercial claims.

### Implemented

The repository currently contains production-shaped software paths for:

- unified application startup and lifecycle ownership;
- ControlHost, RuntimeHost and AIHost supervision;
- Preview/Program operation;
- CUT and DISSOLVE;
- local media playback and transport;
- timeline, Cue and marker workflows;
- graphics/compositing;
- Audio Follow Video;
- recording;
- governed Program/Aux output roles and monitoring;
- governed AI showcase integration;
- hardware/runtime health and performance visibility;
- recovery and session-state handling;
- release packaging, update/rollback foundations and qualification evidence.

### Qualified by repository automation

The repository has automated gates for:

- CI
- Quality
- Security
- Provider Smoke
- Packaged E2E

These prove software behavior within their declared environments and boundaries.

### Claims that remain evidence-bound

Do not market rtaime as:

- STABLE, VALIDATED or CERTIFIED unless the corresponding release evidence says so;
- physically qualified on a GPU, capture/output device or reference machine without captured qualification evidence;
- zero-frame-failover;
- a replacement for every established media-server workflow;
- production-certified AI;
- autonomous production control.

Use **UNVERIFIED** where the evidence model requires it.

## Go-to-market sequence

### Phase 0 — Credibility foundation

Current stage.

Objectives:

- make the repository and README understandable in under two minutes;
- establish a recognizable visual identity;
- publish the architecture and product thesis;
- demonstrate real workflows instead of concept slides;
- expose qualification boundaries clearly.

Primary assets:

- branded GitHub README
- Product Identity
- Brand Assets
- Marketing Strategy
- architecture diagrams
- Product Showcase
- short feature demonstrations
- technical deep-dives

### Phase 1 — Design-partner program

Target a small group of technically sophisticated production teams, integrators and media-server specialists.

Offer:

- guided technical demo
- structured requirements sessions
- reference workflow evaluation
- qualification feedback
- integration-gap discovery

Success signals:

- repeated usage without developer intervention;
- requested integrations that recur across partners;
- operators understand system state without architecture explanation;
- design partners can describe the rtaime differentiation in their own words.

### Phase 2 — Preview product

Entry conditions:

- Preview release channel is publication-ready under repository policy;
- reference hardware qualification is sufficiently mature for the intended claims;
- installation, update, rollback and recovery are repeatable;
- demo workflow no longer depends on developer context.

Marketing shifts from **architecture proof** to **product evaluation**.

Primary CTA:

**Request a technical evaluation / run the Preview candidate.**

### Phase 3 — Commercial launch

Entry condition:

A STABLE candidate has the required trust, release and hardware evidence for the claims being made.

Marketing shifts from **evaluate the platform** to **deploy with confidence**.

Commercial assets should then add:

- supported hardware matrix
- deployment guides
- support model
- integration catalog
- case studies
- commercial packaging and licensing
- validated performance envelopes

## Marketing-critical commercialization priorities

The current product is strong enough for technical positioning and design-partner conversations, but several repository-declared boundaries should be treated as commercialization priorities before broader deployment claims.

### 1. Reference hardware qualification

Current physical GPU, Media I/O, latency, reference-lock and long-soak evidence remains bounded by the qualification system and is not universally PASS.

Marketing priority:

- publish a supported reference configuration only after retained evidence exists;
- convert measured results into a concise supported-performance matrix;
- keep CI performance results separate from physical-platform claims.

### 2. Professional recording and delivery formats

The Windows recording path now implements MP4 delivery with H.264/AVC video and AAC-LC stereo 48 kHz audio while retaining the deterministic rtaime reference artifact as a separate evidence backend. Repository qualification independently reopens finalized MP4 output through the existing Media Foundation decoder and verifies both supported 1080p development frame rates, repeated recording, A/V timestamp alignment and controlled failure isolation.

Marketing priority:

- describe MP4/H.264/AAC only within the tested Windows/software qualification boundary;
- do not imply MOV/MXF support, hardware-encoder guarantees, sustained professional storage throughput or long-duration physical-platform qualification;
- publish codec/container compatibility only from retained passing evidence.

### 3. Output, streaming and scene-control breadth

Program and Aux now have governed backend output-role contracts with Runtime/provider evidence. Authoritative external streaming/on-air state and additional physical/network output providers remain unavailable or unverified in the current V1 scope.

Marketing priority:

- sell the current Preview/Program workflow plus governed Program/Aux output roles and monitoring as implemented software capabilities;
- distinguish the local Clean Program monitoring window from governed physical/output-provider state;
- treat streaming protocols, additional physical/network providers and external on-air state as roadmap capabilities until corresponding provider contracts and qualification evidence exist;
- do not create decorative UI claims to imply unavailable backend capability.

### 4. Remote and ecosystem integration

Local Named Pipes are the qualified V1 control transport. TCP, HTTP, gRPC, TLS, cluster discovery, NMOS and remote-network deployment remain unverified.

Marketing priority:

- do not market an “open remote API” before a qualified external control boundary exists;
- prioritize the integrations repeatedly requested by design partners;
- publish an integration catalog only when those interfaces have stable contracts and qualification evidence.

### 5. Trusted Preview and Stable release

The repository currently identifies the product as 0.1.0-dev / DEV. Production signing trust, trusted public Preview publication and STABLE status require their corresponding release evidence.

Marketing priority:

- Phase 0 messaging should invite evaluation, not deployment;
- Preview messaging begins only when a publication-ready Preview candidate exists;
- commercial deployment language begins only after the required Stable/trust boundaries are satisfied.

### 6. Support and deployment policy

The repository does not yet establish a production support-period declaration.

Marketing priority:

- define supported Windows/reference configurations;
- define support lifecycle and update expectations;
- define escalation/support ownership before commercial launch;
- keep community/project availability separate from a commercial support promise.

### 7. Customer proof

Architecture and automated evidence establish technical credibility, but they are not substitutes for customer proof.

Marketing priority:

- use the Product Showcase to recruit design partners;
- convert repeated design-partner success into named workflow evidence;
- publish case studies only after the customer and measured claims can be substantiated.

These priorities should guide roadmap sequencing because each one unlocks a stronger class of external claim.

## Channel strategy

### GitHub

Role: technical credibility and product proof.

Content:

- branded README
- architecture
- release evidence
- build/test entry points
- Product Showcase
- clear current-status boundaries

### LinkedIn

Role: category creation and professional awareness.

Recommended recurring themes:

- why AI should not own Program
- production authority versus runtime execution
- what “UNVERIFIED” means and why it matters
- designing operator trust
- real-time lifecycle and recovery
- media-server architecture beyond playback
- measured performance only when evidence is available

### Video

Role: show the product faster than prose can explain it.

Priority formats:

- 60–90 second product overview
- five-minute Product Showcase
- operator workflow clips
- failure/recovery demonstrations
- architecture explainers

Every video should show the actual rtaime UI and use the canonical visual identity.

### Industry events

Priority environments:

- broadcast
- live production
- experiential
- virtual production
- media-server and AV integration communities

The demo should focus on one memorable thesis:

> **AI can participate in Program without owning Program.**

### Direct design-partner outreach

The highest-value early conversations are technical rather than broad lead-generation campaigns.

Lead with architecture, operational behavior, failure handling, integration needs and evidence. Avoid leading with speculative feature roadmaps.

## Content system

| Pillar | Example story | Proof source |
| --- | --- | --- |
| Production trust | Why Program authority is separated from execution | Control/Runtime architecture |
| Real-time operation | Preview → TAKE → DISSOLVE → output | Operator + Runtime |
| Governed AI | AI effect with clean fallback | AIHost + Runtime showcase |
| Operator experience | Startup, health, recovery and notifications | Operator UI |
| Platform engineering | Packaging, lifecycle and recovery | AppHost + release pipeline |
| Performance | Measured latency, cadence and hardware utilization | Qualification evidence only |

## Demonstration strategy

Use **Product Showcase** as the canonical external demonstration narrative.

Internal filenames and qualified tooling may retain established names where changing them would break automation.

The Product Showcase should tell one continuous story:

1. start rtaime;
2. observe real lifecycle readiness;
3. load media;
4. prepare Preview;
5. TAKE to Program;
6. execute a DISSOLVE;
7. manipulate graphics;
8. show audio following video;
9. activate the governed AI effect;
10. demonstrate fallback/recovery;
11. record Program;
12. show output and system health.

The presenter should never need to explain away an unresolved red state.

## Visual brand strategy

### Primary mark

Use **RtaimeLogoHorizontal.svg** for README hero, website hero, presentations, product one-pagers and showcase title surfaces.

### Emblem

Use **RtaimeEmblem.svg** for compact headers, navigation, social avatars where the full lockup is too wide and product UI accents.

### Monochrome emblem

Use **RtaimeEmblemMonochrome.svg** for neutral documentation contexts, print-like applications and constrained backgrounds.

### App icon

Use **RtaimeAppIcon.svg** for executable/app identity, launcher tiles, installer/product shortcuts.

Do not invent parallel marks or recolor the canonical vector files for individual campaigns.

## Brand voice

rtaime should sound:

- technical but understandable;
- confident but evidence-bound;
- precise rather than hyperbolic;
- modern rather than futuristic-for-its-own-sake;
- production-focused rather than research-focused.

Prefer deterministic, governed, observable, production, real-time, authoritative, bounded, qualified, resilient and operator.

Avoid unsupported superlatives such as revolutionary, flawless, zero-latency, unlimited, fully autonomous or certified.

## CTA hierarchy

Current development stage:

1. **Explore the platform**
2. **Run the Product Showcase**
3. **Review the architecture**
4. **Evaluate the production model**

Preview stage:

1. **Request a demo**
2. **Evaluate the Preview**
3. **Discuss an integration**
4. **Join the design-partner program**

Stable commercial stage:

1. **Talk to sales**
2. **Plan a deployment**
3. **Review supported configurations**
4. **Request a technical workshop**

## Measurement framework

Track the funnel by intent rather than vanity metrics.

### Awareness

- qualified profile visits
- README/product-page engagement
- technical video completion
- relevant industry mentions

### Evaluation

- demo requests
- technical workshop requests
- Preview downloads when available
- return visits to architecture/qualification material

### Design-partner activation

- active partner evaluations
- completed showcase sessions
- recurrent requirements across partners
- integration prototypes
- operator feedback themes

### Product readiness

- successful packaged showcase runs
- required-gate stability
- reference-platform qualification coverage
- installation/update/recovery success rate

## Documentation rule

Every product-facing document should reinforce the same hierarchy:

1. **rtaime**
2. **Real Time AI Media Engine**
3. **Production-grade real-time AI media platform.**
4. one specific document purpose
5. explicit proof and qualification boundaries where claims are made

The visual identity should be present without overwhelming technical content. Marketing must make the architecture easier to understand, not obscure it.
