# Required state verification contracts (schema 2.0)

The migration report remains schema `1.5`; build/replay/probe envelopes remain
schema `1.0`. New state plans use **schema `2.0`** and the
[structural JSON schema](state-plan-v2.schema.json). Runtime cross-reference,
ordering, timing, provenance and visual checks are authoritative.

## Establish requirements before evidence

Read the source and user requirements, then declare every required action,
postcondition, restoration and semantic visual region. A convenient observable
property is not a substitute for the complete feature contract. The product
does not infer requirements from English or infer visual correctness from a
page name, tree count or navigation success.

Before **any source capture or first workflow assessment**, run:

```powershell
winapp-migrate uwp closure seal-contract "<target>"
winapp-migrate uwp closure assess "<target>"
```

`seal-contract` stores a locally signed snapshot at
`.migration-evidence/enforcement/required-contract.json`. Its immutable
`requiredContractHash` is independently anchored in the named user CNG key,
not inferred from a mutable plan at record time. CNG custom properties associate
the workspace/contract ID with its current required hash and freeze state.
Deleting/replacing/rolling back JSON cannot reset that association.
The original `baselineHash` is retained across extensions.
Verification never creates a missing workspace association: even another
locally signed contract copied from a clean workspace cannot bless captured
legacy evidence or a moved workflow. Establishment is the only association
creation path, and runs its pre-source/workflow checks.

Before source/workflow activity only, the agent may explicitly **append**
requirements using `seal-contract --extend`. Existing state IDs, criticality,
preconditions, human action descriptions, expected outcomes, action definitions,
assertions and step order cannot be removed, weakened or reordered. Source
observations and target results are not requirements and are recorded later.
TODO/slice metadata is not an alternative verification authority; this protocol
does not implement active-slice attribution.

The first assessment (including the assessment performed by source commands)
freezes the independently anchored contract. Mutation after source capture or
workflow start is rejected, including attempts to relabel an old probe with a
new requirement. Build/replay attestations, workflow operations, state probes
and state attestations carry the required hash. A contract established after
the target replay cannot authorize it retroactively.

This is **same-user tamper evidence**, not an OS boundary against arbitrary
malicious code running as the same user and able to call the user's CNG APIs.
Embedded public keys alone do not authorize any contract or result.

## Ordered machine requirements

Each state keeps its `actions` descriptions and `assertions`, and adds:

```json
"verification": {
  "kind": "nonvisual",
  "requiredActions": [
    { "id": "observe", "command": ["inspect"] }
  ],
  "steps": [
    { "kind": "action", "id": "observe" },
    { "kind": "assertion", "id": "startup-name" }
  ],
  "restorations": []
}
```

Action and assertion IDs are unique within their kind. Every human action must
have a machine action definition; every action and assertion must appear exactly
once in the declared ordered steps. `command` is the semantic WinApp UI argv,
without the executable/`ui` prefix, HWND/app/output plumbing or JSON-format flag.
For example `["invoke","Night"]` or
`["get-property","SelectedItem","--property","IsSelected"]`.
The product normalizes native `-w/--window`, `-o/--output`, `-p/--property`
spellings; it does not accept an agent's “action passed” boolean or claimed ID.

Required action witnesses are re-derived from **authenticated probe commands**.
One action probe cannot discharge two actions. Assertions retain their planned
zero-based `probeIndex`; an observation action and predicates on that same
observation may share a probe. Steps must be monotonic, with actions consuming
distinct later probes. Extra development probes do not remove requirements.
The attestation seals `requiredActionIds`, ordered `verifiedSteps` (including
actual probe IDs/indices), assertion IDs, issues and the derived verification
level. Reassessment repeats derivation, not just signature validation.

For a round trip, explicitly declare both transitions and the final comparison:

```json
"verification": {
  "kind": "nonvisual",
  "requiredActions": [
    { "id": "to-alternate", "command": ["invoke","Night"] },
    { "id": "return-initial", "command": ["invoke","Day"] }
  ],
  "steps": [
    { "kind": "assertion", "id": "initial" },
    { "kind": "action", "id": "to-alternate" },
    { "kind": "assertion", "id": "alternate" },
    { "kind": "action", "id": "return-initial" },
    { "kind": "assertion", "id": "restored" }
  ],
  "restorations": [
    {
      "id": "roundtrip",
      "baselineAssertionId": "initial",
      "returnActionId": "return-initial",
      "restoredAssertionId": "restored"
    }
  ]
}
```

