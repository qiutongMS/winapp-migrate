# Behavioral validation

Use this protocol to preserve observable behavior across the UWP-to-WinUI migration. It orchestrates installed tools; do not create helper scripts, temporary test projects, or app-specific CLI extensions.

The migration report uses schema `1.5`. The state plan below is a separate artifact and stays at schema `1.0`.

## 1. Plan evidence by behavior

After reading `migration-report.json` and the source, create a compact state plan covering:

- startup and initial navigation;
- each fast-path primary sentinel or expanded-path seam sentinel;
- each distinct top-level feature path;
- states that exercise migration-sensitive TODOs, bindings, data loading, selection, dialogs, or window-dependent behavior;
- protocol, file, toast, or command-line activation; suspend/resume and lifecycle transitions; background tasks; and secondary windows when the source uses them;
- user-provided critical flows.

Capture one state per distinct outcome or migration risk. Persist the plan at the path declared by the report, normally `<target>/.migration-evidence/state-plan.json`. Store runtime evidence under the report's source and target evidence roots. Do not commit evidence unless the user requests it.

Mark the primary sentinel in `featurePaths` with `sentinel:<flow-name>`. On the fast path, capture startup and that primary source sentinel before the first target edit. On the expanded path, capture every seam sentinel before implementing its architecture slice.

Use this schema `1.0` shape:

```json
{
  "schemaVersion": "1.0",
  "states": [
    {
      "id": "stable-state-id",
      "featurePaths": ["feature-or-flow"],
      "todoIds": ["UWMIG000"],
      "preconditions": ["required initial state"],
      "actions": ["ordered semantic action"],
      "expectedOutcome": "observable source behavior",
      "source": {
        "status": "not-run",
        "evidence": [],
        "reason": null
      },
      "target": {
        "status": "not-run",
        "evidence": [],
        "reason": null
      },
      "comparison": {
        "status": "not-run",
        "reason": null
      }
    }
  ]
}
```

Update the file after every attempted source capture, target replay, and comparison. Evidence entries are paths relative to `<target>`. Record implementation defects and runtime signatures in the separate [semantic finding ledger](semantic-migration-protocol.md) and link them to stable state IDs.

Optional schema `1.0` fields on each state are `slice` (defaults to its state
ID), `dependencies` (slice IDs, defaults to empty), and `criticality`
(`required`, the default, or `optional`). States in one slice share criticality;
their dependencies are combined, must exist, and must be acyclic. Follow only
the first dependency-ready semantically unresolved slice in the enforcement snapshot.
Required source states need captured evidence before target work. Required
blocked/unverified states remain incomplete; optional debt is never verification
and cannot unlock dependent slices.
Target changes invalidate evidence freshness without reopening resolved
implementation work. Closure revalidates stale resolved slices; genuine
comparison/runtime failures or open findings may reopen semantic work before
the run becomes terminal. Follow snapshot `disposition`: terminal outcomes
cannot be resumed by more operations or new target changes.

Set `source.status` to `captured`, `blocked`, or `unverified`; `target.status` to `passed`, `blocked`, `unverified`, or `failed`; and `comparison.status` to `verified`, `blocked`, `unverified`, or `failed`. Keep `not-run` only until the phase is attempted, and provide `reason` for every status other than `captured`, `passed`, or `verified`.

After writing the state plan and before launching the source, persist the
initial closure assessment:

```powershell
winapp-migrate uwp closure assess "<target>"
```

This establishes the source-oracle checklist frontier before any source or
target work. Elapsed time and operation totals are telemetry only and never
authorize or stop migration work.

For state-changing behavior, plan a transition rather than only a destination. Verify changed values, selection, collections, navigation state, or visible content. Include cancel/back restoration and alternate modifier-key paths when they produce different source behavior.

Split `expectedOutcome` into independently observable assertions and retain
direct evidence for every assertion on both sides. A reached page is not proof
that its navigation destination is selected. For selection, toggles, values,
focus, enabled state, and restoration, query the relevant UI Automation
property after the decisive action; do not infer it from nearby content or a
screenshot. A state cannot be `passed` or `verified` while an expected property
is false, absent, or supported only by indirect evidence.

