---
name: uwp-to-winapp
description: "Use immediately when migrating, porting, or converting a C# UWP app to WinUI 3 or the Windows App SDK, including projects using Windows.UI.Xaml or UWP Package.appxmanifest files. C++/WinRT and Visual Basic UWP migrations are out of scope."
---

# UWP to WinApp migration

Preserve the app; do not redesign it. Keep every page, control, resource, helper, navigation path, and user-visible behavior unless authoritative platform evidence proves there is no WinUI 3 desktop equivalent. Report unsupported behavior explicitly; never omit it silently.

This skill is the UWP pipeline in the preview `winapp-migrate` product. It uses `winapp-migrate uwp` for deterministic mechanical conversion and consumes its UWP migration report with schema `1.5`. WinApp CLI remains a separate implementation tool, but every target build, launch, diagnosis, and UI action must cross the `winapp-migrate uwp closure` boundary.

## Tool and contract compatibility

Treat three capabilities as independent:

| Capability | Required contract | Current compatibility truth |
|---|---|---|
| UWP migration and closure | `winapp-migrate uwp` with `migrate`, `verify`, `decide-project-item`, and `closure`; migration report schema `1.5`, closure evidence schema `1.0`, workflow progress schema `3.0` | The preview `winapp-migrate` build from this repository owns these capabilities and invokes WinApp CLI at its target-operation boundary. |
| Build, register, launch, diagnose, and automate | Gateway-mediated WinApp CLI `run` and `ui` capabilities | WinApp CLI 0.7+ can supply these capabilities. The gateway resolves it from `--winapp`, then `WINAPP_MIGRATE_WINAPP_PATH`, then PATH; it is valid for the raw executable to be absent from the agent's PATH. |
| Static WinUI diagnostics | A project `PackageReference` to `Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer` with `PrivateAssets="all"` | The analyzer is a separate preview/`0.x` NuGet package. WinApp CLI does not inject it, and this plugin does not bundle its DLL. |

Run these checks before changing the source or target:

```powershell
winapp-migrate --version
winapp-migrate --help
winapp-migrate uwp --help
winapp-migrate uwp migrate --help
winapp-migrate uwp verify --help
winapp-migrate uwp decide-project-item --help
winapp-migrate uwp closure --help
```

Verify from the actual output that:

1. `winapp-migrate uwp` exposes `migrate`, `verify`, `decide-project-item`, and `closure`;
2. `winapp-migrate uwp closure --help` exposes `assess`, `attest`, `capture`, `finalize`, `diagnose`, `record-failure`, and `record-diagnosis`;
3. the configured WinApp CLI satisfies the gateway contract. If the raw executable is intentionally hidden from PATH, do not bypass that boundary merely to inspect its help.

Accept the `winapp-migrate uwp` pipeline only when its local help or versioned documentation explicitly promises schema `1.5`. Do not infer compatibility from a preview version number, a branch name, or the presence of this skill.

If `<target>\migration-report.json` does not exist, create the deterministic target before semantic edits:

```powershell
winapp-migrate uwp migrate "<source>" --output "<target>"
```

If the report already exists, parse it, require `schemaVersion` exactly `1.5`, and refresh mechanically owned evidence:

```powershell
winapp-migrate uwp verify "<target>"
```

The behavioral state plan remains a separate artifact with schema `1.0`; never upgrade or reject it merely because the migration report uses `1.5`.

### Start or resume closure

For a target with an existing report, inspect the state plan immediately. If a
valid state plan already exists, the next stateful command must be:

```powershell
winapp-migrate uwp closure assess "<target>"
```

Run this initial assessment before source UI capture, broad source or target
investigation, semantic target edits, or delegating implementation work. It
resumes the deterministic migration checklist and returns the authoritative
stage, frontier, disposition, and allowed operations. Elapsed time and operation
totals are telemetry only. If the state plan is missing, create only that plan
first, then assess before doing any capture or implementation.

