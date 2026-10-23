---
name: uwp-to-winapp
description: "Use immediately when migrating, porting, or converting a C# UWP app to WinUI 3 or the Windows App SDK, including projects using Windows.UI.Xaml or UWP Package.appxmanifest files. C++/WinRT and Visual Basic UWP migrations are out of scope."
---

# UWP to WinApp migration

Preserve the app; do not redesign it. Keep every page, control, resource, helper, navigation path, and user-visible behavior unless authoritative platform evidence proves there is no WinUI 3 desktop equivalent. Report unsupported behavior explicitly; never omit it silently.

This skill is the UWP pipeline in the preview `winapp-migrate` product. It uses `winapp-migrate uwp` for deterministic mechanical conversion and consumes its UWP migration report with schema `1.4`. WinApp CLI remains a separate implementation tool, but every target build, launch, diagnosis, and UI action must cross the `winapp-migrate uwp enforce` boundary.

## Tool and contract compatibility

Treat three capabilities as independent:

| Capability | Required contract | Current compatibility truth |
|---|---|---|
| UWP migration and enforcement | `winapp-migrate uwp` with `migrate`, `verify`, `decide-project-item`, and `enforce`; migration report schema `1.4` plus enforcement evidence schema `1.0` | The preview `winapp-migrate` build from this repository owns these capabilities and invokes WinApp CLI at its process boundary. |
| Build, register, launch, diagnose, and automate | Runner-mediated WinApp CLI `run` and `ui` capabilities | WinApp CLI 0.7+ can supply these capabilities. The runner resolves it from `--winapp`, then `WINAPP_MIGRATE_WINAPP_PATH`, then PATH; it is valid for the raw executable to be absent from the agent's PATH. |
| Static WinUI diagnostics | A project `PackageReference` to `Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer` with `PrivateAssets="all"` | The analyzer is a separate preview/`0.x` NuGet package. WinApp CLI does not inject it, and this plugin does not bundle its DLL. |

Run these checks before changing the source or target:

```powershell
winapp-migrate --version
winapp-migrate --help
winapp-migrate uwp --help
winapp-migrate uwp migrate --help
winapp-migrate uwp verify --help
winapp-migrate uwp decide-project-item --help
winapp-migrate uwp enforce --help
```

Verify from the actual output that:

1. `winapp-migrate uwp` exposes `migrate`, `verify`, `decide-project-item`, and `enforce`;
2. `winapp-migrate uwp enforce --help` exposes `configure`, `run`, `diagnose`, `ui`, `record-failure`, `record-diagnosis`, `assess`, and `sync-report`;
3. the configured WinApp CLI satisfies the runner contract. If the raw executable is intentionally hidden from PATH, do not bypass that boundary merely to inspect its help.

Accept the `winapp-migrate uwp` pipeline only when its local help or versioned documentation explicitly promises schema `1.4`. Do not infer compatibility from a preview version number, a branch name, or the presence of this skill.

If `<target>\migration-report.json` does not exist, create the deterministic target before semantic edits:

```powershell
winapp-migrate uwp migrate "<source>" --output "<target>"
```

If the report already exists, parse it, require `schemaVersion` exactly `1.4`, and refresh mechanically owned evidence:

```powershell
winapp-migrate uwp verify "<target>"
```

The behavioral state plan remains a separate artifact with schema `1.0`; never upgrade or reject it merely because the migration report uses `1.4`.

If neither a valid report nor a compatible `winapp-migrate uwp` pipeline is available, stop before scaffolding or copying application files. Report that deterministic migration is unavailable. Do not replace the missing tool with hand-written namespace replacement, a second scaffold, or a fabricated report.

## Ownership

- `winapp-migrate uwp` owns source-file classification, deterministic transforms, project-item accounting, activation declaration inventory, mechanical verification, enforcement attestations, retry budgets, closure gates, and its mechanically owned schema `1.4` report fields.
- This skill owns semantic decisions, dependency-contract preservation, target implementation, and behavioral validation.
- WinApp CLI owns the underlying build, registration, launch, crash diagnostics, and UI automation. The runner owns every target invocation and its provenance; source baseline capture may still use WinApp CLI directly.
- The analyzer reports static WinUI risks during ordinary builds. It does not create the migration report and does not replace runtime replay.

Do not duplicate `winapp-migrate uwp` checks with ad hoc extension lists or repository-wide searches when current schema `1.4` evidence already covers them. An empty migration TODO list still does not guarantee a buildable, runnable, or behaviorally equivalent app.

## 1. Validate the migration input and classify risk

Before semantic edits:

1. Identify the exact entry UWP `.csproj`; never substitute a repository or solution root merely because it contains the project recursively.
2. Confirm `<target>\migration-report.json` exists and has schema `1.4`.
3. Confirm `source.projectFile` identifies that exact entry project.
4. Require the overall mechanical result, project graph, project-item accounting, and mechanically eligible activation declarations to be in the report-documented ready state.
5. If mechanically owned evidence is stale or failed, run `winapp-migrate uwp verify "<target>"`. For a decision-required project item, use the stable item ID and an allowed strategy from `winapp-migrate uwp decide-project-item --help`, then run `verify` again. Never edit mechanically owned fields by hand.
6. Read startup, the feature registry or navigation shell, `dependencyAnalysis`, `activationAnalysis`, and enough source to classify the migration as fast or expanded.
7. Identify the source behavior that proves the app's primary purpose and any user-critical flow.

Use the **fast path** when the primary behavior is page-local, dependencies and APIs have direct target mappings, and it crosses no uncertain project, lifetime, activation, window, native-host, or shared asynchronous boundary. Read only startup, capability-bearing manifest entries, relevant pages, and their custom controls or resources. Do not create a semantic finding ledger before a real finding exists.

Use the **expanded path** when a critical behavior depends on a `review-required` dependency; spans projects; requires an adapter, source port, or equivalent implementation; crosses activation, background, multi-window, native-host, lifecycle, shared-state, or asynchronous ownership boundaries; or when the first build or runtime probe exposes an unknown shared failure. Once a migration expands, do not downgrade it merely because a local workaround compiles.

On the expanded path, follow [Semantic migration protocol](references/semantic-migration-protocol.md) and select at most three migration-critical seams. Each seam joins a source behavior to the target architecture through an uncertain dependency, lifetime, state, or platform boundary. Choose one sentinel flow that proves startup plus the highest-risk seam; add another only for a genuinely independent risk.

## 2. Capture the source behavior baseline

Follow [Behavioral validation](references/behavioral-validation.md) to create and persist the complete state plan declared by the report. The state plan uses schema `1.0`.

Before editing, capture startup and the fast-path primary sentinel, or every expanded-path seam sentinel. Capture remaining source states before implementing or validating their corresponding target feature. Complete the bounded source-recovery and evidence-fallback process before declaring a state unavailable.

After persisting the state plan and before starting source capture, run
`winapp-migrate uwp enforce assess "<target>"`. This atomically installs the
balanced policy when no policy exists, starts the durable source-oracle budget,
and reports its deadline. Install a custom policy with `enforce configure`
before this first assessment when needed. Do not postpone the first assessment
until source capture is complete, because that would leave the source phase
outside the workflow clock.

If usable source evidence still cannot be obtained, record the affected states as `unverified`; never infer parity from source code or build success. Treat a newly observed source window as a successful launch even when the launch command's output transport remains pending. Complete exact-window cleanup after each source-capture session before editing the target.

Immediately after the required source states are captured and persisted, run
`winapp-migrate uwp enforce assess "<target>"` again. Require the snapshot to
enter `mechanical-stabilization` before target implementation begins; this
observation closes the source unit and starts the mechanical clock. Do not
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

Use schema `1.4` `dependencyAnalysis` as the source dependency inventory, not as a package recommendation. Follow [Dependency contracts](references/dependency-contracts.md) for every `review-required` dependency and every package replacement, adapter, source port, or equivalent implementation. Resolve or explicitly account for every `inspectionIssue` when the analysis is incomplete.

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
diagnosis manually, but the enforcement runner must not attest or close the
build. Do not claim an analyzer-enabled build.

For the packaged target, let the enforcement runner build/register and then
start the detached canonical replay:

```powershell
winapp-migrate uwp enforce run "<target>"
```

Pass `--configuration`, `--architecture`, `--runtime`, `--framework`, and
repeatable `--property Name=Value` arguments when defaults are not correct.
The runner forwards the same values to WinApp CLI's `--no-launch --json` build
and `--no-build --detach --json` replay. It writes raw output, grouped
diagnostics, target fingerprints, and versioned attestations under
`<target>\.migration-evidence\enforcement`; do not duplicate that bookkeeping.
Never invoke raw `winapp run` for the target. A raw target invocation has no
runner attestation and its output cannot satisfy a closure gate.

On failure, read the current enforcement state and grouped build diagnostics,
then fix every governed occurrence of the active root cause in one pass. Do
not build after every file or diagnostic.

