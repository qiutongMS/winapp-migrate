# UI Surface Parity

Use this guide to prepare `.migration-evidence\state-plan.json` and interpret surface results. The [schema 3.0 definition](surface-plan-v3.schema.json) specifies the input structure; the tool validates evidence and derives acceptance.

## Plan inputs

Account for each source definition exactly once with kind `window`, `page`, `dialog`, `flyout`, or `user-control`. A `required` surface needs a source tree and target navigation ending in a complete tree inspection. An `excluded` surface needs a concrete reason, `navigation: null`, and `excluded` source, target, and comparison statuses.

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

For a surface that requires navigation, supply the exact semantic WinApp UI argv in `navigation.requiredActions`. Each action needs a stable ID and unique zero-based `probeIndex`; `treeProbeIndex` must be later than all navigation probes:

```json
{
  "requiredActions": [
    {
      "id": "open-settings",
      "probeIndex": 0,
      "command": ["invoke", "Settings"]
    }
  ],
  "treeProbeIndex": 1
}
```

Run `closure seal-contract` before source capture or the first `closure assess`. The tool scans source XAML roots and rejects missing, extra, duplicate, or kind-mismatched definitions. `seal-contract --extend` can only append surfaces before source capture and workflow assessment; it cannot change established entries.

Use source-run-authorized HWNDs for `source capture` and the current replay-owned HWND for target `closure capture --surface`. Submit the navigation probes in their declared order and finish with one complete `inspect --json`; that final tree is compared with the source tree. Use an unbound `closure capture "<target>" -- list-windows --json` if target window discovery is needed.

## Changes and retries

After target changes, run `closure attest` and recapture target surfaces. Use `closure attest "<target>" --force-replay` to relaunch an unchanged successfully built target when needed; a new replay requires fresh target probes.

Do not edit frozen navigation commands. If a locator becomes ambiguous while its action and payload remain correct, use:

```powershell
winapp-migrate uwp closure rebind-selector "<target>" --surface "<surface-id>" --action "<action-id>" --reason "<reason>" -- <replacement-ui-command>
```

Rebinding changes only the locator and requires new target probes for that surface. `record-surface` can reuse an unchanged, still-valid probe sequence. Changed trees, receipt contents, or process/window ownership require reassessment and fresh affected evidence, not manual verdict edits.

## Tree comparison and results

The comparator walks the entire semantic UIA tree, comparing node type, AutomationId, name/text/value, parent/child hierarchy and ordering, visibility, and stable semantic state. Coordinates, dimensions, generated selectors, runtime element IDs, and process-specific identity are ignored. Multiple app windows remain distinct semantic roots.

| Evidence | Product outcome |
|---|---|
| No attempt, or previously recorded evidence is no longer valid | `not-run` (pending) |
| Authenticated navigation fails or the surface cannot be reached | `surface-unreachable` |
| Complete source and target trees are comparable but differ | `surface-mismatch` |
| Complete source and target trees match | `surface-parity-verified` |
| Automation cannot produce trustworthy comparable trees | `human-validation-required` |

Only `surface-parity-verified` closes a required surface. A TODO mapped only to `target-reachable` may satisfy that narrower check despite a tree mismatch; app acceptance still requires full parity for every required surface. Human validation is a handoff, not machine success. Screenshots, partial trees, and agent-authored verdicts cannot substitute for complete tree parity.
