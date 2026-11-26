# UI Surface Reachability and UIA diagnostics

Use this guide to prepare `.migration-evidence\state-plan.json` and interpret surface results. The [schema 3.0 definition](surface-plan-v3.schema.json) specifies the input structure; the tool validates evidence and derives acceptance.

## Plan inputs

Account for each source definition exactly once with kind `window`, `page`, `dialog`, `flyout`, or `user-control`. A `required` surface needs authenticated target navigation ending in a supported target inspection; a source tree is optional diagnostic evidence. An `excluded` surface needs a concrete reason, `navigation: null`, and `excluded` source, target, and comparison statuses.

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

Seal the inventory with `closure seal-contract` before source discovery or target operations. Required-contract schema `3.0` freezes the obligations, not draft navigation recipes. Navigation may remain null during discovery, but declare the actual route from source understanding before target capture. Source observation and binding are optional. Existing required-contract schema `2.0` retains its old immutable navigation and needs a fresh workspace for this lifecycle.

Identify the route's starting surface with `startSurfaceId`; omit it only for a fresh launch before any state-changing action. Probe 0 is a current target inspect. Actions occupy consecutive indices 1..N, followed by a final inspect at N+1. A zero-action surface uses only inspect at index 0:

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

For optional source comparison, capture complete source trees and run `closure bind-source "<target>" --surface "<surface-id>" --probe "<initial-probe.json>" --probe "<action-probe.json>" --probe "<final-probe.json>"`. It authenticates receipts, unchanged source, launch/window identity, ordering, complete initial/final trees, and action history, then freezes that route and baseline. A warm startup, omitted failed action, modified evidence, or incorrect start is rejected. An existing binding cannot be bypassed by deleting it. Do not copy receipts or trust anchors from another workspace.

The inventory check still rejects missing, extra, duplicate, or kind-mismatched definitions. `seal-contract --extend` can only append surfaces before source/workflow activity; it cannot change established entries.

Use source-run-authorized HWNDs for optional `source capture` and the current replay-owned HWND for target `closure capture --surface`. Submit all navigation probes in their declared order and finish with `inspect --json`. A supported partial target observation can establish reachability alongside the actual authenticated route, but never full tree parity. If a complete authenticated source baseline is available, the tool also compares the complete trees. Use an unbound `closure capture "<target>" -- list-windows --json` for window discovery.

Prefer `closure navigate "<target>" --surface "<surface-id>" --window <target-hwnd>`. It verifies the current starting state against the recorded target observation of `startSurfaceId`, executes actions sequentially, and stops at the first failure. Successful execution records reachability even if comparison is absent, unsupported, or mismatched. This same-platform starting-state check is route authentication, not UWP/WinUI tree equivalence. Committed proofs retain the declared route/source snapshot for dependent routes without mandatory source capture. Failed or interleaved actions cannot be omitted.

## Changes and retries

After target changes, run `closure attest` and recapture target surfaces. Use `closure attest "<target>" --force-replay` to relaunch an unchanged successfully built target when needed. Committed surface-proof schema `3.0` preserves observations across replays of the same canonical build, source/navigation contract, selector generation, declared inputs, and comparison-rule identity. A new build, changed inputs or evidence, or changed parsing/normalization/comparison rules invalidates incompatible proof; unrelated validator assembly changes do not. Legacy `2.0` proofs remain replay-bound; they are not silently upgraded.

Do not edit frozen navigation commands. If a locator becomes ambiguous while its action and payload remain correct, use:

```powershell
winapp-migrate uwp closure rebind-selector "<target>" --surface "<surface-id>" --action "<action-id>" --reason "<reason>" -- <replacement-ui-command>
```

Rebinding changes only the locator and requires new target probes for that surface. `record-surface` can reuse an unchanged, still-valid probe sequence. Capture and recording check live window ownership. A committed proof retains collection-time identity after the window closes; new capture and navigation still require an authenticated live replay and current starting-state inspect. Changed evidence or receipt contents invalidate affected proof; never edit verdicts manually.

## Tree comparison and results

The comparator walks the entire semantic UIA tree, comparing node type, AutomationId, name/text/value, parent/child hierarchy and ordering, visibility, and stable semantic state. Coordinates, dimensions, generated selectors, runtime element IDs, and process-specific identity are ignored. Multiple app windows remain distinct semantic roots.