Stop when the disposition is terminal. Otherwise, perform only work consistent
with the reported frontier and allowed operations. Reassess only at a workflow
boundary: after a target-fingerprint change, source/state completion, semantic
finding resolution, slice completion, or immediately before finalization. Do
not reassess after each build substep or UI command. The default bounded receipt
points to the durable full state; use `--full` only for focused workflow repair.
Target execution must still use the specific `closure attest`,
`closure diagnose`, or `closure capture` operation compatible with the current
stage and evidence frontier.

If neither a valid report nor a compatible `winapp-migrate uwp` pipeline is available, stop before scaffolding or copying application files. Report that deterministic migration is unavailable. Do not replace the missing tool with hand-written namespace replacement, a second scaffold, or a fabricated report.

## Ownership

- `winapp-migrate uwp` owns source-file classification, deterministic transforms, project-item accounting, activation declaration inventory, mechanical verification, canonical closure attestations, evidence freshness, closure gates, and its mechanically owned schema `1.5` report fields.
- This skill owns semantic decisions, dependency-contract preservation, target implementation, and behavioral validation.
- WinApp CLI owns the underlying build, registration, launch, crash diagnostics, and UI automation. The closure gateway owns every target invocation and its provenance; read-only source baseline capture may still use WinApp CLI directly.
- The analyzer reports static WinUI risks during ordinary builds. It does not create the migration report and does not replace runtime replay.

Do not duplicate `winapp-migrate uwp` checks with ad hoc extension lists or repository-wide searches when current schema `1.5` evidence already covers them. An empty migration TODO list still does not guarantee a buildable, runnable, or behaviorally equivalent app.

## 1. Validate the migration input and classify risk

Before semantic edits:

1. Identify the exact entry UWP `.csproj`; never substitute a repository or solution root merely because it contains the project recursively.
2. Confirm `<target>\migration-report.json` exists and has schema `1.5`.
3. Confirm `source.projectFile` identifies that exact entry project.
4. Require the overall mechanical result, project graph, project-item accounting, and mechanically eligible activation declarations to be in the report-documented ready state.
5. If mechanically owned evidence is stale or failed, run `winapp-migrate uwp verify "<target>"`. For a decision-required project item, use the stable item ID and an allowed strategy from `winapp-migrate uwp decide-project-item --help`, then run `verify` again. Never edit mechanically owned fields by hand.
6. For every required TODO with `guidance`, treat its `facts` and `mechanicallyAppliedTransforms` as tool-owned evidence. A `candidateStrategy` is applicable only when its listed preconditions are proven from the source and target; record the selected strategy in the semantic finding ledger and obey its risks, `forbiddenShortcuts`, and `completionConditions`. Map the TODO to every state needed to prove those conditions. Do not inject guidance as target-code comments or treat a candidate as an automatic repair.
7. Read startup, the feature registry or navigation shell, `dependencyAnalysis`, `activationAnalysis`, and enough source to classify the migration as fast or expanded.
8. Identify the source behavior that proves the app's primary purpose and any user-critical flow.

Use the **fast path** when the primary behavior is page-local, dependencies and APIs have direct target mappings, and it crosses no uncertain project, lifetime, activation, window, native-host, or shared asynchronous boundary. Read only startup, capability-bearing manifest entries, relevant pages, and their custom controls or resources. Do not create a semantic finding ledger before a real finding exists.

Use the **expanded path** when a critical behavior depends on a `review-required` dependency; spans projects; requires an adapter, source port, or equivalent implementation; crosses activation, background, multi-window, native-host, lifecycle, shared-state, or asynchronous ownership boundaries; or when the first build or runtime probe exposes an unknown shared failure. Once a migration expands, do not downgrade it merely because a local workaround compiles.

On the expanded path, follow [Semantic migration protocol](references/semantic-migration-protocol.md) and select at most three migration-critical seams. Each seam joins a source behavior to the target architecture through an uncertain dependency, lifetime, state, or platform boundary. Choose one sentinel flow that proves startup plus the highest-risk seam; add another only for a genuinely independent risk.

