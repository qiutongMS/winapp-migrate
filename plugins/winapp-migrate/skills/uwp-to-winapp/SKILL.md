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

Read [the surface plan and capture guide](references/ui-surface-parity.md). Create `<target>\.migration-evidence\state-plan.json` using its schema and example. Account for all app-owned windows, pages, dialogs, flyouts, and independently presented user controls, including startup and navigation registrations. Seal the inventory before source capture or workflow assessment:

```powershell
winapp-migrate uwp closure seal-contract "<target>"
winapp-migrate uwp closure assess "<target>"
```

## 3. Capture source surface trees

```powershell
winapp-migrate uwp source run "<target>"
winapp-migrate uwp source capture "<target>" -- inspect -w <source-hwnd> --json
```

For each required surface, navigate using `source capture`, then capture one complete semantic UIA tree. Set its plan observation's `source.status` to `captured`, include the receipt path in `source.evidence`, and set `source.treeEvidence` to that same tree path.

If automation cannot produce a complete tree, leave the surface unverified and report the condition. Never infer a tree from source code or substitute a screenshot.

## 4. Implement migration TODOs

Start from the report's detection facts, candidate strategies, risks, forbidden shortcuts, and completion checks. Strategies are non-exhaustive; choose a source-preserving implementation rather than forcing a suggested approach.

```powershell
winapp-migrate uwp todo status "<target>"
winapp-migrate uwp todo assess "<target>" "<todo-id>"
winapp-migrate uwp todo record "<target>" "<todo-id>" --outcome implemented --summary "<change>" --evidence "<path>"
```

Implementation records and optional `todo select-approach` decisions are bookkeeping, not verification. Read `implementationStatus`, `verificationStatus`, and `verificationScope` independently.

When a UI obligation lacks its proof mapping, bind the smallest existing required surface to the fixed checks that actually prove it:

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

Repair actual failures and retry as needed. `closure diagnose`, `record-failure`, and `record-diagnosis` retain optional diagnostics; they are not retry prerequisites. After target changes, obtain a fresh build and replay before capturing target evidence.

## 6. Capture and record surface checks

For each required surface, execute its sealed navigation sequence and final complete-tree inspection against the current replay-owned HWND:

```powershell
winapp-migrate uwp closure capture "<target>" --surface "<surface-id>" -- <navigation-command> -w <target-hwnd>
winapp-migrate uwp closure capture "<target>" --surface "<surface-id>" -- inspect -w <target-hwnd> --json
winapp-migrate uwp closure record-surface "<target>" --surface "<surface-id>" `
  --probe "<navigation-probe.json>" `
  --probe "<inspect-probe.json>"
```

Startup surfaces may use only the inspect probe at index `0`. Submit all ordered receipts for other surfaces. The tool derives reachability and tree parity and shares the current result with every exactly mapped TODO. Use the guide's result classifications and freshness rules rather than editing verdicts.

The tool automatically attempts screenshots alongside source and surface-bound target JSON inspections. Retain `.migration-evidence/comparison/index.html` and its linked artifacts for the before/after record; missing or stale screenshots are labeled, not an extra acceptance gate or a substitute for UIA evidence.

## 7. Finalize

After every required surface is verified:

```powershell
winapp-migrate uwp closure finalize "<target>" --strict
```

Report the `ui-surface-parity` verification scope, every human-validation item, remaining implementation obligations and findings, and excluded surfaces with their reasons. Out-of-scope obligations do not block UI acceptance or become verified when the app passes. Do not claim complete app equivalence from this bounded result.
