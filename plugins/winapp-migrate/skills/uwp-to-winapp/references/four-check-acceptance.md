# Bounded four-check acceptance

This is an **explicit opt-in profile**, not a weaker interpretation of strict
closure. It has its own versioned plan, inventory and result under
`.migration-evidence/acceptance`. Existing migration-report schema 1.7,
state-plan 2.0, TODO states and source/closure gateway rules are unchanged.
Neither command edits the report, TODO receipts or strict state plan.

```powershell
winapp-migrate uwp acceptance inventory "<target>" --profile four-checks
winapp-migrate uwp acceptance assess "<target>" --profile four-checks
winapp-migrate uwp acceptance assess "<target>" --profile four-checks --strict
```

`--profile` is required. Here `--strict` only makes the assessment exit nonzero
unless all **four checks** pass. It does not invoke strict migration closure.
Malformed artifacts are command errors; insufficient evidence yields a
persisted partial result with unknown checks, not a fabricated success.

## What is actually checked

| Check | Evidence and stopping condition |
|---|---|
| Startup | Current authenticated successful build/replay plus a successful post-launch whole-window inspect from that replay. This proves an observed responsive window, not indefinite stability or crash freedom. |
| Surface accounting | Re-evaluate the exact report-recorded source project and referenced projects with Visual Studio MSBuild. Include linked Compile/Page/ApplicationDefinition inputs, every static XAML element, navigation/UI-call candidates, per-code-file review obligations, explicitly discovered dynamic surfaces and observed source UIA nodes. Every static/discovered entry needs an explicit target mapping or justified not-applicable disposition. Observed UIA nodes are mapped by a complete successful paired structure comparison. |
| Reachability | Every applicable Page, Window, dialog, flyout, page-level VisualState and declared major mode needs mapped states. Every mapped state needs successful ordered source/target gateway probes and an observable endpoint predicate satisfied on both sides. Mutually exclusive VisualStates cannot reuse one state observation. |
| Semantic structure | Compare the complete captured application trees in order: control role, semantic parentage, name, enabled/offscreen state and available value/selection/expansion/toggle properties. No screenshot is necessary. A missing required property, truncated tree, missing source observation or unproved endpoint is unknown. A conclusive structural difference fails. |

Accounting is **not** automatic proof of all application behavior. The lexical
C# candidates deliberately over-approximate `typeof`, navigation and common
UI-show calls; they are not a C# semantic analyzer. Review every code-file
entry for dynamic factories, conditional compilation, custom navigation,
runtime dialogs and major modes. Add discoveries to `additionalSurfaces`.
Do not mark unresolved code review not-applicable merely to clear the list.
The inventory denominator is re-derived during assessment; deleting a row or
omitting a state from the authored plan cannot remove its static obligation.

The evaluated configuration is Debug/x64. Other configurations, dynamically
downloaded UI, reflection-created content, UIA virtualization and surfaces
not exercised by the recorded exploration are not automatically discovered.
Explicitly review and account for these limits; never describe this result
as universal feature coverage. A framework element lacking a UIA peer is
still a static accounting obligation, not automatic not-applicable evidence.

## Workflow

1. Mechanically migrate once to the explicitly selected target. Run inventory
   **before the source baseline**. It evaluates items without a build/restore,
   follows project references and retains linked files outside the sample
   directory. Failed evaluation or unreadable inputs remain discovery issues.
2. Read `four-checks-inventory.json`. In `four-checks-plan.json`, resolve each
   mapping and review code candidates; add dynamic windows/dialogs/modes.
   Do not copy external source inputs into the sample to hide link failures.
3. Use the existing source/closure gateways with the smallest valid nonvisual
   state contract. In four-check workflows the source run additionally seals
   the evaluated source-input fingerprint; new source probes seal their raw
   output hashes. Old unsigned source evidence is not retroactively trusted.
