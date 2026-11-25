# UI Surface Parity

Use this guide to prepare `.migration-evidence\state-plan.json` and interpret surface results. The [schema 3.0 definition](surface-plan-v3.schema.json) specifies the input structure; the tool validates evidence and derives acceptance.

## Plan inputs

Account for each source definition exactly once with kind `window`, `page`, `dialog`, `flyout`, or `user-control`. A `required` surface needs a source tree and target navigation ending in a complete tree inspection. An `excluded` surface needs a concrete reason, `navigation: null`, and `excluded` source, target, and comparison statuses.

Run `winapp-migrate uwp source inventory "<target>"` before authoring the plan. It lists definition keys and kinds without launching either application or starting evidence operations. XAML roots use their relative file paths; inline XAML and C# popup declarations use `path#L<line>C<column>` keys. Source-only C# analysis resolves aliases and local base-type chains; unresolved custom XAML roots are reported rather than silently omitted. Review startup/navigation and runtime-created UI beyond statically discoverable declarations; a tool limitation is not a reason to drop a required surface.

Minimal startup-page example; replace its ID and source path with the actual inventory:

```json
{
  "schemaVersion": "3.0",
  "surfaces": [
    {
      "id": "startup",
      "kind": "page",
      "disposition": "required",
      "sourceDefinitions": ["Views/MainPage.xaml"],
      "criticality": "required",
      "navigation": {
        "requiredActions": [],
        "treeProbeIndex": 0
      },
      "source": { "status": "not-run", "evidence": [] },
      "target": { "status": "not-run", "evidence": [] },
      "comparison": { "status": "not-run" }
    }
  ]
}
```

## Navigation and sealing

Seal the inventory with `closure seal-contract` before source discovery. New required-contract schema `3.0` freezes the obligations, not guessed navigation recipes. Leave `navigation` null until the source route has been observed; bind surfaces independently. Existing required-contract schema `2.0` retains its old immutable navigation and needs a fresh workspace for this lifecycle.

For an observed route, identify its starting surface with `startSurfaceId`; omit it only for a fresh launch before any state-changing action. Probe 0 is a complete initial inspect. Actions occupy consecutive indices 1..N, followed by a final inspect at N+1. A zero-action surface uses only inspect at index 0:

```json
{
  "startSurfaceId": "startup",
  "requiredActions": [
    {
      "id": "open-settings",
      "probeIndex": 1,
      "command": ["invoke", "Settings"]
    }
  ],
  "treeProbeIndex": 2
}
```

After source capture, run `closure bind-source "<target>" --surface "<surface-id>" --probe "<initial-probe.json>" --probe "<action-probe.json>" --probe "<final-probe.json>"`. It verifies gateway-authenticated receipts, unchanged source, launch/window identity, command ordering, initial and final trees, and the complete action history, then freezes this route and baseline. A warm startup, an omitted failed action, modified evidence, or a start state different from the declared surface is rejected. Do not copy receipts or trust anchors from another workspace.

The inventory check still rejects missing, extra, duplicate, or kind-mismatched definitions. `seal-contract --extend` can only append surfaces before source/workflow activity; it cannot change established entries.

Use source-run-authorized HWNDs for `source capture` and the current replay-owned HWND for target `closure capture --surface`. Submit the navigation probes in their declared order and finish with one complete `inspect --json`; that final tree is compared with the source tree. Use an unbound `closure capture "<target>" -- list-windows --json` if target window discovery is needed.

Prefer `closure navigate "<target>" --surface "<surface-id>" --window <target-hwnd>` for the target route. It inspects the current starting state, verifies it against the current recorded target tree of `startSurfaceId`, executes actions sequentially, and stops immediately at the first failure. Successful execution records the surface, even if comparison honestly reports mismatch. A starting surface needs current authenticated reachability, not parity success. Failed or interleaved actions cannot be omitted when recording a trace.

## Changes and retries

After target changes, run `closure attest` and recapture target surfaces. Use `closure attest "<target>" --force-replay` to relaunch an unchanged successfully built target when needed. Committed surface-proof schema `3.0` preserves observations across replays of the same canonical build, source/navigation contract, selector generation, declared inputs, and validator comparison build. A new build, changed inputs or evidence, or a different comparison build invalidates incompatible proof. Legacy `2.0` proofs remain replay-bound; they are not silently upgraded.

Do not edit frozen navigation commands. If a locator becomes ambiguous while its action and payload remain correct, use:

```powershell
winapp-migrate uwp closure rebind-selector "<target>" --surface "<surface-id>" --action "<action-id>" --reason "<reason>" -- <replacement-ui-command>
```

Rebinding changes only the locator and requires new target probes for that surface. `record-surface` can reuse an unchanged, still-valid probe sequence. Capture and recording check live window ownership. A committed proof retains that collection-time identity after the window closes; new capture and navigation still require an authenticated live replay and a fresh complete starting-state inspect. Changed trees or receipt contents invalidate affected proof; never edit verdicts manually.

## Tree comparison and results

The comparator walks the entire semantic UIA tree, comparing node type, AutomationId, name/text/value, parent/child hierarchy and ordering, visibility, and stable semantic state. Coordinates, dimensions, generated selectors, runtime element IDs, and process-specific identity are ignored. Multiple app windows remain distinct semantic roots.

