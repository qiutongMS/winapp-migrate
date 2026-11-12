---
name: uwp-to-winapp
description: "Use immediately when migrating, porting, or converting a C# UWP app to WinUI 3 or the Windows App SDK, including projects using Windows.UI.Xaml or UWP Package.appxmanifest files. C++/WinRT and Visual Basic UWP migrations are out of scope."
---

# UWP to WinApp migration

Preserve the app; do not redesign it. Keep every page, control, resource, helper, navigation path, and user-visible behavior unless authoritative platform evidence proves there is no WinUI 3 desktop equivalent. Report unsupported behavior explicitly; never omit it silently.

This skill is the UWP pipeline in the preview `winapp-migrate` product. It uses `winapp-migrate uwp` for deterministic mechanical conversion and consumes its UWP migration report with schema `1.7`. WinApp CLI remains a separate implementation tool, but every source baseline operation must cross the `winapp-migrate uwp source` boundary and every target build, launch, diagnosis, and UI action must cross the `winapp-migrate uwp closure` boundary.

Three controls remain independent. The plugin's machine-readable Skill
execution contract enforces gateway boundaries. Migration
closure determines whether the resulting app is mechanically and behaviorally
complete. TODO touch sets partition implementation work; build and runtime
stabilization remain project-level.

## Choose the acceptance scope

Follow an explicit user choice. If acceptance scope has not been specified,
ask whether the user wants bounded **four-checks** acceptance or the existing
strict behavioral closure. Do not silently downgrade existing strict workflows;
the CLI default and strict closure contracts are unchanged.

For four-checks, read [bounded four-check acceptance](references/four-check-acceptance.md)
before authoring runtime requirements. Use the explicit
`uwp acceptance ... --profile four-checks` commands. They assess startup,
complete declared/discovered surface accounting, major-state reachability and
semantic UIA structure independently of strict closure. A completed list of
hand-picked states is not an exhaustive surface inventory.

In this mode, do not add pixel/visual parity, internal lazy-object lifetime,
performance, draft retention or full functional parity as completion gates.
Fix only defects that prevent one of the four agreed checks. Record other
differences as excluded follow-up work; do not repeatedly rebuild to refine
fonts, colors or initial window geometry unless they obstruct those checks.
Do not treat every template interaction state as a major application mode.

The source and closure gateways, mechanical safety, provenance and frozen
gateway contracts still apply. Build the smallest observable nonvisual gateway
contract consistent with the chosen scope. Do not invent a selector from
`x:Name`, weaken an existing sealed contract, manually verify TODOs, or bypass a
gateway to obtain a four-checks pass. If gateway prerequisites cannot be
satisfied within scope, stop with the concrete capability gap rather than
expanding into tool development or full behavioral verification.

The strict completion/falsification instructions below apply when strict
closure is selected. In four-checks mode, stop at the separately reported
four-check result or an explicit blocked/unknown handoff. A four-checks pass
never means `parityStatus: verified` or strict migration closure.

## Tool and contract compatibility

Treat three capabilities as independent:

| Capability | Required contract | Current compatibility truth |
|---|---|---|
| UWP migration and closure | `winapp-migrate uwp` with `migrate`, `verify`, `decide-project-item`, `todo`, and `closure`; migration report schema `1.7`, TODO progress schema `2.4`, closure evidence schema `1.0`, workflow progress schema `3.0` | The preview `winapp-migrate` build from this repository owns these capabilities and invokes WinApp CLI at its target-operation boundary. |
| Build, register, launch, diagnose, and automate | Gateway-mediated WinApp CLI `run` and `ui` capabilities | WinApp CLI 0.7+ can supply these capabilities. The source and closure gateways resolve it from `--winapp`, then `WINAPP_MIGRATE_WINAPP_PATH`, then PATH; it is valid for the raw executable to be absent from the agent's PATH. |
| Static WinUI diagnostics | A project `PackageReference` to `Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer` with `PrivateAssets="all"` | The analyzer is a separate preview/`0.x` NuGet package. WinApp CLI does not inject it, and this plugin does not bundle its DLL. |

