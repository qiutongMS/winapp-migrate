# Behavioral validation

Use this protocol to preserve observable behavior across the UWP-to-WinUI migration. It orchestrates installed tools; do not create helper scripts, temporary test projects, or app-specific CLI extensions.

The migration report uses schema `1.4`. The state plan below is a separate artifact and stays at schema `1.0`.

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

Set `source.status` to `captured`, `blocked`, or `unverified`; `target.status` to `passed`, `blocked`, `unverified`, or `failed`; and `comparison.status` to `verified`, `blocked`, `unverified`, or `failed`. Keep `not-run` only until the phase is attempted, and provide `reason` for every status other than `captured`, `passed`, or `verified`.

For state-changing behavior, plan a transition rather than only a destination. Verify changed values, selection, collections, navigation state, or visible content. Include cancel/back restoration and alternate modifier-key paths when they produce different source behavior.

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

## 3. Build/register and replay WinUI 3

The target project must reference `Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer` with `PrivateAssets="all"`. The package is not bundled with this plugin and is not injected by WinApp CLI.

Build and register the packaged target without launching:

```powershell
winapp run "<target.csproj>" --no-launch --json
```

Preserve the JSON result, diagnostics, and exit code. This is the standard target convergence build. Do not substitute a wrapper script or a copied analyzer DLL.

After the build succeeds, launch the existing output without rebuilding:

```powershell
winapp run "<target.csproj>" --no-build --detach --json
```

Pass the same configuration, architecture, framework, runtime, and relevant MSBuild properties used by the build. Replay the active sentinel before peripheral features.

If the detached app exits, turns blank, or never exposes a usable window, run one foreground diagnostic launch:

```powershell
winapp run "<target.csproj>" --no-build --debug-output
```

`--debug-output` cannot combine with `--detach` or `--json`. Record a crash signature consisting of the exception or HRESULT, the first app-owned stack frame, and the WinUI triage verdict, then create or update its semantic finding.

If diagnostics terminate without an app-owned cause, allow one focused temporary instrumentation pass at the narrowest existing app-owned constructors, handlers, and transition helpers. Remove all temporary instrumentation before the final analyzer-enabled build and clean replay.

When the target exits only while a semantic action is driven through `winapp ui invoke`, or a native crash stack is dominated by `UIAutomationCore` without an app-owned frame, test the same control once with `winapp ui click` after a clean launch.

If `invoke` fails while pointer input succeeds, inspect the complete downstream path for overlapping fire-and-forget tasks, reentrant navigation, frame replacement, overlay removal, or disposal while UI Automation is still returning. Serialize and await app-owned transitions, prevent re-entry, and remove visual-tree elements only after the action and required exit transition complete.

If pointer input cannot run because the host lacks an interactive desktop or click support, record that limitation only for the input-method comparison. Keep the invoke-triggered app defect `failed`; do not mark it `blocked`.

Group all locations explained by one signature into one correction. For the same signature, allow at most two correction-and-probe cycles. Each cycle must state one root-cause hypothesis the next probe can disprove. If unchanged after the second cycle, stop speculative edits and retain the failure evidence.

Only when mechanical evidence, build diagnostics, WinUI triage, resource resolution, and generated XAML provide no actionable cause may you bisect XAML. Disable approximately half of one root subtree per probe, change only that dimension, and stop after four probes. Restore the complete page before applying the root-cause fix.

Reuse the running app during development replay. Restart only when a state requires clean startup, prior actions cannot be reversed, or a correction requires a new process. Development observations are provisional.

After progressive coverage, close the development process and start one fresh no-build process. Replay the primary sentinel first, then every runnable state in order. Only this final falsification pass may set target states to `passed` and resolve runtime findings.

Before the first final action, persist a stable run ID, launch command, returned primary PID, initial HWND, target fingerprint, and launch time. Include the run ID and actual HWND in every final action, tree, screenshot, and health record. Evidence from a replacement process cannot complete the earlier run.

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

When finalizing schema `1.4` `migration-report.json`, keep the schema `1.0` state-plan path and evidence roots stable. Summarize:

- `sourceBaseline.status`: `captured` when every source state has evidence, `partial` when some do, or `unverified` when none do;
- `targetReplay.status`: `passed` when every target state succeeds, `partial` when at least one is blocked or unverified, or `failed` when any state fails;
- `parityStatus`: `verified` when every comparison is verified, `partial` when verified states coexist with blocked or unverified states, `unverified` when no paired comparison completes, or `failed` when any comparison fails.

Provide a concise reason whenever a status is not the successful first value. Before writing summaries, reconcile the state plan, findings, and report as one closure transaction. Evidence for an attempted state whose plan remains `not-run`, or evidence satisfying a finding that remains open, invalidates completion.