4. Record finite navigation/action sequences and one final **whole-window**
   `inspect --json` per observed state, on source and target. Use actual gateway
   receipt paths, not copied raw trees, screenshots or authored success JSON.
   Each sequence must be successful, ordered, from one run, and end in inspect.
   Intermediate inspect endpoints need separate state comparisons. Unassigned
   successful inspect observations from the current input-bound source run
   remain inventory gaps.
5. Add those sequences, endpoint predicates and justified structure
   normalizations to the acceptance plan. Run inventory again to inspect the
   runtime surface list. It adds pending static/discovered mappings without
   accepting them or deleting existing decisions.
6. Assess, repair only failures of the four checks, and stop at passed or an
   explicit partial/failed handoff. The result lists all four denominators,
   state outcomes, unknowns and differences. Do not call strict finalize to
   try to turn this result into a different acceptance claim.

Source inputs are re-evaluated and hashed on every assessment. A source change
requires explicit inventory/plan review and a new source baseline. A target
change invalidates old target replay evidence. An inventory refresh never
silently updates an existing plan's source fingerprint.

## Plan shape

The generated plan uses schema `1.0`, profile `four-checks`. Example entries:

```json
{
  "schemaVersion": "1.0",
  "profile": "four-checks",
  "sourceInputFingerprint": "<generated input fingerprint>",
  "mappings": [
    {
      "surfaceId": "<generated static surface ID>",
      "disposition": "mapped",
      "targetFile": "MainPage.xaml",
      "targetNode": "0.1",
      "reason": "The original page declaration is retained here.",
      "stateIds": ["main-page"]
    }
  ],
  "states": [
    {
      "id": "main-page",
      "sourceProbes": [".migration-evidence/source/gateway/probes/<id>/probe.json"],
      "targetProbes": [".migration-evidence/enforcement/probes/<id>/probe.json"],
      "predicates": [
        {
          "sourceNode": "0.1",
          "targetNode": "0.2.1",
          "property": "name",
          "expected": "Main page"
        }
      ],
      "structureExceptions": [
        {
          "side": "target",
          "path": "0.2",
          "property": "wrapper",
          "reason": "Desktop host container; all children are still compared."
        }
      ]
    }
  ],
  "additionalSurfaces": [
    {"id": "runtime-dialog", "kind": "dialog", "reason": "Discovered in the reviewed dialog factory."}
  ]
}
```

XAML `targetNode` and runtime paths are zero-based element/child index paths;
they are not AutomationIds. A mapped static XAML entry must resolve to an
existing target XAML node. `not-applicable` requires a concrete rationale.
Runtime entries are accounted through successful full-tree comparison, not
handwritten `passed` fields. Missing, stale, duplicate and unknown mapping IDs
are not silently accepted.

Endpoint predicates support `name`, `type`, `isEnabled` and `isOffscreen`.
Choose a predicate that actually identifies the page/mode, not a button
common to every state. If mode identity is not observable, retain unknown;
do not infer object creation from absent UIA nodes or call `FindName` to
passively inspect a lazy element.

## Normalization and excluded work

Structural exceptions are local to one side and one node and require a reason:

- `wrapper`: promote children of a Window/Pane/Group without dropping them.
- `host-chrome`: omit a TitleBar or the known native caption/input-sink
  containers (`ReunionWindowingCaptionControls`, `InputNonClientPointerSource`).
  Arbitrary application controls cannot be removed as host chrome.
- `name` / `value`: exclude that single property, for example a launch-time
  timestamp. Enabled/visibility state, role and hierarchy remain compared.

Framework class names, generated selectors, physical coordinates and
AutomationIds are not compared as semantic roles. Extra application nodes,
missing nodes, changed semantic parentage and reordered content are not
automatically normalized away.

Fonts, pixel similarity, colors, exact default geometry, internal lazy/template
instantiation, lifecycle counters, draft persistence, performance and complete
functional parity are **not four-check completion gates**. They may be recorded
as separate follow-up work. Fix them here only when a concrete defect blocks
startup, accounting, major-state reachability or observable structure.

Evidence uses the existing local signing authority. Assess on the capturing
machine; a different machine can audit the code and artifacts but must
recapture locally rather than trust imported signatures as local evidence.