Run these checks before changing the source or target:

```powershell
winapp-migrate --version
winapp-migrate --help
winapp-migrate uwp --help
winapp-migrate uwp migrate --help
winapp-migrate uwp verify --help
winapp-migrate uwp decide-project-item --help
winapp-migrate uwp todo --help
winapp-migrate uwp source --help
winapp-migrate uwp closure --help
```

Verify from the actual output that:

1. `winapp-migrate uwp` exposes `migrate`, `verify`, `decide-project-item`, `todo`, `source`, and `closure`;
2. `winapp-migrate uwp source --help` exposes `run`, `capture`, and `diagnose`;
3. `winapp-migrate uwp closure --help` exposes `seal-contract`, `rebind-selector`, `assess`, `attest`, `capture`, `record-state`, `finalize`, `diagnose`, `record-failure`, and `record-diagnosis`;
4. the configured WinApp CLI satisfies the gateway contract. If the raw executable is intentionally hidden from PATH, do not bypass that boundary merely to inspect its help.

Accept the `winapp-migrate uwp` pipeline only when its local help or versioned documentation explicitly promises schema `1.7`. Do not infer compatibility from a preview version number, a branch name, or the presence of this skill.

If `<target>\migration-report.json` does not exist, create the deterministic target before semantic edits:

```powershell
winapp-migrate uwp migrate "<source>" --output "<target>"
```

If the report already exists, parse it, require `schemaVersion` exactly `1.7`, and refresh mechanically owned evidence:

```powershell
winapp-migrate uwp verify "<target>"
```

The behavioral state plan is a separate artifact with schema `2.0` and
immutable ordered verification requirements. Read
[required contracts and ROI verification](references/state-verification-contracts.md).
Legacy `1.0` plans still load provisionally, but are never silently trusted for
parity. Upgrade explicitly only before source/workflow activity and after
authoring complete structured requirements; captured mutable legacy plans need
a fresh planned/captured workflow.

### Start or resume closure

For a target with an existing report, inspect the state plan immediately.
Before a new workflow, establish the complete source/user-required action,
assertion, restoration and visual ROI contract:

```powershell
winapp-migrate uwp closure seal-contract "<target>"
winapp-migrate uwp closure assess "<target>"
```

Treat `UWMIG016` as a pre-seal planning requirement. If the source uses
`FileOpenPicker`, `FileSavePicker`, or `FolderPicker`, declare ordered source
and target actions for opening the system picker, selecting and cancelling,
and returning to the app-owned state. The picker is a trusted delegated
Windows window, not permission to accept arbitrary external-process UI.

For an already sealed workflow, resume with `assess`, never re-seal changed
requirements. `--extend` is append-only before source/workflow start; it cannot
remove actions/assertions or rewrite order. Run the initial assessment after
contract establishment and before source UI capture, semantic target edits,
or delegating implementation work. It
resumes the deterministic migration checklist and returns the current
stage, frontier, disposition, and recommended next operations. Elapsed time and operation
totals are telemetry only. If the state plan is missing, create only that plan
first, seal its requirements, then assess before doing any capture or implementation.

Treat the reported frontier as guidance, not execution permission. You may
work on any declared state whose dependencies are understood. Reassess only at a workflow
boundary: after a target-fingerprint change, source/state completion, semantic
finding resolution, slice completion, or immediately before finalization. Do
not reassess after each build substep or UI command. The default bounded receipt
points to the durable full state; use `--full` only for focused workflow repair.
Target execution must still use the gateway `closure attest`, `closure diagnose`,
and `closure capture` operations so process identity and evidence provenance
remain verifiable.

