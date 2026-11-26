---
name: uwp-to-winapp
description: "Use immediately when migrating, porting, or converting a C# UWP app to WinUI 3 or the Windows App SDK, including projects using Windows.UI.Xaml or UWP Package.appxmanifest files. C++/WinRT and Visual Basic UWP migrations are out of scope."
---

# UWP to WinApp migration

Preserve the application; do not redesign it. Keep every page, control, resource, helper, and navigation destination unless authoritative platform evidence proves that WinUI 3 has no desktop equivalent. Preserve consumed values, events, bindings, asynchronous completion, errors, and lifetime behavior; empty shims are not replacements.

Use `winapp-migrate uwp` for conversion, TODOs, source execution, and target closure. Do not invoke raw `winapp run`, `winapp build`, or `winapp ui`. The tool owns report facts, evidence validation, and verdicts; the agent owns source understanding, target implementation, and navigation recipes. Keep app-specific adapters in the migrated solution, not in the migration tools.

Machine acceptance is **UI Surface Parity**: necessary mechanical checks, an analyzer-enabled build, canonical launch, complete required/excluded source inventory, target reachability, and complete semantic UIA tree parity for every required surface. Behavioral, pixel, lifecycle, restoration, timing, pointer-input, accessibility-quality, and business-logic equivalence remain separate validation obligations, not permission to omit implementation.

Use `winapp-migrate --version` and the relevant command's `--help` to inspect the installed CLI. Let the tool validate artifact versions and report missing prerequisites; do not hand-edit mechanically owned report fields or derived evidence.

## Read migration knowledge on demand

| When | Reference |
|---|---|
| A source package or project cannot be used unchanged | [Dependency replacements](references/dependency-contracts.md) |
| The source declares protocol or file activation | [Activation routing](references/activation-contracts.md) |
| Window ownership, XAML, collections, or hosted controls behave differently after conversion | [Platform differences](references/platform-semantic-differences.md) |

## 1. Create or refresh the deterministic target

Identify the exact entry UWP `.csproj` and pass its containing project directory to `create`. Do not substitute a solution or unrelated repository root.

For a new target:

```powershell
winapp-migrate uwp create "<source-project-directory>" --output "<target>"
```

For an existing target, refresh mechanically owned evidence:

```powershell
winapp-migrate uwp verify "<target>"
```

Use `decide-project-item` for report-declared project-item decisions.

## 2. Inventory source UI surfaces

Read [the surface plan and capture guide](references/ui-surface-parity.md). Run `winapp-migrate uwp source inventory "<target>"` to obtain source definition keys, including inherited XAML roots and inline/code-created popups. Create `<target>\.migration-evidence\state-plan.json` using its schema and example. Account for all app-owned windows, pages, dialogs, flyouts, and independently presented user controls, including startup and navigation registrations. Seal the inventory before source capture or workflow assessment:

```powershell
winapp-migrate uwp closure seal-contract "<target>"
winapp-migrate uwp closure assess "<target>"
```

## 3. Capture source surface trees

```powershell
winapp-migrate uwp source run "<target>"
winapp-migrate uwp source capture "<target>" -- inspect -w <source-hwnd> --json
```

For each required surface, capture its initial state, navigate using `source capture`, and finish with one complete semantic UIA tree. Navigation may remain null until observed. Declare the actual route and `startSurfaceId` in the plan, then freeze it with `closure bind-source "<target>" --surface "<surface-id>" --probe "<initial-probe.json>" --probe "<action-probe.json>" --probe "<final-probe.json>"`. The tool sets the captured source observation; do not author success flags. A zero-action startup uses one initial inspect receipt. Bind incrementally; one unsupported surface does not prevent work on the others. Once a surface is bound, proceed with implementation, build, and target checks for it without waiting for every source route.

If automation cannot produce a complete tree, leave the surface unverified and report the condition. Never infer a tree from source code or substitute a screenshot.

## 4. Implement migration TODOs

