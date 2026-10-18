<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Shared Operator control presentation

## Scope and compatibility

The shared theme keeps all existing resource keys, font families, semantic Preview/Program/Armed/Healthy roles and persisted reference geometry. This change is limited to presentation. Native WPF command routing, text editing, IME, selection and undo remain the underlying mechanisms. Native file dialogs are unchanged. No production command, source, clock, persistence schema or version target changes.

## Additive resources

| Key | Value | Consumer / meaning |
| --- | --- | --- |
| OperatorMinimumTargetSize | 24 DIP | Shared control target reference; dense controls and splitters require individual geometry review |
| OperatorLiveActionHeight | 40 DIP | Larger primary action density for consuming feature styles |
| OperatorLiveActionFontSize | 16 DIP | Primary action type scale |
| OperatorReadableStatusFontSize | 12 DIP | State labels and numeric-unit text; legacy fine-print keys remain available |
| OperatorFocusBorderThickness | 2 DIP | Additive focus border metric |
| OperatorFocusThickness | 2 DIP | Existing keyboard-focus adorner metric, strengthened in place |
| OperatorDisabledOpacity | 1 | Avoid fading disabled text below its designed contrast |
| OperatorMotionDuration | 150 ms | Bounded shared entrance duration; helper clamps to 120–180 ms |
| OperatorColorAccessibleMutedText | #A9B6C1 | Muted text brush on existing dark surfaces |
| OperatorColorAccessibleDisabledText | #A9B6C1 | Disabled text brush on existing dark surfaces |

The legacy 8/9-DIP fine-print resources are not removed. Critical shared state labels and numeric-unit labels use the readable status resource. Complete feature typography and primary-action adoption still require their owning feature review; resource availability alone is not a claim that every view has migrated.

## Immediate state and accessibility

OperatorControlPresentation exposes IsPending, DisabledReason and ErrorText, with an immediate StateDescription. Shared interactive styles project this into automation ItemStatus/HelpText and keep disabled tooltips available. Validation uses a shared error border. Custom status and empty/recovery state peers expose Text roles, names, state and help rather than only colored indicators.

Interactive controls retain their WPF automation patterns and native keyboard/command behavior. The shared focus adorner is restored instead of being suppressed. High Contrast follows system preference changes during loaded-control lifetime; control state triggers use system text/background/border brushes. Native editor context menus use shared MenuItem chrome, explicit text-editor command targets and a session-local Reduce motion action routed to the owning window.

## Optional motion and ownership

Only explicitly opted-in, non-authoritative entrance chrome is animated. Current productive adoption is the selection-menu/context-menu presentation panel. Enabling AnimateEntrance does not change visibility, keyboard eligibility, CanExecute or commands. The owner continues to decide opening and closing; closing is immediate. No exit animation holds focus or an invisible popup open.

A loaded-element session owns at most one opacity animation clock. New entrances replace obsolete clocks. Reduced Motion, hidden/unloaded state, High Contrast, disabled system animation and explicit disposal remove the current clock. System preference handlers attach while loaded and detach on unload/disposal. No polling timer, composition rendering subscription, layout animation or Runtime observation is added.

The local preference is inherited window presentation state and is not persisted into shell/production files. It can reduce motion even when Windows permits animation, but cannot override a system request to suppress it. Popup boundaries explicitly bind that preference from their owner.

## Qualification and retained gaps

The existing real MainWindow STA fixture is extended with actual themed fields/buttons/menus, resource contrast calculations, automation roles/names/states, native undo/IME-enabled checks, 100 interrupted dropdown cycles, clock cancellation and subscription cleanup. The pointer test calls the standard ButtonBase click entry; it does not inject a physical mouse. Keyboard activation uses the focused real WPF control.

The engineering targets are 4.5:1 normal text and 3:1 focus on the actual shared dark surface brushes. The tests log measured resource-pair ratios. This is not WCAG certification or evidence for every feature-specific foreground/background. System High Contrast branch injection is software coverage, not a physical Windows accessibility review.

Run the canonical Release developer build, then OperatorXamlStartupTests and the full Operator suite. Existing Required Gates include those checks; no extra workflow or parallel Application harness is introduced. All five final-head gates, physical mixed-DPI, actual pointer/IME/screen-reader review and feature-specific compact hit areas remain separate evidence obligations. Six-DIP shell splitters and existing shell geometry are preserved; a universal 24×24 target claim is not made.

At draft preparation no Windows build or tests have run. The existing baseline Quality failures in unrelated allocation/recovery tests remain independent; do not weaken thresholds or rerun unchanged code until green. No release/hardware readiness is implied.