If a frozen action's selector becomes ambiguous while its verb, payload,
ordering, assertions, and intended behavior remain valid, use the audited
`closure rebind-selector` recovery described in the required-contract
reference. Never edit the frozen plan command directly. Rebinding invalidates
that state's earlier target probes and requires recapture.

Legacy state-plan `todoIds` are ignored compatibility metadata. They do not
redefine the sealed action/assertion contract, TODO ownership, or the state
contract fingerprint; new plans omit them. Ownership is derived from exact
TODO `completionChecks`, with `affectedStates` retained only for legacy report
compatibility. A changed completion state requires fresh target evidence for
that state.

If neither a valid report nor a compatible `winapp-migrate uwp` pipeline is available, stop before scaffolding or copying application files. Report that deterministic migration is unavailable. Do not replace the missing tool with hand-written namespace replacement, a second scaffold, or a fabricated report.

## Ownership

- `winapp-migrate uwp` owns source-file classification, deterministic transforms, project-item accounting, activation declaration inventory, mechanical verification, canonical closure attestations, evidence freshness, closure gates, and schema `1.7` report fields.
- This skill owns semantic decisions, dependency-contract preservation, target implementation, and behavioral validation.
- `winapp-migrate` owns classic UWP source detection, Visual Studio MSBuild/SDK selection, framework-dependency preparation, and source evidence provenance. WinApp CLI owns general target build plus loose-layout registration, launch, crash diagnostics, and UI automation. The source gateway owns source launch/window capability; the closure gateway owns every target invocation and its provenance.
- The analyzer reports static WinUI risks during ordinary builds. It does not create the migration report and does not replace runtime replay.

Do not duplicate `winapp-migrate uwp` checks with ad hoc extension lists or repository-wide searches when current schema `1.7` evidence already covers them. An empty migration TODO list still does not guarantee a buildable, runnable, or behaviorally equivalent app.

## 1. Validate the migration input and classify risk

Before semantic edits:

1. Identify the exact entry UWP `.csproj`; never substitute a repository or solution root merely because it contains the project recursively.
2. Confirm `<target>\migration-report.json` exists and has schema `1.7`.
3. Confirm `source.projectFile` identifies that exact entry project.
4. Require the overall mechanical result, project graph, project-item accounting, and mechanically eligible activation declarations to be in the report-documented ready state.
5. If mechanically owned evidence is stale or failed, run `winapp-migrate uwp verify "<target>"`. For a decision-required project item, use the stable item ID and an allowed strategy from `winapp-migrate uwp decide-project-item --help`, then run `verify` again. Never edit mechanically owned fields by hand.
6. For every required TODO with `guidance`, treat its `facts` and `mechanicallyAppliedTransforms` as tool-owned evidence. A candidate approach is applicable only when its listed preconditions are proven from the source and target; record it with `uwp todo select-approach`, then obey its risks, `forbiddenShortcuts`, and `completionConditions`. Map the TODO to every state needed to prove those conditions. If implementation is delegated, include the relevant facts, selected and rejected approaches, risks, forbidden shortcuts, and completion conditions. Do not inject guidance as target-code comments or treat a candidate as an automatic repair.
7. Read startup, the feature registry or navigation shell, `dependencyAnalysis`, `activationAnalysis`, and enough source to classify the migration as fast or expanded.
8. Identify the source behavior that proves the app's primary purpose and any user-critical flow.

Read
[Migration TODO execution contract](references/migration-todo-contract.md) and
[Parallel migration work orchestration](references/parallel-work-orchestration.md).
Partition required, non-terminal TODOs by touch paths, project items, conflict
keys, shared abstractions, and acceptance conditions. Implement disjoint groups
in parallel and overlapping groups under one owner. Reconcile all results in
the canonical target before project-level build/runtime stabilization. Parallel
execution is an optimization, never a completion requirement.

