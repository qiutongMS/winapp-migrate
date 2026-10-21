---
name: uwp-to-winapp
description: "Use immediately when migrating, porting, or converting a C# UWP app to WinUI 3 or the Windows App SDK, including projects using Windows.UI.Xaml or UWP Package.appxmanifest files. C++/WinRT and Visual Basic UWP migrations are out of scope."
---

# UWP to WinApp migration

Preserve the app; do not redesign it. Keep every page, control, resource, helper, navigation path, and user-visible behavior unless authoritative platform evidence proves there is no WinUI 3 desktop equivalent. Report unsupported behavior explicitly; never omit it silently.

This skill is the UWP pipeline in the preview `winapp-migrate` product. It uses `winapp-migrate uwp` for deterministic mechanical conversion and consumes its UWP migration report with schema `1.4`. WinApp CLI remains a separate build, launch, diagnosis, and UI-automation tool.

## Tool and contract compatibility

Treat three capabilities as independent:

| Capability | Required contract | Current compatibility truth |
|---|---|---|
| Mechanical UWP migration | `winapp-migrate uwp` with `migrate`, `verify`, and `decide-project-item`; `migration-report.json` schema `1.4` | The preview `winapp-migrate` build from this repository owns this capability. WinApp CLI does not. |
| Build, register, launch, diagnose, and automate | `winapp run` with `--no-launch`, `--no-build`, `--detach`, and `--json`, plus the required `winapp ui` operations | WinApp CLI 0.7+ can supply these capabilities when its local help lists them. |
| Static WinUI diagnostics | A project `PackageReference` to `Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer` with `PrivateAssets="all"` | The analyzer is a separate preview/`0.x` NuGet package. WinApp CLI does not inject it, and this plugin does not bundle its DLL. |

Run these checks before changing the source or target:

```powershell
winapp-migrate --version
winapp-migrate --help
winapp-migrate uwp --help
winapp-migrate uwp migrate --help
winapp-migrate uwp verify --help
winapp-migrate uwp decide-project-item --help
winapp --version
winapp --help
winapp run --help
winapp ui --help
```

Verify from the actual output that:

1. `winapp-migrate uwp` exposes `migrate`, `verify`, and `decide-project-item`;
2. `winapp` exposes `run` and `ui`;
3. `winapp run --help` lists `--no-launch`, `--no-build`, `--detach`, and `--json`;
4. the required UI subcommands are present before planning automated evidence.

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

- `winapp-migrate uwp` owns source-file classification, deterministic transforms, project-item accounting, activation declaration inventory, mechanical verification, and its mechanically owned schema `1.4` report fields.
- This skill owns semantic decisions, dependency-contract preservation, target implementation, and behavioral validation.
- WinApp CLI owns build, registration, launch, crash diagnostics, and UI automation only. Its success does not prove mechanical or behavioral migration completeness.
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

If usable source evidence still cannot be obtained, record the affected states as `unverified`; never infer parity from source code or build success. Treat a newly observed source window as a successful launch even when the launch command's output transport remains pending. Complete exact-window cleanup after each source-capture session before editing the target.

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

If NuGet cannot resolve the package, continue only when the rest of the build can proceed, retain the restore evidence, and tell the user that analyzer checks did not run. Do not claim an analyzer-enabled build.

For the packaged target, the standard build and registration command is:

```powershell
winapp run "<target.csproj>" --no-launch --json
```

`--no-launch` builds, prepares deployment, and registers a packaged app without starting it. It is not a build-only switch for an unpackaged target. Capture the JSON result, diagnostics, and exit code in a new iteration directory under `<target>\.migration-evidence\builds`.

On failure, read the complete error set, group it by root cause, and fix every governed occurrence in one pass. Do not build after every file or diagnostic. Target three grouped builds—initial convergence, root-cause correction, and final confirmation—but allow another when the previous result exposes a genuinely new signature.

If the same signature survives two builds, stop speculative edits and inspect the complete type, project-item, generated-code, and call-site context. Record expanded-path root causes in the semantic finding ledger. Build success closes only compile-time completion conditions.

Do not spend turns clearing advisory diagnostics unrelated to migration success.

## 5. Prove the architecture, then expand coverage

After the first successful analyzer-enabled build/register, launch the existing output without rebuilding:

```powershell
winapp run "<target.csproj>" --no-build --detach --json
```

Pass the same configuration, architecture, framework, and relevant MSBuild properties used by the build when they differ from defaults. Replay the active sentinel immediately. Control existence and process survival are not sufficient.

If the sentinel fails, it becomes the active migration frontier. Pause broader coverage, create or update its finding, return to the implementation and build steps, and correct the shared root cause. For startup or action-triggered process exits, follow the runtime diagnosis in [Behavioral validation](references/behavioral-validation.md). An unavailable pointer-input comparison is missing comparison evidence, not an external blocker for an app-owned crash.

After every sentinel passes, expand progressively to remaining feature paths and source states. Group features that share a dependency or architecture boundary and replay each group before moving to an independent group.

## 6. Falsify and finalize

After all target changes, start one fresh no-build process from the canonical initial state. Replay the primary sentinel followed by every runnable planned state. Persist the run identity, returned PID, initial HWND, launch time, target fingerprint, and final evidence.

This final pass attempts to disprove completion. Do not reuse a development process or old evidence. Reopen a finding when the clean sequence changes the signature or fails.

Before claiming completion:

- every source feature contract is represented by target implementation;
- every semantic finding is resolved, genuinely externally blocked, or truthfully unverified after its bounded evidence path;
- no finding retains an actionable app-owned location or next action;
- the current build is analyzer-enabled and successful;
- the primary sentinel and every runnable target state pass in one fresh canonical sequence;
- mechanically owned schema `1.4` evidence remains current according to `winapp-migrate uwp verify`;
- the schema `1.0` state plan, semantic findings, and report validation summary agree.

Update only semantically owned report fields documented by schema `1.4`. Never fabricate or manually repair `winapp-migrate uwp`-owned mechanical evidence. Runtime parity and migration completion are separate claims: paired source/target evidence may establish parity while required migration work remains, and completed target work may coexist with `parityStatus: unverified` when source evidence is unavailable.

Report unresolved behavior, unavailable analyzer checks, `winapp-migrate uwp` limitations, and behavioral-validation status. Do not claim parity from build success or process launch.