Start from the report's detection facts, candidate strategies, risks, forbidden shortcuts, and completion checks. Strategies are non-exhaustive; choose a source-preserving implementation rather than forcing a suggested approach.

```powershell
winapp-migrate uwp todo status "<target>"
winapp-migrate uwp todo assess "<target>" "<todo-id>"
winapp-migrate uwp todo record "<target>" "<todo-id>" --outcome implemented --summary "<change>" --evidence "<path>"
```

Implementation and failure records are bookkeeping, not verification. Read `implementationStatus`, `verificationStatus`, and `verificationScope` independently. Follow each surface's `comparisonDiagnostics`: tool or capture faults require collecting trustworthy comparable evidence, not speculative application edits.

When a UI obligation lacks its proof mapping, bind the smallest existing required surface to the fixed checks that actually prove it. A `surface-binding` completion check is an unresolved obligation, not a surface ID; binding resolves it against the actual inventory without weakening its checks:

```powershell
winapp-migrate uwp todo bind-surface "<target>" "<todo-id>" `
  --surface "<surface-id>" `
  --check "target-reachable" `
  --check "uia-structure-match" `
  --check-id "<stable-check-id>" `
  --summary "<why-this-surface-proves-the-TODO>"
```

Project-only checks need no surface binding. Business behavior outside UI Surface Parity needs explicit human validation, not an invented homepage binding. Build and launch failures remain project-level work unless evidence proves an omitted app-owned migration obligation; use `todo discover` for that obligation, not for each diagnostic. Missing app-owned implementation is unfinished work, not an external blocker.

If delegating, partition work by disjoint touch/conflict sets, give overlapping work one owner, and integrate changes before the common build and replay.

## 5. Build and launch through the closure gateway

Ensure the target references `Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer` with `PrivateAssets="all"`, then run:

```powershell
winapp-migrate uwp closure attest "<target>"
```

Repair actual failures and retry as needed. Use `closure diagnose` for raw diagnostics and `todo record --outcome failed` to attach the repair summary and evidence to the existing TODO; discover a new TODO only for a new obligation. Diagnostics are not retry prerequisites. Collection or input-review failures require fixing the collection or inputs, not application code. After target changes, obtain a fresh build and replay before capturing target evidence.

## 6. Capture and record surface checks

For each source-bound surface, replay its verified navigation sequence against the current replay-owned HWND:

```powershell
winapp-migrate uwp closure navigate "<target>" --surface "<surface-id>" --window <target-hwnd>
```

The tool checks the declared starting state, stops at the first failed action, captures the final tree, and records the honest result. A reachable parent with a parity mismatch can still start a dependent route; it does not become verified. Manual `closure capture` and `record-surface` remain available but require the complete ordered trace, including its initial inspect. The tool derives reachability and tree parity and shares the current result with every exactly mapped TODO. Use the guide's result classifications and freshness rules rather than editing verdicts.

The tool automatically attempts screenshots alongside source and surface-bound target JSON inspections. Retain `.migration-evidence/comparison/index.html` and its linked artifacts for the before/after record; missing or stale screenshots are labeled, not an extra acceptance gate or a substitute for UIA evidence.

## 7. Finalize

After every required surface is verified:

```powershell
winapp-migrate uwp closure finalize "<target>" --strict
```

For an incomplete handoff, use `closure assess` and non-strict `closure finalize` to preserve honest current results. Report the product's `completion` checkpoint and remaining obligations; a model's final response does not mean migration completion, and `tool-blocked` does not mean parity passed.

When the host decides to end iteration, record its budget, no-progress, or external-blocker decision with `closure stop --reason <reason>`. The stopped result remains incomplete, retains outstanding gates, and returns nonzero; the tool does not choose the host's limits or treat stopping as success.

Report the `ui-surface-parity` verification scope, every human-validation item, remaining implementation obligations and findings, and excluded surfaces with their reasons. Out-of-scope obligations do not block UI acceptance or become verified when the app passes. Do not claim complete app equivalence from this bounded result.