Use the **fast path** when the primary behavior is page-local, dependencies and APIs have direct target mappings, and it crosses no uncertain project, lifetime, activation, window, native-host, or shared asynchronous boundary. Read only startup, capability-bearing manifest entries, relevant pages, and their custom controls or resources. Do not create a semantic finding ledger before a real finding exists.

Use the **expanded path** when a critical behavior depends on a `review-required` dependency; spans projects; requires an adapter, source port, or equivalent implementation; crosses activation, background, multi-window, native-host, lifecycle, shared-state, or asynchronous ownership boundaries; or when the first build or runtime probe exposes an unknown shared failure. Once a migration expands, do not downgrade it merely because a local workaround compiles.

On the expanded path, follow [Semantic migration protocol](references/semantic-migration-protocol.md) and select at most three migration-critical seams. Each seam joins a source behavior to the target architecture through an uncertain dependency, lifetime, state, or platform boundary. Choose one sentinel flow that proves startup plus the highest-risk seam; add another only for a genuinely independent risk.

## 2. Capture the source behavior baseline

Follow [Behavioral validation](references/behavioral-validation.md) to create and persist the complete schema `2.0` state plan declared by the report. Establish its required contract before source capture.

Before editing, capture startup and the fast-path primary sentinel, or every expanded-path seam sentinel. Capture remaining source states before implementing or validating their corresponding target feature. Complete the bounded source-recovery and evidence-fallback process before declaring a state unavailable. The initial assessment required by the closure entry protocol must already be persisted before this capture starts.

If usable source evidence still cannot be obtained, record the affected states as `unverified`; never infer parity from source code or build success. Treat a newly observed source window as a successful launch even when the launch command's output transport remains pending. Complete exact-window cleanup after each source-capture session before editing the target.

Immediately after the required source states are captured and persisted, run
`winapp-migrate uwp closure assess "<target>"` again. If bounded recovery cannot
produce usable source evidence, persist the affected states as `unverified` and
reassess before target implementation. The source-oracle gate remains explicit
debt and prevents parity/strict closure, but it does not prohibit target edits,
mechanical verification, canonical build/replay, diagnosis, or partial target
development probes.

## 3. Implement the TODO backlog

Treat report categories as evidence within the active behavior scope, not as an exhaustive worklist or a completion definition.

Partition non-terminal TODOs into conflict-free groups. Parallelize groups with
non-overlapping touch sets and independent acceptance checks; keep startup,
shared project files, shared abstractions, and overlapping XAML/code-behind
under one owner. Workers implement and report; they do not require a global
build. The parent owns conflict handling, integration, and final closure.

For each active TODO, use the product-owned bounded execution receipt:

```powershell
winapp-migrate uwp todo status "<target>"
winapp-migrate uwp todo assess "<target>" "<todo-id>"
winapp-migrate uwp todo select-approach "<target>" "<todo-id>" "<approach-key>" --reason "<rationale>"
```

The project status combines the authoritative closure workflow stage, budget,
last observation, frontier, and disposition with TODO and state counts, the
incremental development frontier, latest TODO failure, current action, and
next checkpoint.
The assessment is the compact per-TODO resume packet. Do not repeatedly reread the
whole report, this Skill, CLI help, or unrelated source after it identifies the
TODO facts, current approach, rejected approaches, completion states, and next
command. Candidate approaches remain non-exhaustive unless the report says
otherwise.

If assessment returns `bind-state`, the mechanical pass could not know the
exact semantic proof. Bind only the smallest existing state and assertion(s)
that prove the TODO invariant; do not broaden it across every state:

```powershell
winapp-migrate uwp todo bind-state "<target>" "<todo-id>" --state "<state-id>" --assertion "<assertion-id>" --check-id "<stable-check-id>" --summary "<why-this-assertion-proves-the-invariant>"
```

Implement one coherent attempt and record it without requiring a build:

```powershell
winapp-migrate uwp todo record "<target>" "<todo-id>" --outcome implemented --summary "<change>" --evidence "<target-relative-path>"
```