Both gateways default JSON inspect to `--depth 30` when no depth was specified, and preserve explicit depth arguments in their receipts. Depth alone does not establish completeness: any `hasMoreChildren: true` or active interactive/visibility filter rejects the tree for binding, comparison, and starting-state verification. Increase depth and recapture rather than normalizing missing descendants. An already frozen incomplete source baseline requires a fresh workspace, not manual evidence or contract edits.

The tool aligns supported native UWP `ApplicationFrameWindow`/`CoreWindow` hosts and WinUI desktop content bridges before comparing every application descendant and the outer window's semantic properties. Recognized caption/input hosts and paired `Xaml_WindowedPopupClass` versus `PopupWindowSiteBridge`/`InputSiteWindowClass` shells can be normalized; logical popups, tooltip/menu content, order, and semantic state remain compared. A popup's `isOffscreen: true` never justifies removing its visible descendants. Extra controls, unpaired popups, unknown host shapes, or meaningful wrapper state prevent normalization and require a tool-support handoff. Raw captures and their hashes remain unchanged.

Starting-state checks between captures with the same native host class compare the complete semantic trees without cross-platform host normalization, including native captions and popup descendants. An unchanged popup need not block navigation; changed or missing popup content still rejects the starting state.

Surface attestations, assessments, the comparison index, and the current frontier expose `comparisonDiagnostics`: the difference kind, `repairOwner`, `suggestedAction`, source/target evidence paths and raw node locations, and expected/actual values. Follow the frontier's specific repair or handoff rather than retrying unchanged attest/finalize commands. Date-like text differences suggest checking comparable input/clock conditions, but remain mismatches: they are not automatically ignored or declared dynamic. Unsupported boundaries, including nested provider boundaries, produce `human-validation-required` rather than invented application differences. Independently confirmed application differences still produce `surface-mismatch` alongside the unsupported-region handoff; neither outcome establishes parity.

Between supported native UWP and WinUI hosts, narrowly recognized legacy/modern `NavigationView` provider shapes produce informational `platform-control-normalized` diagnostics. The tool aligns the transparent framework wrapper (including its control ID), standard menu roles, a single unannotated same-name/state generated label peer, the known legacy/modern back label, and an empty unannotated footer. Parity passes only if every remaining menu item and application descendant matches, including AutomationIds, text, hierarchy, order, visibility, and semantic state. Extra interactive items, custom label metadata, and nonempty or changed-state footers remain differences. Unknown or annotated provider shapes retain blocking `platform-control-review` rather than being flattened. This normalization never applies to same-platform starting-state checks or ordinary application trees.

Known inline popup/light-dismiss siblings are retained while the navigation behind them is aligned. Only the standard unannotated WinUI `PopupRoot` dismissal peer accompanying a paired `MenuFlyout` may be treated as generated framework UI. Within a recognized paired ColorPicker flyout, the known English HSV `Brightness`/`Value` resource terms can be aligned. A collapsed standard RGB selected-item peer can be represented as the selected value only when the complete visible standard RGB fields prove that mode on both sides; extra or annotated peers, different selections, and unsupported templates remain blocking. Color descriptions, every HSV/RGB/hex value, application children, order, and state remain compared. These narrowly scoped rules do not remove editable peers or header labels and do not establish pixel-level visual fidelity.

An `isOffscreen` difference produces a capture-condition review: first check equivalent client viewport, DPI, scroll, and overlay state before changing layout. Visibility is still compared, and the difference still prevents parity. `closure finalize` derives an honest partial report from assessed evidence even when verified, stale, unsupported, and missing surfaces coexist; it does not bless stale or tampered proof. `--strict` still fails until all closure gates pass. When only tool-support work remains, nonterminal `tool-blocked` rejects unchanged build/replay retries while allowing diagnostic capture. New target/build inputs or a different comparison build reopen assessment; independent actionable surfaces remain available.

When only capture/input reviews remain, disposition is nonterminal `input-review-required`, with `retryable: false` and `completion.inputReviewSurfaceIds`; this is still `needs-work`, not equivalence or a confirmed application defect. Source diagnostic capture remains available after initial discovery. Supplemental probes cannot replace an already bound immutable baseline or rewrite old target proof. Establish comparable inputs before recording again; if the frozen inputs cannot be reproduced, use a fresh workspace with a comparable baseline. Unchanged build/replay retries are rejected, but diagnostic UI capture and independent actionable work remain available. Neither date-like text nor this review status permits ignoring a semantic difference.

| Evidence | Product outcome |
|---|---|
| No attempt, or previously recorded evidence is no longer valid | `not-run` (pending) |
| Authenticated navigation fails or the surface cannot be reached | `surface-unreachable` |
| Complete source and target trees are comparable but differ | `surface-mismatch` |
| Complete source and target trees match | `surface-parity-verified` |
| Automation cannot produce trustworthy comparable trees | `human-validation-required` |

Only `surface-parity-verified` closes a required surface. A TODO mapped only to `target-reachable` may satisfy that narrower check despite a tree mismatch; app acceptance still requires full parity for every required surface. Human validation is a handoff, not machine success. Screenshots, partial trees, and agent-authored verdicts cannot substitute for complete tree parity.
