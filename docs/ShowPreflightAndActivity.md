<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->
# Show checks and activity

Open **Show checks** in the navigation rail, or search for **Show preflight** in Commands (Ctrl+K). This modeless view combines advisory evidence, recent Control requests and the existing notification service. Tab navigates its controls; Escape closes it and returns focus. Review links close the view and navigate to the owning workspace. It uses existing custom controls and dark resources, and scrolls at compact sizes without creating another monitor or Program renderer.

## Existing authority and diagnostics

Health Center remains the incident and recovery service. Support bundles and startup diagnostics retain their existing capture paths. Show template dependency/conflict preview remains the established template preflight. Saved show state and journal recovery are unchanged. ARM/GO, rundown, macros, audio, effects and output changes still pass through Client and ControlHost; no check in this view enables a show launch or grants a local ready flag.

## Advisory preflight

Nine checks consume already published evidence. Missing or stale evidence is displayed as **UNVERIFIED** (the existing `Unknown` model state), never PASS:
- **Show assets:** media references in the authoritative selected cue list versus the read-only catalogue snapshot. Missing/offline assets fail the check; absent or expired catalogue evidence is UNVERIFIED. No media references is NOT APPLICABLE.
- **Cue references:** current source, scene, layer and output identities, plus markers for the loaded asset. Unloaded-asset marker evidence is UNKNOWN. This is a structural observation, not a second execution validator.
- **Output roles:** published role health and authoritative active state. Missing, inactive or unqualified roles are UNKNOWN; explicit failure is FAIL. A monitor image never proves physical output readiness.
- **Program output / AUX outputs:** separate checks keep a healthy Program route from concealing an AUX failure, or the reverse. A missing role kind is UNVERIFIED; every observed role of that kind must be active and PASS. These remain published-role observations, not physical certification.
- **Recording target:** only an active Runtime recording with an identified target/profile/provider and confirmed samples written can pass. Explicit writer failures fail. Idle, unavailable, unprobed or unwritten targets remain UNVERIFIED; a locally entered path does not establish writability or capacity. The check never probes the filesystem, starts a recording or modifies a target.
- **Program audio:** the existing Health Center audio projection, with its warning/failure/unknown distinctions.
- **AI readiness:** current enabled-effect evidence, a published provider identity, matching source/applied sequences and reported failure. Missing evidence is UNKNOWN; explicitly disabled AI is NOT APPLICABLE. Provider and physical inference qualification remain separate.
- **Saved show:** the existing durable-project state; this view never saves, rewrites, migrates or restores production.

The view rejects authoritative, catalogue and audio observations older than ten seconds. **Refresh checks** only reevaluates already received evidence locally; it sends no catalogue or production request. The Media Library supplies its snapshot and original receipt timestamp. The existing server catalogue query also refreshes availability, so preflight deliberately does not invoke it. Use Media Library's established explicit refresh/relink workflow to update missing assets. Disconnect/stale connection invalidates retained catalogue evidence until Media Library receives a new snapshot. The displayed production revision and observation timestamp come from the last completed authoritative synchronization.

## Correlated session history

The last 128 Client mutation requests retain a generated request ID, operation name, pending timestamp, response timestamp, outcome and the production revision observed before sending. The five metadata-bearing production commands additionally retain their real wire command ID and response production revision. Each entry also captures the observed production identity, snapshot receipt time and a local Client observation epoch at submission. Disconnect starts a new local epoch without rewriting pending or completed entries; this is explicitly not a server/Runtime epoch. The current public Control contract does not expose that server epoch. APIs that do not expose a revision display **not exposed**, rather than guessing from a later snapshot.

PENDING means a request is in flight. Explicit boolean mutation/recording/catalogue responses show ACCEPTED/SUCCEEDED or REJECTED/FAILED; other returned values show response receipt with acceptance not exposed. Runtime commitment/completion remains explicitly not exposed by this receipt rather than being inferred from a later revision. CONFIRMED means its Control response was received; scheduled cue execution and Runtime visibility still follow their own evidence. Explicit negative mutation/recording/catalogue responses are REJECTED. Lost, cancelled or exceptional responses remain UNCONFIRMED, because an exception does not prove that the server did not apply an action. Inspect authoritative state before retrying. A later snapshot-refresh failure cannot erase a response already received, and reconnect does not replay or reclassify history.

History is session-only, with no disk or durable schema changes. It retains no arguments, authored names, source URLs, credentials, paths, response payloads, rejection text or exception messages. It is not an audit log and does not expose operations from other clients. The oldest observation ages out at capacity, even if its response arrives later.

## Consolidated notifications

The existing service retains at most 64 keyed notifications, with at most four transient toasts. Duplicate conditions increment a saturating occurrence count. Capacity eviction prefers older low-priority or acknowledged entries over fatal/persistent incidents. Text and technical detail are bounded; no external navigation URI is accepted.

Notifications are grouped as FATAL, ERROR, WARNING, RECOVERY, INFORMATION and SUCCESS over the same 64 retained records. Fatal conditions appear first, regardless of category. Grouping adds no independent feed or persistence.

**Pause reminders 5 min** affects presentation reminders only. The condition stays visible in its group and in the existing persistent active-problem list. Fatal conditions cannot be paused. Identical observations preserve the deadline, materially changed conditions resume reminders immediately, and the existing expiry timer resumes reminders at the deadline. **Resume reminders** ends the pause early. Neither action acknowledges or resolves a fault; no reconnect, production or launch policy changes.

**Acknowledge** records presentation acknowledgement while retaining the active/fatal condition. Identical observations preserve it; a materially changed condition resets it. **Dismiss** removes presentation; a subsequent observation can publish the condition again. Neither operation changes Health, recovery state, production revision or launch policy. **Review** navigates only to an existing workspace. **Export support bundle** delegates directly to the existing startup/System exporter, picker, availability gate and visible result status. It does not add an exporter or include action arguments in history.

Technical details remain opt-in and expanded state survives duplicate refreshes.

Free-form footer events are informational. Correlated response outcomes drive informational/negative command notifications rather than keyword matching in event text; response receipt alone is not an execution-success claim. UI projections coalesce queued updates and keep bounded storage; the advisory freshness update runs once per second.

## Qualification

Focused domain tests cover missing/stale evidence, references, media availability absence, audio freshness, inactive outputs, wire/revision correlation, rejection, uncertain completion, reconnect, refresh failure and history privacy/bounds. Notification tests cover capacity, duplicate acknowledgement, bounded snooze expiry/change/fatal handling, severity grouping and safe navigation. History regressions preserve submission provenance across a delayed reply and reconnect, and never invent acceptance or a server epoch. Preflight tests independently fail Program/AUX, reject unverified recording targets and mismatched AI sequences, and assert unchanged input snapshots. Operator tests cover a delayed dispatcher, incident presentation and disposal. The existing real MainWindow/BAML qualification opens the view, checks keyboard navigation, automation names and workspace deep links at 960×500 with injected 100/125/150/200% layout scales.

Injected scales verify software layout, not physical mixed-monitor hardware qualification. Required Gates and packaged lifecycle results must be checked at the exact source head. Physical hardware and external release qualification remain UNVERIFIED until their separate evidence exists.