After integrated build/replay, record each assigned exploratory state outcome.
Successful outcomes must reference product-generated state-bound development
probe artifacts; they advance only TODOs whose completion checks own that state
and reach `development-validated` only when every assigned state passes for the
current target fingerprint:

```powershell
winapp-migrate uwp todo record-development "<target>" --state "<state-id>" --summary "<attempt>" --evidence "<probe.json>"
```

The product derives the result. Exact assertion parity passes, an exact
behavior mismatch fails, and every unsupported, ambiguous, unavailable, or
safety-denied result becomes `human-validation-required`. Follow the emitted
human validation item; never rewrite an inconclusive result as passed.

An affected target change matching the TODO touch set, or an upstream state
dependency's owning touch set, makes that development result `stale` without
discarding the implementation receipt; unrelated files do not invalidate it.
A failed state records its stable
signature and leaves only its owning TODOs `blocked`, not every TODO mentioned
by an arbitrary state-plan mapping.

Do not begin canonical `closure record-state` while any required TODO is
`not-run`, `partial`, `blocked`, `stale`, or `unmapped`. The CLI enforces this
TODO readiness gate; the existing closure frontier independently governs
failures and semantic findings. After it opens, run one complete ordered
canonical pass on one unchanged target fingerprint and replay identity, then
finalize once.

If the approach fails, record the exact negative result:

```powershell
winapp-migrate uwp todo record "<target>" "<todo-id>" --outcome failed --summary "<failure>" --failure-signature "<signature>" --retry-condition "<changed-condition>" --evidence "<target-relative-path>"
```

The same approach cannot be selected again without `--retry-evidence`.
Unrelated target changes do not invalidate implementation progress. If work
reveals a missing app-owned migration obligation, add it with `todo discover`;
do not turn ordinary compiler/runtime symptoms into TODOs.

On the fast path, migrate the complete small behavior surface as one coherent patch when practical. On the expanded path, implement coherent conflict groups across the consumed dependency contract, owning projects, shared abstraction, and direct call path. Do not require each group to build independently; project-level stabilization follows after currently implementable work is integrated.

Common checks include:

- merge application resources without replacing the WinUI startup bootstrap;
- restore only dependencies and manifest declarations required by preserved features;
- replace dispatcher and windowing APIs through shared WinUI 3 abstractions;
- reconcile shared-file conflicts without losing either side's required behavior;
- wire the initial page without replacing generated bootstrap or title-bar behavior.

Use schema `1.7` `dependencyAnalysis` as the source dependency inventory, not as a package recommendation. Follow [Dependency contracts](references/dependency-contracts.md) for every `review-required` dependency and every package replacement, adapter, source port, or equivalent implementation. Resolve or explicitly account for every `inspectionIssue` when the analysis is incomplete.

Use `activationAnalysis` as the declaration inventory, not as evidence that activation behavior works. For every activation contract in scope, follow [Activation contracts](references/activation-contracts.md). A translated declaration does not prove routing, single-instance behavior, navigation, file loading, restoration, or error handling.

For unknown report categories, use their `summary`, `reason`, and `locations`; do not guess from an ID. Resolve mappings that block the current slice or govern a shared root cause. Preserve source XAML bindings, event handlers, default selection, initialization order, navigation reachability, AutomationIds, and observable outcomes.

