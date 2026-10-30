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

Update source observations and truthful unsuccessful target/comparison outcomes
after attempts. Do not write successful target/comparison statuses by hand:
`closure record-state` derives them from the contract below. Evidence entries
are paths relative to `<target>`. Record implementation defects and runtime
signatures in the separate [semantic finding ledger](semantic-migration-protocol.md).

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
the run becomes terminal. Follow snapshot `disposition`: a still-valid
completion and other terminal outcomes reject operations. Completed closure
is re-assessed against the full current-target build/replay chain; stale or
missing canonical evidence reopens it instead of retaining old success.

Set `source.status` to `captured`, `blocked`, or `unverified`; `target.status` to `passed`, `blocked`, `unverified`, or `failed`; and `comparison.status` to `verified`, `blocked`, `unverified`, or `failed`. Keep `not-run` only until the phase is attempted, and provide `reason` for every status other than `captured`, `passed`, or `verified`.
The product validates these status vocabularies: target `captured`, for
example, is invalid, not a synonym for `not-run`.

### Machine-checkable state contracts

Schema `1.0` additionally supports `assertions` on each state. Define the full
observable acceptance contract **before** its target captures. For example:

```json
"assertions": [
  {
    "id": "selected-item",
    "kind": "ui-property-equals-source",
    "probeIndex": 1,
    "sourceEvidence": ".migration-evidence/source/selected-item-ui.json",
    "automationId": "SelectedItem",
    "selector": "SelectedItem",
    "property": "isSelected"
  }
]
```