For each visual state, retain:

1. ordered semantic actions;
2. a screenshot;
3. a JSON UI tree;
4. related migration TODOs or feature paths.

Retain screenshots as evidence files, but do not open every capture through a tool. Confirm progressive states from the corresponding UI tree and action result; reserve bounded visual inspection for final comparison.

## 2. Capture the original UWP app

Prefer user-provided baseline evidence when it already covers the plan. Otherwise:

1. Capture a pre-launch window inventory in its own command, retaining only `hwnd`, `processId`, `title`, and `className`.
2. Launch the exact source project separately:

   ```powershell
   winapp run "<source.csproj>" --arch <x64|arm64|x86> --detach --json
   ```

3. If project mode reports a build failure, use its structured diagnostics. Never edit the source project for baseline capture.
4. Treat a compiled and registered source that fails activation as a runtime failure. Make one diagnostic launch without incompatible `--detach` and `--json` options:

   ```powershell
   winapp run "<source.csproj>" --arch <x64|arm64|x86> --debug-output
   ```

5. If a Debug-only startup failure is plausible, make at most one Release attempt. After bounded recovery, record affected states as `unverified`.
6. Poll `winapp ui list-windows --json` and the returned PID for at most 10 seconds. A UWP top-level window can belong to `ApplicationFrameHost`; identify it by the before/after HWND difference, title, and timing. Never stop `ApplicationFrameHost`.

Capture states with the resolved HWND:

```powershell
winapp ui inspect -w <hwnd> -d 8 --json
winapp ui screenshot -w <hwnd> --focus --json -o "<evidence-path>.png"
winapp ui invoke "<semantic-name>" -w <hwnd> --json
```

Persist command JSON rather than relying on terminal scrollback. After each action, inspect again and confirm the expected state before taking its screenshot. Prefer visible semantic names because they can survive framework-specific AutomationId changes.

Use `blocked` only for a known external prerequisite such as unavailable permissions, data, hardware, or credentials. Use `unverified` after bounded capture or comparison finishes without usable evidence and no specific outstanding prerequisite remains.

After source capture, close the exact source HWND through its UIA Close element and confirm it disappears. If it has no UIA Close element, stop only the exact source PID returned by `winapp run`. Never clean up by broad process name.

After persisting the completed required source states, immediately run
`winapp-migrate uwp closure assess "<target>"` again and require
`mechanical-stabilization` before target implementation. The assessment closes
the source unit and activates the target checklist; do not perform target work
before this transition is persisted.

## 3. Build/register and replay WinUI 3

The target project must reference `Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer` with `PrivateAssets="all"`. The package is not bundled with this plugin and is not injected by WinApp CLI.

Build/register the packaged target and start its detached canonical replay
through the closure gateway:

```powershell
winapp-migrate uwp closure attest "<target>"
```

Pass `--configuration`, `--architecture`, `--framework`, `--runtime`, and
repeatable `--property Name=Value` arguments when required. The gateway
forwards the same selection to its `--no-launch --json` build and
`--no-build --detach --json` replay, and preserves raw output, grouped
diagnostics, exit codes, fingerprints, and attestations. Do not substitute a
wrapper script or copied analyzer DLL. Replay the active sentinel before
peripheral features.

For the target, never invoke raw `winapp run`, `winapp build`, or `winapp ui`.
Those calls bypass target fingerprinting, replay identity,
and evidence provenance, so their output cannot close a state. Source baseline
capture remains outside this target-only boundary.

If the detached app exits, turns blank, or never exposes a usable window, run
one foreground diagnostic launch through the gateway:

```powershell
winapp-migrate uwp closure diagnose "<target>"
```

The gateway invokes `--no-build --debug-output`, writes stdout and stderr under
`.migration-evidence/enforcement/diagnostics`, and records a diagnostic
development probe. That probe never satisfies canonical build, replay, parity,
or finalization gates. Derive a stable signature from the exception or HRESULT, the first
app-owned stack frame, and the WinUI triage verdict. A failed canonical replay
already records its runtime attempt; do not duplicate it. When a later target
UI action first exposes the failure, persist that action evidence and use
`winapp-migrate uwp closure record-failure`.