Use [Platform semantic differences](references/platform-semantic-differences.md) as a non-exhaustive review accelerator. When an API mapping is uncertain, consult the official [UWP to Windows App SDK mapping table](https://learn.microsoft.com/windows/apps/windows-app-sdk/migrate-to-windows-app-sdk/api-mapping-table). A visible fallback is a documented limitation, not evidence of parity.

## 4. Stabilize the project

The target project must reference the analyzer package. Add it through the project's package-management convention:

```powershell
dotnet add "<target.csproj>" package Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer
```

Ensure the resulting project reference has `PrivateAssets="all"`. With central package management, keep the version in the repository's package version file and keep `PrivateAssets` on the project reference. Do not copy or reference an analyzer DLL from this plugin or a local checkout.

If NuGet cannot resolve the package, retain the restore evidence and report the
blocked analyzer prerequisite. You may continue bounded implementation
diagnosis manually, but the closure gateway must not attest or close the build.
Do not claim an analyzer-enabled build.

For the packaged target, let the closure gateway build/register and then
start the detached canonical replay:

```powershell
winapp-migrate uwp closure attest "<target>"
```

Pass `--configuration`, `--architecture`, `--runtime`, `--framework`, and
repeatable `--property Name=Value` arguments when defaults are not correct.
The gateway forwards the same values to WinApp CLI's `--no-launch --json` build
and `--no-build --detach --json` replay. It writes raw output, grouped
diagnostics, target fingerprints, and versioned attestations under
`<target>\.migration-evidence\enforcement`; do not duplicate that bookkeeping.
Never invoke raw `winapp run` for the target. A raw target invocation has no
canonical gateway attestation and its output cannot satisfy a closure gate.

On failure, read the current enforcement state and grouped build diagnostics,
then fix every governed occurrence of the active root cause in one pass. Do
not build after every file or diagnostic.

Build and runtime failures are project-level closure failures, not TODO
dependencies. Record them through `closure record-failure`, complete the
source-first diagnosis through `closure record-diagnosis`, and retain
behavioral root causes in the semantic finding ledger. Do not copy them into
a parallel TODO issue ledger.

Associate the evidence with an existing TODO only when it proves that TODO's
approach is wrong. Add a discovered TODO only when it proves omitted app-owned
migration work.

An unchanged failed target cannot be built again. Change the governed target
fingerprint before requesting another build. Repeated replay requires fresh
discriminating evidence, and repeated diagnosis or hypothesis for the same
signature and fingerprint is rejected. Elapsed time and aggregate operation
counts never authorize, cancel, defer, or terminate otherwise valid work.
Progressive corrections may use as many informative gateway operations as
needed.

Use the closure stage and frontier to choose useful next evidence, but do not
treat them as a scheduler authorization boundary. Repeated replay requires new
`--discriminating-evidence`; closure UI uses one fresh state intent for the
whole state sequence, not one justification per command. Workflow schema `2.0`
is imported to `3.0` while preserving operation/evidence history and clearing
retired budget state.
Stop speculative edits and inspect the complete
type, project-item, generated-code, and call-site context. Record expanded-path
root causes in the semantic finding ledger. Build success closes only
compile-time completion conditions.

Do not spend turns clearing advisory diagnostics unrelated to migration success.

## 5. Prove the architecture, then expand coverage

After the gateway returns a successful detached-launch attestation, replay the
active sentinel immediately. Control existence and process survival are not
sufficient.

If the sentinel fails, prioritize it as the active migration problem. For a
foreground target diagnostic, use only:

```powershell
winapp-migrate uwp closure diagnose "<target>"
```

The gateway preserves the WinApp CLI development probe under
`<target>\.migration-evidence\enforcement\diagnostics`. A diagnostic probe
never satisfies canonical build, replay, parity, or finalization gates. If the failed canonical
replay already recorded the runtime attempt, do not record it a second time.
For a failure first observed during a later UI action, persist its evidence and
record one stable signature:

```powershell
winapp-migrate uwp closure record-failure "<target>" runtime "<signature>" --state "<state-id>" --evidence "<target-relative-path>"
```

Before any target rebuild or replay, compare the failing source and target
paths. Start at the source handler, constructor, navigation callback, or
transition corresponding to the failed action; read its comments, guards,
exception workarounds, ordering, and lifecycle assumptions before interpreting
native stack noise. Persist the source/target diff and record one falsifiable
hypothesis and one discriminating test:

```powershell
winapp-migrate uwp closure record-diagnosis "<target>" "<signature>" `
  --state "<state-id>" `
  --source-location "<path:line>" `
  --target-location "<path:line>" `
  --source-invariant "<behavior or workaround to preserve>" `
  --hypothesis "<single falsifiable cause>" `
  --test "<one change or observation that distinguishes the cause>" `
  --evidence "<fresh-target-relative-diagnosis-path>"
```

The gateway exposes `diagnosis-required` until this source-first diagnosis is
complete and fresh, then `change-required` until the target fingerprint changes.
Do not overwrite a completed diagnosis or record another for the same signature
and fingerprint. Then pause broader coverage, create or update the finding,
apply only the change required by the discriminating test, and correct the
shared root cause. For startup or action-triggered process exits, follow
[Behavioral validation](references/behavioral-validation.md).
An unavailable pointer-input comparison is missing comparison evidence, not
an external blocker for an app-owned crash.

After every sentinel passes, expand progressively to remaining feature paths
and source states. Retain `slice`, `dependencies` (slice IDs),
and `criticality` fields to group states by dependency or architecture boundary.
During semantic convergence, let the gateway record automatic provenance for
each target UI probe. Do not create hand-written justification JSON for every
`list-windows`, `inspect`, `invoke`, or `screenshot`. Declare the complete
machine-checkable state `assertions` first, use `closure capture --state <id>`,
and submit the ordered receipts to `closure record-state` when the sequence
is complete. Never manually set target `passed` or comparison `verified`.
State-bound inspect, screenshot, and actions require an explicit `-w/--window`
owned by the canonical replay's PID and process start time; PID reuse does
not preserve replay authority. Use unbound `list-windows` for discovery.
Keep bound windows/processes alive through record-state and final verification:
the product re-resolves ownership instead of trusting stored PID/start fields.
Only the independently loaded named local CNG key anchors closure provenance;
an embedded-key portable signature check is not locally trusted closure.
Use state-bound `get-property <selector> -p <native-property> --json` for
selection/focus: pinned `inspect` does not expose those properties.
Keep the selector/property arguments consistent with the planned assertion;
use real `expandState`/`ExpandCollapseState` enums, never synthetic `isExpanded`.
`closure capture` writes a bounded receipt by
default and persists raw stdout/stderr plus
`probes/<probe-id>/probe.json`. Read only the relevant persisted output; use
`--raw-output` only when that focused payload is itself needed.
The snapshot recommends the first dependency-ready semantically unresolved
slice through `activeSlice`, `activeStateIds`, and `frontier`. It does not reject
capture or recording for another declared state. Whenever state evidence
completes a slice, consume the assessment returned by `record-state` and use it
to choose the next useful action.
A target change makes resolved evidence stale without returning those slices
to semantic implementation work. Closure selects stale resolved evidence with
a `revalidate` frontier; failed comparisons, runtime regressions, and open
findings can reopen semantic work before terminal completion.
Inspect snapshot `disposition`, not just the legacy `status`. A completed
workflow whose current target build/replay chain or closure evidence becomes
stale automatically reopens; it cannot retain success through an old replay.

## 6. Falsify and finalize

After all target changes, run the following command with the same
build-selection options:

```powershell
winapp-migrate uwp closure attest "<target>" --force-replay --discriminating-evidence "<new-target-relative-evidence>"
```

Fresh discriminating evidence must justify a repeated replay. Touching,
renaming, or reusing the same evidence content is not progress.
`--force-replay` cannot bypass diagnosis, change-required, TODO development
readiness, or evidence freshness.
Finish this falsification pass before report synchronization makes a fully
passed closure terminal; do not request more target operations afterward.

From that fresh canonical process, replay the primary sentinel followed by
every runnable planned state. For each state, create one target-relative intent
after the recorded replay starts. Its content must name the current target
fingerprint, replay attestation ID, state ID, and expected outcome. Pass that
same file to each distinct action in the state sequence:

```powershell
winapp-migrate uwp closure capture "<target>" --state "<state-id>" --discriminating-evidence "<state-intent>" -- <ui-command-and-arguments> -w <target-hwnd>
winapp-migrate uwp closure record-state "<target>" --state "<state-id>" --probe "<action-probe.json>" --probe "<inspect-probe.json>"
```

The gateway binds the intent to the target fingerprint and canonical replay,
allows it across distinct command signatures, and rejects an immediate exact
repeat. Replace it when the target, replay, or state changes. Persist only the
state-level completion bundle: the decisive action result, final
tree/property evidence, and a screenshot when visible parity matters. Do not
promote every intermediate inspect or capture to completion evidence. Never
invoke raw `winapp ui` or infer an HWND from launch output.

Treat every independently observable clause in `expectedOutcome` as an
assertion, not as descriptive prose. Record direct final evidence for each
clause before asking `record-state` to derive closure. Navigation to a destination
does not prove that its navigation item is selected; visible text does not prove
`IsSelected`, toggle state, value retention, focus, enabled state, or restored
state. Query the relevant property after the decisive action and retain that
result with the final tree. If any expected property is false or missing, keep
the state failed or unverified and reopen the linked finding.
Only the supported typed source/target assertion contract can derive success;
do not replace unsupported clauses with agent-authored verdict booleans or
prose. Unsupported visual, timing, lifecycle, or other claims remain unverified.
Low-information screenshots cannot establish visual parity, but do not reject
intentional restoration similarity or valid structured nonvisual assertions.
CRC/chunk-valid 16-bit/interlaced PNGs may be valid-unclassified supplemental
evidence for those nonvisual assertions; they cannot prove visual information.
Findings resolve only through satisfied mapped `completionAssertions`;
free-text-only completion conditions stay open.

`evidence-valid`, `contract-satisfied`, and `parity-verified` are different
product-rederived outcomes. Only parity-verified can clear mapped TODOs or
findings or complete final closure. Visual claims require semantic ROI
sufficiency and paired comparison, not page/tree presence or informative
chrome. Restoration requires the return action and post-return comparison.
This final pass attempts to disprove completion. Do not reuse a development process or old evidence. Reopen a finding when the clean sequence changes the signature or fails.

Before claiming completion:

- every source feature contract is represented by target implementation;
- every semantic finding is resolved, genuinely externally blocked, or truthfully unverified after its bounded evidence path;
- no finding retains an actionable app-owned location or next action;
- the current build is analyzer-enabled and successful;
- the primary sentinel and every runnable target state pass in one fresh canonical sequence;
- mechanically owned schema `1.7` evidence remains current according to `winapp-migrate uwp verify`;
- `winapp-migrate uwp closure assess "<target>" --strict` reports every closure gate as passed.

After updating state and finding evidence, run:

```powershell
winapp-migrate uwp closure finalize "<target>" --strict
```

Without `--strict`, `assess` and `finalize` synchronize and report closure
status without failing merely because work or evidence remains. Use strict mode
only for a final acceptance decision. A partial result is valid progress but
must be reported as partial or unverified, never as complete.

Do not hand-edit its derived validation summary.
Every assessment verifies canonical copy hashes against re-derived state
evidence and checks complete per-state report statuses/reasons. Valid-looking
replacement artifacts or matching aggregate statuses are not closure proof.
Never fabricate or manually repair `winapp-migrate uwp`-owned mechanical
evidence. Runtime parity and migration completion are separate claims: paired
source/target evidence may establish parity while required migration work
remains, and completed target work may coexist with `parityStatus: unverified`
when source evidence is unavailable.

Report unresolved behavior, unavailable analyzer checks, `winapp-migrate uwp` limitations, and behavioral-validation status. Do not claim parity from build success or process launch.