An unchanged failed target cannot be built again. The persisted stage/slice
time and operation budgets also survive new CLI invocations; `stage-budget`
means stop and inspect the measured budget decision, not rerun the command.
Configure budgets before operations with `enforce configure --write-default`
or `--policy <file>` (both also take `<target>`). After semantic convergence
starts, corrections remain in that stage and reuse the active slice's budget.
The initial analyzer-attested build is charged to mechanical stabilization,
not target bootstrap; the initial replay is charged to target bootstrap.
Balanced policy grants at most one measured-progress extension independently
per stage or semantic slice (`maximumProgressExtensionsPerUnit`), subject to
the unchanged stage and workflow hard deadlines. Source oracle allows no
target operations; mechanical stabilization allows builds only; bootstrap
allows replay and runtime-failure diagnosis/hypothesis, never UI automation.
Closure allows only final replay/UI justified by `--discriminating-evidence`;
report sync is not a counted external operation. Custom ceilings cannot enable
operations incompatible with their stage.
Stop speculative edits and inspect the complete
type, project-item, generated-code, and call-site context. Record expanded-path
root causes in the semantic finding ledger. Build success closes only
compile-time completion conditions.

Do not spend turns clearing advisory diagnostics unrelated to migration success.

## 5. Prove the architecture, then expand coverage

After the runner returns a successful detached-launch attestation, replay the
active sentinel immediately. Control existence and process survival are not
sufficient.

If the sentinel fails, it becomes the active migration frontier. For a
foreground target diagnostic, use only:

```powershell
winapp-migrate uwp enforce diagnose "<target>"
```

The runner preserves stdout and stderr under
`<target>\.migration-evidence\enforcement\diagnostics`. If the failed canonical
replay already recorded the runtime attempt, do not record it a second time.
For a failure first observed during a later UI action, persist its evidence and
record one stable signature:

```powershell
winapp-migrate uwp enforce record-failure "<target>" runtime "<signature>" --state "<state-id>" --evidence "<target-relative-path>"
```

Before any target rebuild or replay, compare the failing source and target
paths. Start at the source handler, constructor, navigation callback, or
transition corresponding to the failed action; read its comments, guards,
exception workarounds, ordering, and lifecycle assumptions before interpreting
native stack noise. Persist the source/target diff and record one falsifiable
hypothesis and one discriminating test:

```powershell
winapp-migrate uwp enforce record-diagnosis "<target>" "<signature>" `
  --state "<state-id>" `
  --source-location "<path:line>" `
  --target-location "<path:line>" `
  --source-invariant "<behavior or workaround to preserve>" `
  --hypothesis "<single falsifiable cause>" `
  --test "<one change or observation that distinguishes the cause>" `
  --evidence "<fresh-target-relative-diagnosis-path>"
```

The runner exposes `diagnosis-required` until this source-first diagnosis is
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
The runner selects only the first dependency-ready incomplete slice. Required
blocked work stops; optional debt never verifies a state or unlocks a dependent
slice. Follow snapshot `activeSlice`, `activeStateIds`, and `budget`. Whenever
state evidence completes a slice, persist the state-plan update and immediately
run `enforce assess` before editing the next slice. That assessment records the
completion and starts the next slice's independent clock and counters.

## 6. Falsify and finalize

After all target changes, run the following command with the same
build-selection options:

```powershell
winapp-migrate uwp enforce run "<target>" --force-replay --discriminating-evidence "<new-target-relative-evidence>"
```

Fresh discriminating evidence must justify a repeated replay. Touching,
renaming, or reusing the same evidence content is not progress.
`--force-replay` cannot bypass diagnosis, change-required, or stage budgets.

From that fresh canonical process, replay the primary sentinel followed by
every runnable planned state. Persist state evidence after the recorded replay
start; obtain target window-specific evidence through
`winapp-migrate uwp enforce ui "<target>" --discriminating-evidence
"<new-target-relative-evidence>" -- <ui-arguments>` during closure rather than
invoking raw `winapp ui` or inventing an HWND from launch output. Each closure
UI operation needs explicit fresh justification.

This final pass attempts to disprove completion. Do not reuse a development process or old evidence. Reopen a finding when the clean sequence changes the signature or fails.

Before claiming completion:

- every source feature contract is represented by target implementation;
- every semantic finding is resolved, genuinely externally blocked, or truthfully unverified after its bounded evidence path;
- no finding retains an actionable app-owned location or next action;
- the current build is analyzer-enabled and successful;
- the primary sentinel and every runnable target state pass in one fresh canonical sequence;
- mechanically owned schema `1.4` evidence remains current according to `winapp-migrate uwp verify`;
- `winapp-migrate uwp enforce assess "<target>"` reports every closure gate as passed.

After updating state and finding evidence, run:

```powershell
winapp-migrate uwp enforce sync-report "<target>"
```

Do not hand-edit its derived validation summary.
Never fabricate or manually repair `winapp-migrate uwp`-owned mechanical
evidence. Runtime parity and migration completion are separate claims: paired
source/target evidence may establish parity while required migration work
remains, and completed target work may coexist with `parityStatus: unverified`
when source evidence is unavailable.

Report unresolved behavior, unavailable analyzer checks, `winapp-migrate uwp` limitations, and behavioral-validation status. Do not claim parity from build success or process launch.