`probeIndex` is zero-based in the ordered `--probe` sequence supplied to
`record-state`, including action and screenshot probes. The mapped probe must
be an `inspect` or `get-property` command requesting `--json`.
`sourceEvidence` must already be in this state's captured source evidence.
An inspect observation must identify exactly one element by `automationId`.
An optional `selector` defaults to `automationId`; when present in an inspect
command it must match the contract (and an explicit contract selector must
also match the returned element's `selector`).

Use the actual pinned WinApp `UiElement`/`UiPropertyResult` formats:

| Assertion `property` | `inspect` field/type | Required `get-property -p` |
|---|---|---|
| `name`, `value` | string (including an observed empty value) | `Name`, `Value` |
| `isEnabled`, `isOffscreen` | JSON boolean | `IsEnabled`, `IsOffscreen` |
| `isSelected`, `hasKeyboardFocus` | **not exposed** | `IsSelected`, `HasKeyboardFocus` |
| `expandState` | `"expanded"` or `"collapsed"` | `ExpandCollapseState` |
| `toggleState` | `"on"`, `"off"`, `"indeterminate"` | `ToggleState` |

For selection, capture both sides with their exact HWND and planned selector:

```powershell
winapp-migrate uwp source capture "<target>" -- get-property "SelectedItem" -w <source-hwnd> -p IsSelected --json
winapp-migrate uwp closure capture "<target>" --state "<state-id>" -- get-property "SelectedItem" -w <target-hwnd> -p IsSelected --json
```

The current result includes a typed element detail, for example:

```json
{
  "elementId": "tab-selected-item-a1b2",
  "element": {
    "type": "TabItem",
    "name": "Selected item",
    "automationId": "SelectedItem",
    "selector": "tab-selected-item-a1b2",
    "isEnabled": true,
    "isOffscreen": false,
    "isInvokable": true,
    "x": 10, "y": 20, "width": 120, "height": 32
  },
  "properties": { "IsSelected": "True" }
}
```

This is
not an inspect element containing `isSelected: true`. Query captures require
exactly one positional selector and one explicit `-p/--property`, matching
the contract and the returned property key. Returned `elementId` is a
nonempty generated runtime selector, not an echo of the requested AutomationId.
It may differ between source and target; target identity binding comes from
the sealed command selector/property and HWND/process/replay receipt, not
string equality with that runtime ID. All-properties queries,
extra verdict fields, missing/null properties, and JSON booleans/numbers in
the string-valued `properties` object cannot verify a state.
The `element` object's known fields and typed child/ancestor collections are
validated; unknown verdict members, null/array details, and malformed
collections fail closed. Legacy two-member results remain readable, as do
compact string details that exactly identify the returned runtime selector.

Normalize only genuine native forms: `"True"`/`"False"` to booleans;
`Expanded`/`Collapsed` to inspect's lowercase expansion states;
`PartiallyExpanded`/`LeafNode` are additional get-property-only expansion
states. Toggle enums normalize similarly. Source and target must then match
exactly. Invented `isExpanded`/`text` inspect fields and absent selection/focus
fields are not supported. Missing, ambiguous, or unsupported observations
remain unverified. Verified property-only observations can materialize as
canonical `-ui.json` evidence; they do not have to masquerade as UI trees.

Assertions have unique IDs. There is no `passed`, `verified`, expected-boolean,
script, arbitrary JSON predicate, or free-text assertion language. Product
seals bind receipts to the state ID and its entire planned/source contract,
including source evidence hashes. Changing that contract requires recapture.
Unsigned legacy probes remain development evidence; they cannot be relabeled
as state receipts. The local trust anchor is the named, persistent
non-exportable CNG key `WinAppMigration.StateClosure.v1`. Its public key is
loaded independently of artifacts; carried keys must match it for closure.
Signed operation IDs and artifact digests bind build/replay attestations,
raw outputs, probe receipts, and state attestations into the local chain.
An arbitrary replacement key cannot authorize fabricated evidence.
Portable embedded-key signature integrity is distinct from locally trusted
verification and cannot declare closure. These seals are same-user
tamper-evidence, not an OS boundary against arbitrary malicious same-user code.
Full workflow,
fingerprint/replay, raw-output, state, window-binding, and assertion
re-derivation remains mandatory even with a valid signature.

This is a deliberately bounded contract, not a general semantic/visual oracle.
Do not reduce a richer expected outcome to one convenient property. If a
required clause needs unsupported layout, collection, timing, lifecycle,
different source/target selectors, or other semantics, keep it unverified and
its finding open. Human prose alone cannot safely authorize closure.

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

1. Launch the exact report-recorded source project through the source gateway:

   ```powershell
   winapp-migrate uwp source run "<target>" --arch <x64|arm64|x86>
   ```

   The gateway resolves the source root and project only from
   `migration-report.json`, records the pre/post launch window inventories,
   and returns the newly created HWNDs authorized for source capture. Do not
   pass a source path or invoke raw WinApp CLI.
2. If project mode reports a build failure, use the persisted structured diagnostics. Never edit the source project for baseline capture.
3. Treat a compiled and registered source that fails activation as a runtime failure. Make one gateway-mediated diagnostic launch:

   ```powershell
   winapp-migrate uwp source diagnose "<target>" --arch <x64|arm64|x86>
   ```

4. If a Debug-only startup failure is plausible, make at most one Release attempt. After bounded recovery, record affected states as `unverified`.
5. Use only an HWND in the run receipt's `authorizedWindows`. The gateway derives this capability from the before/after inventory because a UWP top-level window can belong to `ApplicationFrameHost`. Never stop `ApplicationFrameHost`.

Capture states with the resolved HWND:

```powershell
winapp-migrate uwp source capture "<target>" -- inspect -w <hwnd> -d 8 --json
winapp-migrate uwp source capture "<target>" -- screenshot -w <hwnd> --focus --json -o "<source-evidence-path>.png"
winapp-migrate uwp source capture "<target>" -- invoke "<semantic-name>" -w <hwnd> --json
```

The gateway persists command JSON, raw stdout/stderr, and a per-probe receipt
under the report-declared source evidence root. Screenshot output must also
remain under that root. After each action, inspect again and confirm the
expected state before taking its screenshot. Prefer visible semantic names
because they can survive framework-specific AutomationId changes.

Use `blocked` only for a known external prerequisite such as unavailable permissions, data, hardware, or credentials. Use `unverified` after bounded capture or comparison finishes without usable evidence and no specific outstanding prerequisite remains.

After source capture, close the exact source HWND through `uwp source capture`
and confirm it disappears. If it has no UIA Close element, stop only the exact
source PID returned by `uwp source run`. Never clean up by broad process name.

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

Never invoke raw `winapp run`, `winapp build`, or `winapp ui` for either side.
Those calls bypass source identity/window authorization or target
fingerprinting/replay identity and evidence provenance, so their output cannot
close a state. The installed Copilot plugin guard denies these calls in an active
migration session; do not disable or bypass it.

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
winapp-migrate uwp closure capture "<target>" --state "<state-id>" -- inspect -w <hwnd> -d 8 --json
winapp-migrate uwp closure capture "<target>" --state "<state-id>" -- invoke "<semantic-name>" -w <hwnd> --json
winapp-migrate uwp closure capture "<target>" --state "<state-id>" -- screenshot -w <hwnd> --focus --json -o "<evidence-path>.png"
```

The target UI command refuses to run without a fresh canonical replay. Its
state-bound commands require exactly one explicit `-w/--window`, whose
Windows owner PID must match the canonical replay's `ProcessId` before and
after capture, and its OS process start time must match replay
`processStartedAt`. The receipt seals the HWND, PID, and
`windowProcessStartedAt`; `record-state` rechecks their agreement with both
the command and replay. A numerically reused PID is a different process and
cannot reuse the old replay capability. Failed/unavailable identity resolution
fails closed; legacy PID-only receipts require re-attestation/recapture.
Identity lookup is resolver-injected for deterministic tests, while production
uses the native window owner and OS process creation time. It never accepts a
source HWND, another process's window, or implicit/focused-window selection
as target proof. Discovery via unbound `list-windows` stays possible but
cannot itself satisfy a state assertion. Record-state and every closure
reassessment/final verification resolve the HWND owner and process creation
time again through the resolver. Missing windows, exited/reused processes,
and inaccessible identity fail closed. Keep the original bound window/process
alive through final verification; portable historical signature checks cannot
replace a live identity check. Its
default output is a bounded receipt; raw stdout/stderr and a per-probe
`probe.json` remain under `.migration-evidence/enforcement/probes`. Read only
the relevant persisted payload instead of repeatedly emitting the full UI tree;
reserve `--raw-output` for a focused result that must be inline. During semantic
convergence, rely on this automatic provenance and update state evidence once
per completed state rather than authoring evidence for each UI command. When
the target exits only while an `invoke` action runs, or a native crash stack is
dominated by `UIAutomationCore` without an app-owned frame, test the same
control once with gateway-mediated `click` after a clean launch.

Option syntax follows the pinned WinApp CLI's System.CommandLine `2.0.5`
conventions: HWND selectors support `--window value`, `-w value`,
`--window=value`, `-w=value`, `--window:value`, and `-w:value`. Screenshot
outputs support `-o path`, `--output path`, `--output=path`, `-o:path`,
`-o=path`, and `--output:path`. Empty or repeated/ambiguous selectors fail
closed rather than silently selecting a different output or window.

The installed `0.6.3-isolated-rebased.62` screenshot help and
`UiScreenshotCommand.Handler.DefaultOutputFileName`/`PublishAsync` establish
the default: `screenshot.png` in the WinApp process working directory, including
composite captures. With no explicit output, a state-bound capture runs in
its own enforcement probe directory and seals that relative
`workingDirectory`; the native filename/default behavior is unchanged.
Explicit paths still resolve from the target directory; prefer
`.migration-evidence` paths so output does not change application assets.
An unbound screenshot retains the target working directory.
Every successful state-bound screenshot must yield a fresh valid PNG, retained
at `probes/<id>/screenshot.png` with its sealed hash. Missing, stale, malformed,
or dropped screenshot evidence prevents recording closure, even when separate
nonvisual assertions pass.

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
state-bound successful product probes from that replay may be submitted to
`record-state`; old sequences cannot set target states to `passed`.

After each completed sequence, record its canonical receipt paths in order:

```powershell
winapp-migrate uwp closure record-state "<target>" --state "<state-id>" `
  --probe ".migration-evidence/enforcement/probes/<action-id>/probe.json" `
  --probe ".migration-evidence/enforcement/probes/<inspect-id>/probe.json"
```

Include every decisive action and observation, not just the final tree.
Every supplied receipt must carry an intact integrity seal and be successful, state-bound,
tracked by a workflow operation, and fresh for the current target/replay.
The product checks existing raw outputs and their hashes, derives every
assertion, and persists the closure attestation and derived state statuses.
It also resolves findings whose complete mapped machine contracts are met.
Consume its returned assessment before working on the next slice.
`finalize` subsequently materializes canonical evidence and synchronizes
mapped TODOs and report summaries through the existing transaction.
The sealed state attestation includes `sourceEvidenceHashes` and
`targetEvidenceHashes`, computed from original evidence by the product.
Verification re-derives them rather than trusting supplied hash strings.
Assessment re-selects original screenshot/JSON evidence and checks each
canonical copy against those derived hashes. Missing, replaced, or unbound
canonical artifacts reopen completed closure even when replacement files have
valid PNG/UI shapes. Finalize repairs the copies and removes optional
screenshots that the current state evidence no longer includes.

The replay attestation persists the stable run ID, launch command, returned
primary PID, target fingerprint, and launch time. Discover the actual HWND
through `winapp-migrate uwp closure capture`. During closure, create one fresh
state-intent file after the replay starts containing the target fingerprint,
replay attestation ID, state ID, and expected outcome. Reuse that file across
the state's distinct UI commands; an immediate repeat of the same command is
rejected. Replace the intent when the target, replay, or state changes. Include
the HWND with the run ID in the state-level decisive action, final
tree/property, screenshot when needed, and health evidence. Intermediate probes
remain development provenance until an accepted `record-state` sequence. Do not infer an HWND
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

The PNG validator analyzes normal 8-bit noninterlaced pixels and classifies
tiny, uniform, nearly blank, or transparent imagery as low-information.
Legal but unanalyzed encodings, including 16-bit and Adam7-interlaced PNGs,
are `ValidUnclassified` after bounded header/chunk/order/CRC, zlib checksum,
filter, and scan-line-layout validation. No new image-decoder dependency is
required. They are **not informative visual proof**. A sealed, verified
nonvisual contract may retain low-information or valid-unclassified images as
supplemental evidence, or omit screenshots; structured trees/property
observations still materialize. Truly corrupt images remain invalid even
when nonvisual assertions pass. Identical images/observations are
not blanket-rejected: cancellation/back restoration can intentionally return
to the same state. None of these classifications automatically prove
visual-only parity.

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

Provide a concise reason whenever a status is not the successful first value. Use `closure finalize` to reconcile the state plan, findings, and report.
It never manufactures a state attestation from successful status text.
Per-state report entries preserve and must exactly match the plan's observed
status and reason, not just its IDs and aggregate statuses. Legacy ID-only
report entries still load, but must be synchronized into complete entries
before closure can pass. Contradictory failed/blocked entries or stale reasons
invalidate completion.
Prose-only finding completion conditions remain open even if nearby states
pass; map machine-verifiable conditions explicitly with `completionAssertions`.