## 2. Capture the source behavior baseline

Follow [Behavioral validation](references/behavioral-validation.md) to create and persist the complete state plan declared by the report. The state plan uses schema `1.0`.

Before editing, capture startup and the fast-path primary sentinel, or every expanded-path seam sentinel. Capture remaining source states before implementing or validating their corresponding target feature. Complete the bounded source-recovery and evidence-fallback process before declaring a state unavailable. The initial assessment required by the closure entry protocol must already be persisted before this capture starts.

If usable source evidence still cannot be obtained, record the affected states as `unverified`; never infer parity from source code or build success. Treat a newly observed source window as a successful launch even when the launch command's output transport remains pending. Complete exact-window cleanup after each source-capture session before editing the target.

Immediately after the required source states are captured and persisted, run
`winapp-migrate uwp closure assess "<target>"` again. Require the snapshot to
enter `mechanical-stabilization` before target implementation begins; this
observation closes the source unit and activates the target checklist. Do not
perform target work between changing the source statuses and this assessment.

## 3. Build the first viable slice

Treat report categories as evidence within the active behavior scope, not as an exhaustive worklist or a completion definition.

On the fast path, migrate the complete small behavior surface as one coherent patch when practical. On the expanded path, implement the smallest architecture slice that can build and exercise startup plus the active sentinel: its consumed dependency contract, owning projects, shared abstraction, and direct call path.

Common checks include:

- merge application resources without replacing the WinUI startup bootstrap;
- restore only dependencies and manifest declarations required by preserved features;
- replace dispatcher and windowing APIs through shared WinUI 3 abstractions;
- reconcile shared-file conflicts without losing either side's required behavior;
- wire the initial page without replacing generated bootstrap or title-bar behavior.

Use schema `1.5` `dependencyAnalysis` as the source dependency inventory, not as a package recommendation. Follow [Dependency contracts](references/dependency-contracts.md) for every `review-required` dependency and every package replacement, adapter, source port, or equivalent implementation. Resolve or explicitly account for every `inspectionIssue` when the analysis is incomplete.

Use `activationAnalysis` as the declaration inventory, not as evidence that activation behavior works. For every activation contract in scope, follow [Activation contracts](references/activation-contracts.md). A translated declaration does not prove routing, single-instance behavior, navigation, file loading, restoration, or error handling.

For unknown report categories, use their `summary`, `reason`, and `locations`; do not guess from an ID. Resolve mappings that block the current slice or govern a shared root cause. Preserve source XAML bindings, event handlers, default selection, initialization order, navigation reachability, AutomationIds, and observable outcomes.