The three assertions map to their distinct planned observations (for example
indices `0`, `2`, `4`). Restoration requires the return action between the
baseline and restored observations and compares the restored target value/ROI
to the initial target as well as the source. Proving only the alternate state,
omitting the return action, or omitting the post-return comparison is not parity.
Intentional restoration similarity is valid; blanket duplicate rejection is
not used.

## Three different verification levels

| Product-derived level | Meaning | Can complete TODOs/findings/closure? |
|---|---|---|
| `evidence-valid` | Current, locally authenticated, state/contract/replay/process-bound receipt sequence. Required coverage or target sufficiency remains incomplete. | No |
| `contract-satisfied` | Ordered required coverage and target checks/sufficiency pass, but at least one required source/restoration parity comparison is unverified or differs. | No |
| `parity-verified` | All required coverage, supported predicates, source/target comparisons and restoration comparisons pass. | Yes, for the explicit mapped contract |

`record-state` can persist partial levels with product issues; only
`parity-verified` produces target `passed` and comparison `verified`.
Other levels remain `unverified` and cannot clear mapped required work.
Snapshots expose authenticated, re-derived `stateVerifications`; editing status,
level, requirement IDs or step receipts in JSON does not change the result.
Invalid/unsupported query shapes still fail closed rather than becoming parity.

## Semantic ROI visual contracts

States claiming visual outcomes set `verification.kind` to `visual` and include
at least one `visual-region-equals-source` assertion:

```json
{
  "id": "semantic-image",
  "kind": "visual-region-equals-source",
  "probeIndex": 1,
  "sourceEvidence": ".migration-evidence/source/image.png",
  "region": { "x": 0.25, "y": 0.375, "width": 0.5, "height": 0.5 },
  "comparison": "pixel-exact",
  "maximumMeanError": 0
}
```

Regions are normalized image coordinates, must be nonempty and contained, and
are frozen requirements. `sourceRegion` may explicitly identify a different
source layout; otherwise it uses the contracted target region.
There is **no implicit whole-window fallback**. Contract authors must select the
semantic content (for example an image/card region), not decorative chrome.

Alternatively omit `region` and bind to UIA element bounds:

```json
{
  "id": "semantic-image",
  "kind": "visual-region-equals-source",
  "probeIndex": 1,
  "sourceEvidence": ".migration-evidence/source/image.png",
  "automationId": "ImageRegion",
  "boundsEvidence": ".migration-evidence/source/tree.json",
  "boundsProbeIndex": 0,
  "comparison": "pixel-exact"
}
```

The product requires exactly one element inside exactly one UIA `Window`,
finite `x/y/width/height`, a contained region, and a 1:1 single-window screenshot
size/origin. Unknown crop/composite/DPI transforms are rejected, not guessed.
The target bounds probe is authenticated `inspect` evidence preceding the
screenshot; source bounds must be declared source evidence.

ROI sufficiency is computed **inside that region**: at least 16 pixels on each
axis, area at least 1024 pixels, multiple quantized colors and at least 2%
non-dominant content. An informative header cannot rescue a uniform/blank
semantic image area. UIA products/controls being present does not prove their
image content is visible.

Supported comparisons are `pixel-exact` (same ROI dimensions and composited
RGB pixels) and explicitly contracted `sampled-mae` (64x64 normalized sampling,
mean absolute RGB error bounded by `maximumMeanError`, capped at `0.10`).
Exact comparison is the conservative default; neither metric is a general
human semantic/photographic oracle. Unmodeled layout/appearance differences
remain unverified. Visual restoration also compares baseline/restored target
ROIs.

Only the existing bounded 8-bit noninterlaced decoder supplies visual pixels.
CRC/chunk/zlib-valid but unclassified formats (such as 16-bit/Adam7) may be
supplemental evidence for a nonvisual contract, never visual/information proof.
Low-information or invalid semantic ROIs cannot be “passed” using window-wide
entropy. Invalid/corrupt PNGs remain invalid.

## Explicit legacy migration

Schema `1.0` plans and envelopes still load for diagnosis/provisional work.
They are **not** silently promoted to fully verified mutable contracts.
Before any source capture or workflow assessment, author the explicit machine
requirements and use:

```powershell
winapp-migrate uwp closure seal-contract "<target>" --upgrade-plan
```

This upgrades only an unstarted structured plan to `2.0`. It does not infer
commands from prose, reconstruct missing return actions or bless old evidence.
If source/workflow activity already occurred without an independently
established contract, preserve that historical evidence as provisional and
start a fresh planned/captured workflow. There is no `--trust-legacy`,
`--accept-hash`, or status-edit shortcut.
