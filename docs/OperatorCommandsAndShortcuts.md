<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->
# Commands and shortcut profiles

Open **Commands** in the navigation rail or press **Ctrl+K** from the main Operator or a detached panel. The owned palette uses the same command instances and confirmed state as the existing controls. It creates no control connection, renderer or monitoring subscription.

Search matches command labels, IDs and categories with bounded fuzzy matching (128 query characters, 96 catalogue entries, 64 displayed results). It includes workspace navigation, media search and library actions, viewer/docking commands, local timeline/Inspector drafts and existing production commands. Up/Down selects results. Disabled entries remain readable and explain disconnection, stale state, pending confirmation, readiness or missing Preview context.

Enter in search or results can execute navigation and local draft actions. AUTO, CUT, recording, GO, Preview transport and catalogue mutations require deliberate activation of **Execute** by mouse or keyboard after leaving the text field. Execute is not a default button. Ctrl+Enter, typing, key repeat and profile editing never forward to the production shortcut router. Existing command CanExecute and readiness rules are checked again at execution; visible results never grant authority.

Escape or Ctrl+K closes the palette. Cancellation restores the previous visible control's focus. Successful navigation keeps the destination focus; Ctrl+F reveals the MEDIA tools when required and focuses the existing media search. Detached panels share the central registry. Normal text fields retain typing suppression, numeric-edit Escape handling and local timeline editing precedence.

## Profiles

Select **Shortcut profiles**, choose an editable command, modifiers and key, then **Assign**. Collision errors explain the conflicting gesture and leave the whole active profile unchanged. **Remove override** restores one canonical binding; **Reset defaults** restores all canonical bindings. The profile name is saved when an assignment/removal is applied. **Import** applies a validated JSON preset and saves it locally; **Export** writes the active profile.

TAKE/AUTO, CUT, recording, GO, palette, synchronization, media search and fullscreen/recovery command IDs are reserved. Overrides accept Ctrl or Ctrl+Shift with permitted letters, digits or function keys, excluding editing, recovery and system keys. Windows/Alt/AltGr, bare navigation and Enter cannot be imported as overrides. Imported bindings cannot change command authority, parameters, focus scopes or text-entry policy. Same gestures in disjoint local scopes are valid; global/local overlaps and duplicate IDs are rejected.

The separate UI preference file is `%LOCALAPPDATA%/rtaime/operator-shortcuts.json`: version 2, at most 64 overrides, a printable name up to 64 characters, maximum 64 KiB. Version 1 imports migrate without inventing production values. Unknown versions/IDs, invalid keys, duplicates, null bindings and corrupt/oversized files are rejected before activation. Failed imports preserve active bindings and saved preferences. Startup failures retain defaults and show a status; they do not overwrite the unreadable file. Saves are serialized and atomically replace the preference file. A save failure reports that the profile is active only for the current session. Production/checkpoint data is never stored or edited here.

## Qualification and limits

OperatorShortcutProfileTests and OperatorCommandPaletteTests cover reserved keys, overlapping focus scopes, atomic validation, v1 migration, bounded persistence, rejected imports, repeat/text-entry suppression, stale/denied execution and explicit activation. The real STA OperatorXamlStartupTests loads the compiled palette, checks disabled CUT, Enter suppression, workspace navigation, compact profile layout and Escape. The palette reuses native monitor/work-area clamping and the existing custom controls, dark caption and keyboard focus treatment.

CI, Quality, Security, Provider Smoke and Packaged E2E must pass for the exact source head, including the packaged lifecycle and legacy checkpoint recovery. Hosted tests do not qualify physical mixed-DPI movement, keyboard layouts/IME, screen-reader interaction or hardware latency; these remain UNVERIFIED. Source checks alone do not grant release readiness or change NO-GO.