Use [Platform semantic differences](references/platform-semantic-differences.md) as a non-exhaustive review accelerator. When an API mapping is uncertain, consult the official [UWP to Windows App SDK mapping table](https://learn.microsoft.com/windows/apps/windows-app-sdk/migrate-to-windows-app-sdk/api-mapping-table). A visible fallback is a documented limitation, not evidence of parity.

## 4. Add the analyzer and build/register in batches

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

An unchanged failed target cannot be built again. Change the governed target
fingerprint before requesting another build. Repeated replay requires fresh
discriminating evidence, and repeated diagnosis or hypothesis for the same
signature and fingerprint is rejected. Elapsed time and aggregate operation
counts never authorize, cancel, defer, or terminate otherwise valid work.
Progressive corrections may use as many informative gateway operations as
needed.

Source oracle allows no target operations; mechanical stabilization allows
builds only; bootstrap allows replay and runtime-failure diagnosis/hypothesis,
never UI automation. Closure permits revalidation build/replay/UI only when
the evidence frontier requires it. Repeated replay requires new
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

If the sentinel fails, it becomes the active migration frontier. For a
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
and source states. Use optional schema `1.0` `slice`, `dependencies` (slice IDs),
and `criticality` fields to group states by dependency or architecture boundary.
During semantic convergence, let the gateway record automatic provenance for
each target UI probe. Do not create hand-written justification JSON for every
`list-windows`, `inspect`, `invoke`, or `screenshot`; update the state plan once
the state outcome is known. `closure capture` writes a bounded receipt by
default and persists raw stdout/stderr plus
`probes/<probe-id>/probe.json`. Read only the relevant persisted output; use
`--raw-output` only when that focused payload is itself needed.
The gateway selects only the first dependency-ready semantically unresolved slice. Required
blocked work stops; optional debt never verifies a state or unlocks a dependent
slice. Follow snapshot `activeSlice`, `activeStateIds`, and `frontier`. Whenever
state evidence completes a slice, persist the state-plan update and immediately
run `closure assess` before editing the next slice. That assessment records the
resolution and activates the next dependency-ready slice.
A target change makes resolved evidence stale without returning those slices
to semantic implementation work. Closure selects stale resolved evidence with
a `revalidate` frontier; failed comparisons, runtime regressions, and open
findings can reopen semantic work before terminal completion.
Inspect snapshot `disposition`, not just the legacy `status`.
`continue-with-debt` is nonterminal. `complete`, genuinely
`blocked-required`, and `infrastructure-failure` are durable terminal outcomes
and reject target operations. Historical `total-hard-limit`,
`unit-hard-limit`, `operation-ceiling`, and `stage-soft-limit` dispositions are
retired and resume during schema import.

## 6. Falsify and finalize

After all target changes, run the following command with the same
build-selection options:

```powershell
winapp-migrate uwp closure attest "<target>" --force-replay --discriminating-evidence "<new-target-relative-evidence>"
```

Fresh discriminating evidence must justify a repeated replay. Touching,
renaming, or reusing the same evidence content is not progress.
`--force-replay` cannot bypass diagnosis, change-required, or evidence freshness.
Finish this falsification pass before report synchronization makes a fully
passed closure terminal; do not request more target operations afterward.

From that fresh canonical process, replay the primary sentinel followed by
every runnable planned state. For each state, create one target-relative intent
after the recorded replay starts. Its content must name the current target
fingerprint, replay attestation ID, state ID, and expected outcome. Pass that
same file to each distinct action in the state sequence:

```powershell
winapp-migrate uwp closure capture "<target>" --discriminating-evidence "<state-intent>" -- <ui-arguments>
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
clause before setting the state to passed/verified. Navigation to a destination
does not prove that its navigation item is selected; visible text does not prove
`IsSelected`, toggle state, value retention, focus, enabled state, or restored
state. Query the relevant property after the decisive action and retain that
result with the final tree. If any expected property is false or missing, keep
the state failed or unverified and reopen the linked finding.

This final pass attempts to disprove completion. Do not reuse a development process or old evidence. Reopen a finding when the clean sequence changes the signature or fails.

Before claiming completion:

- every source feature contract is represented by target implementation;
- every semantic finding is resolved, genuinely externally blocked, or truthfully unverified after its bounded evidence path;
- no finding retains an actionable app-owned location or next action;
- the current build is analyzer-enabled and successful;
- the primary sentinel and every runnable target state pass in one fresh canonical sequence;
- mechanically owned schema `1.5` evidence remains current according to `winapp-migrate uwp verify`;
- `winapp-migrate uwp closure assess "<target>"` reports every closure gate as passed.

After updating state and finding evidence, run:

```powershell
winapp-migrate uwp closure finalize "<target>"
```

Do not hand-edit its derived validation summary.
Never fabricate or manually repair `winapp-migrate uwp`-owned mechanical
evidence. Runtime parity and migration completion are separate claims: paired
source/target evidence may establish parity while required migration work
remains, and completed target work may coexist with `parityStatus: unverified`
when source evidence is unavailable.

Report unresolved behavior, unavailable analyzer checks, `winapp-migrate uwp` limitations, and behavioral-validation status. Do not claim parity from build success or process launch.