Both gateways default JSON inspect to `--depth 30` and preserve explicit depth arguments. Depth alone does not establish completeness: `hasMoreChildren: true` or active filters prevent source binding and full diagnostic parity, but do not invalidate a supported target reachability observation. Increase depth only when full comparison is useful, not to satisfy an extra machine gate. Never normalize away missing descendants or edit a frozen baseline.

The tool aligns supported native UWP `ApplicationFrameWindow`/`CoreWindow` hosts and WinUI desktop content bridges before comparing every application descendant and the outer window's semantic properties. Recognized caption/input hosts and paired `Xaml_WindowedPopupClass` versus `PopupWindowSiteBridge`/`InputSiteWindowClass` shells can be normalized; logical popups, tooltip/menu content, order, and semantic state remain compared. A popup's `isOffscreen: true` never justifies removing its visible descendants. Extra controls, unpaired popups, unknown host shapes, or meaningful wrapper state prevent normalization and require a tool-support handoff. Raw captures and their hashes remain unchanged.

Starting-state checks compare the recorded/current target observations without cross-platform host normalization, including observed native captions and popup descendants. An unchanged popup need not block navigation; changed or missing observed content still rejects the declared starting state.

Surface attestations, assessments, and the comparison index retain `comparisonDiagnostics`: kind, `repairOwner`, suggested review, evidence paths, raw locations, and expected/actual values. They do not create a machine repair frontier when target reachability is proved. Date-like text remains a mismatch, not an ignored dynamic value. Unsupported boundaries produce `human-validation-required`; independently observed tree differences retain `surface-mismatch`. These are diagnostic outcomes, not proof of an application defect or a reason to continue until framework trees match.

Between supported native UWP and WinUI hosts, narrowly recognized legacy/modern `NavigationView` provider shapes produce informational `platform-control-normalized` diagnostics. The tool aligns the transparent framework wrapper (including its control ID), standard menu roles, a single unannotated same-name/state generated label peer, the known legacy/modern back label, and an empty unannotated footer. Parity passes only if every remaining menu item and application descendant matches, including AutomationIds, text, hierarchy, order, visibility, and semantic state. Extra interactive items, custom label metadata, and nonempty or changed-state footers remain differences. Unknown or annotated provider shapes retain blocking `platform-control-review` rather than being flattened. This normalization never applies to same-platform starting-state checks or ordinary application trees.

Known inline popup/light-dismiss siblings are retained while the navigation behind them is aligned. Only the standard unannotated WinUI `PopupRoot` dismissal peer accompanying a paired `MenuFlyout` may be treated as generated framework UI. Within a recognized paired ColorPicker flyout, the known English HSV `Brightness`/`Value` resource terms can be aligned. A collapsed standard RGB selected-item peer can be represented as the selected value only when the complete visible standard RGB fields prove that mode on both sides; extra or annotated peers, different selections, and unsupported templates remain blocking. Color descriptions, every HSV/RGB/hex value, application children, order, and state remain compared. These narrowly scoped rules do not remove editable peers or header labels and do not establish pixel-level visual fidelity.

An `isOffscreen` difference retains a capture-condition review and prevents diagnostic parity, not reachability closure. `closure finalize` reports verified, stale, unsupported, and missing evidence honestly; it never blesses stale or tampered proof. `--strict` requires current project/launch, inventory, and target reachability gates, not matching trees.

When the three machine conditions pass, disposition is terminal `complete` even if UIA review remains; those diagnostics are preserved as human handoffs, never relabeled as verified. Missing or stale required target proof, failed navigation, and unavailable trustworthy target observations still leave machine work incomplete. Supplemental source probes cannot replace a bound immutable baseline or rewrite old target proof.

| Evidence | Product outcome |
|---|---|
| No attempt, or previously recorded evidence is no longer valid | `not-run` (pending) |
| Authenticated navigation fails or the surface cannot be reached | `surface-unreachable` |
| Complete source and target trees are comparable but differ | `surface-mismatch` |
| Complete source and target trees match | `surface-parity-verified` |
| Automation cannot produce trustworthy comparable trees | `human-validation-required` |

`surface-reachable` or `surface-parity-verified` closes the target reachability condition when its authenticated proof is current. `result`, comparison status, and UIA handoffs remain separate and honest. Existing TODO parity assertions become diagnostic handoffs rather than repair/retry gates. Neither machine completion nor a human handoff claims full business/visual equivalence. Screenshots, partial trees, and agent-authored verdicts never prove full diagnostic parity.
