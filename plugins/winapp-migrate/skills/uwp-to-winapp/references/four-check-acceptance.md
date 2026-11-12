# Bounded four-check acceptance

Explicit opt-in; existing defaults, strict closure and TODO/report verification
are unchanged:

```powershell
winapp-migrate uwp acceptance inventory "<target>" --profile four-checks
winapp-migrate uwp acceptance assess "<target>" --profile four-checks --strict
```

`--profile` is required. `--strict` only returns nonzero unless these four
checks pass; it never invokes strict finalize. Missing evidence yields partial,
structural differences fail, and malformed artifacts are command errors.

## Reuse the existing workflow

There is **one state plan**, the report's existing schema 2.0 `state-plan`.
Use its immutable requirements, ordered actions, nonvisual
`ui-property-equals-source` assertions, and `source.evidence` /
`target.evidence`. The assessor calls the existing `StateClosureProtocol.Derive`;
it does not implement another state scheduler, assertion language or target
receipt verifier. It never writes state observations or closure attestations.

Establish the smallest scope-appropriate nonvisual state contracts before
source capture, using the existing gateways. Do not remove frozen requirements
or bypass gateway rules. A visual/intermediate assertion or an off-scope
restoration in an existing contract is a scope conflict, not permission to
weaken the contract or start an endless repair loop.

Record ordered canonical `.../probe.json` receipt paths in each state's
`source.evidence` and `target.evidence`, in addition to existing evidence.
Each sequence must end in exactly one whole-window, unfiltered `inspect --json`.
Intermediate inspect observations need separate state comparisons. Source
assertion files must contain the same bytes as that authenticated source
endpoint; they cannot be independently edited baselines. Target assertions
must observe the final endpoint. Select assertions that identify the page/mode,
not a button common to every state. If identity cannot be observed, report
unknown. Unassigned current source inspect observations remain inventory gaps.

## Additional accounting, not another state plan

Run inventory **before source launch**. It evaluates Debug/x64 source items
without building/restoring, follows project references and retains linked
Compile/Page/ApplicationDefinition inputs. Re-evaluation failures are gaps.
The generated `.migration-evidence/acceptance/four-checks-surfaces.json` only holds
the source-input fingerprint, surface mappings, discovered dynamic surfaces
and node-local structure normalizations. State IDs refer to the existing plan:

```json
{
  "schemaVersion": "1.0",
  "profile": "four-checks",
  "sourceInputFingerprint": "<generated>",
  "mappings": [
    {
      "surfaceId": "<generated>",
      "disposition": "mapped",
      "targetFile": "MainPage.xaml",
      "targetNode": "0.1",
      "reason": "The original declaration is retained here.",
      "stateIds": ["main-page"]
    }
  ],
  "structureExceptions": {
    "main-page": [
      {"side": "target", "path": "0.2", "property": "wrapper", "reason": "Desktop host; all children remain compared."}
    ]
  },
  "additionalSurfaces": [
    {"id": "runtime-dialog", "kind": "dialog", "reason": "Discovered in the reviewed dialog factory."}
  ]
}
```

Every static XAML element, lexical navigation/UI-call candidate, code-file
review obligation and declared dynamic surface needs a target mapping or
justified `not-applicable` disposition. Mapped XAML paths must resolve to actual
target nodes. Paths are zero-based element/child indices, not AutomationIds.
Deleting authored rows cannot shrink the re-derived inventory. Review each
code file for dynamic factories, conditional UI, reflection and custom
navigation; declare additional discoveries instead of clearing unresolved work.

## Four checks and limits

| Check | Requirement |
|---|---|
| Startup | Current authenticated successful build/replay and a successful post-launch state endpoint. Proves an observed responsive window, not indefinite stability. |
| Surface accounting | All evaluated static/review/discovered entries explicitly accounted for, plus complete paired mapping of observed source UIA nodes. |
| Reachability | Every applicable Page, Window, dialog, flyout, page-level VisualState and declared major mode mapped to successful existing state contracts. Mutually exclusive VisualStates cannot reuse one observation. Template interaction states are not automatically major modes. |
| Semantic structure | Whole captured trees compared in order: role, semantic parentage, name, enabled/offscreen and available value/selection/expansion/toggle properties. Missing properties/truncated trees are unknown; conclusive differences fail. |

Reasoned normalizations are local to one side/node: `wrapper` promotes children
of a Window/Pane/Group; `host-chrome` omits TitleBar or recognized native
caption/input-sink containers; `name`/`value` excludes only that property.
Application controls cannot be removed as chrome. Framework class names,
generated selectors, AutomationIds and physical geometry are not semantic roles.

Accounting is not universal behavior discovery. C# candidates are lexical,
not a semantic analyzer; other configurations, downloaded UI, virtualization
and unexercised behavior need review. Elements without UIA peers still require
static accounting. Do not use FindName as a passive lazy-creation probe.

Source inputs are re-evaluated/hashed. Changes require explicit map review and
new source captures; target changes invalidate replay. Source runs seal the
evaluated-input fingerprint and probes seal raw output hashes. Old unsigned
observations are not retroactively trusted. Evidence uses the existing local
signing authority: another machine must recapture, not trust imported seals.

The inventory/result are persisted separately under `acceptance`. A pass is
not strict closure or `parityStatus: verified`. Fonts, pixels, colors, exact
geometry, internal lazy/template lifecycle, draft persistence, performance
and full functional parity are excluded unless they directly block one of
the four checks. Stop at passed or an explicit partial/failed handoff.