Before changing the target, diagnose source semantics in this order:

1. Map the failing target action to the corresponding source handler,
   constructor, navigation callback, or state transition.
2. Compare the complete source and target paths, including nearby comments,
   guards, catches, dispatcher ordering, initialization order, and lifecycle
   assumptions. Treat preserved source workarounds as behavioral contracts
   until disproved.
3. State one source invariant, one falsifiable hypothesis, and one
   discriminating test. Do not batch unrelated fixes or move to media/native
   stack speculation while an app-owned source/target difference remains.
4. Persist a compact source/target diff or investigation note under the target
   evidence root, created after the recorded failure.
5. Record the diagnosis before another build or replay:

   ```powershell
   winapp-migrate uwp closure record-diagnosis "<target>" "<signature>" `
     --state "<state-id>" `
     --source-location "<path:line>" `
     --target-location "<path:line>" `
     --source-invariant "<behavior or workaround to preserve>" `
     --hypothesis "<single falsifiable cause>" `
     --test "<one discriminating change or observation>" `
     --evidence "<fresh-target-relative-diagnosis-path>"
   ```

The gateway's `failure-diagnosis` gate exposes `diagnosis-required` and rejects
missing, incomplete, out-of-root, or pre-failure evidence. Diagnosis is bound to
the failing signature, state/slice, and target fingerprint; a completed record
cannot be overwritten or repeated for that signature/fingerprint. It then
exposes `change-required` until the target fingerprint changes. Apply only the change required by the recorded test,
then use gateway-controlled build and replay. A changed target fingerprint does
not excuse an undiagnosed runtime failure.

If diagnostics terminate without an app-owned cause, allow one focused temporary instrumentation pass at the narrowest existing app-owned constructors, handlers, and transition helpers. Remove all temporary instrumentation before the final analyzer-enabled build and clean replay.

Drive target UI through the current gateway-attested canonical replay:

```powershell
winapp-migrate uwp closure capture "<target>" -- list-windows --json
winapp-migrate uwp closure capture "<target>" -- inspect -w <hwnd> -d 8 --json
winapp-migrate uwp closure capture "<target>" -- invoke "<semantic-name>" -w <hwnd> --json
winapp-migrate uwp closure capture "<target>" -- screenshot -w <hwnd> --focus --json -o "<evidence-path>.png"
```

The target UI command refuses to run without a fresh canonical replay. Its
default output is a bounded receipt; raw stdout/stderr and a per-probe
`probe.json` remain under `.migration-evidence/enforcement/probes`. Read only
the relevant persisted payload instead of repeatedly emitting the full UI tree;
reserve `--raw-output` for a focused result that must be inline. During semantic
convergence, rely on this automatic provenance and update state evidence once
per completed state rather than authoring evidence for each UI command. When
the target exits only while an `invoke` action runs, or a native crash stack is
dominated by `UIAutomationCore` without an app-owned frame, test the same
control once with gateway-mediated `click` after a clean launch.

If `invoke` fails while pointer input succeeds, inspect the complete downstream path for overlapping fire-and-forget tasks, reentrant navigation, frame replacement, overlay removal, or disposal while UI Automation is still returning. Serialize and await app-owned transitions, prevent re-entry, and remove visual-tree elements only after the action and required exit transition complete.

If pointer input cannot run because the host lacks an interactive desktop or click support, record that limitation only for the input-method comparison. Keep the invoke-triggered app defect `failed`; do not mark it `blocked`.

Group all locations explained by one signature into one correction. A failed
build cannot repeat on an unchanged target, and replay/debug/hypothesis work
cannot be repeated without the required new target or discriminating evidence.
Each correction cycle must state one root-cause hypothesis the next probe can
disprove. Elapsed time and aggregate operation counts are telemetry only:
they never authorize, cancel, defer, or terminate otherwise valid work.
Progressive target changes may use as many informative operations as needed.
The initial build belongs to mechanical stabilization, the initial replay to
target bootstrap, and later semantic corrective build/replay attestations to
their active slice. Stage compatibility remains deterministic: source oracle
has no target operations, mechanical stabilization only builds, and bootstrap
permits no UI automation or diagnosis/hypothesis without a runtime failure.
Repeated closure replay requires fresh `--discriminating-evidence`; closure UI
uses one fresh intent per state sequence. Corrections made after semantic
convergence do not restart mechanical stabilization.

Only when mechanical evidence, build diagnostics, WinUI triage, resource resolution, and generated XAML provide no actionable cause may you bisect XAML. Disable approximately half of one root subtree per probe, change only that dimension, and stop after four probes. Restore the complete page before applying the root-cause fix.

Reuse the running app during development replay. Restart only when a state requires clean startup, prior actions cannot be reversed, or a correction requires a new process. Development observations are provisional.

After progressive coverage, close the development process and run the
following command with the same build-selection options:

```powershell
winapp-migrate uwp closure attest "<target>" --force-replay --discriminating-evidence "<new-target-relative-evidence>"
```

The evidence must be new, fresh discriminating content, not a touched or renamed
copy. Force replay bypasses neither no-progress guards nor evidence freshness.

Replay the primary sentinel first, then every runnable state in order. Only
evidence created after this recorded replay start may set target states to
`passed` and resolve runtime findings.

After a state-plan update completes the active slice, immediately run
`winapp-migrate uwp closure assess "<target>"` before working on the next
slice. This persists the completion event and activates the next slice;
do not batch an unobserved phase transition with the next slice's edits.

The replay attestation persists the stable run ID, launch command, returned
primary PID, target fingerprint, and launch time. Discover the actual HWND
through `winapp-migrate uwp closure capture`. During closure, create one fresh
state-intent file after the replay starts containing the target fingerprint,
replay attestation ID, state ID, and expected outcome. Reuse that file across
the state's distinct UI commands; an immediate repeat of the same command is
rejected. Replace the intent when the target, replay, or state changes. Include
the HWND with the run ID in the state-level decisive action, final
tree/property, screenshot when needed, and health evidence. Intermediate probes
remain development provenance, not completion evidence. Do not infer an HWND
from launch JSON. Evidence from a replacement process cannot complete the
earlier run.

## 4. Compare semantically

For every source/target pair, verify:

- the action succeeded and reached the intended state;
- state-changing actions produced the expected changed value, selection, collection, navigation state, or visible result;
- expected navigation, text, controls, data, selection, status, and user-visible outcomes are present;
- content order and relative layout remain usable;
- no content is missing, blank, unintentionally hidden, clipped, or replaced by template UI;
- intentional differences are supported by the migration design or platform behavior.

Use screenshots as semantic visual evidence, not a raw pixel threshold. UWP and WinUI 3 can differ in theme, window size, default styles, spacing, rasterization, and item density while preserving behavior.

## 5. Apply the completion gate

Classify each planned state:

- `verified`: required source evidence exists, target replay succeeded, and semantic comparison passed;
- `blocked`: an identified external prerequisite prevented capture or replay;
- `unverified`: no usable source evidence exists or comparison could not be completed;
- `failed`: replay or comparison exposed a regression.

A target crash, app-owned exception, visual-tree race, or broken navigation transition is `failed`, not `blocked`. If a later state cannot run because an earlier app-owned state failed, keep the earlier state `failed` and mark the later state `unverified` with that failed prerequisite named.

When finalizing schema `1.5` `migration-report.json`, keep the schema `1.0` state-plan path and evidence roots stable. Summarize:

- `sourceBaseline.status`: `captured` when every source state has evidence, `partial` when some do, or `unverified` when none do;
- `targetReplay.status`: `passed` when every target state succeeds, `partial` when at least one is blocked or unverified, or `failed` when any state fails;
- `parityStatus`: `verified` when every comparison is verified, `partial` when verified states coexist with blocked or unverified states, `unverified` when no paired comparison completes, or `failed` when any comparison fails.

Provide a concise reason whenever a status is not the successful first value. Before writing summaries, reconcile the state plan, findings, and report as one closure transaction. Evidence for an attempted state whose plan remains `not-run`, or evidence satisfying a finding that remains open, invalidates completion.
