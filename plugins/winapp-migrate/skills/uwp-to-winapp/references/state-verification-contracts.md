# Required state verification contracts (schema 2.0)

The migration report uses schema `1.7`; build/replay/probe envelopes remain
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
uses authenticated operation intent for state/slice attribution, not an
agent-selected progress label.

Legacy state-plan `todoIds` are ignored compatibility metadata. They are not
part of the immutable action/assertion contract, do not define TODO ownership,
and do not participate in the state contract fingerprint. New plans should
omit them; TODO completion checks own the mapping.

## Audited selector rebinding

A frozen action's locator can become ambiguous even when the required
behavior is unchanged. Do not edit the state-plan command or extend the
contract to disguise that repair. Record a selector-only amendment:

```powershell
winapp-migrate uwp closure rebind-selector "<target>" `
  --state "<state-id>" `
  --action "<required-action-id>" `
  --reason "<why the old locator is no longer unique>" `
  -- invoke "<replacement-selector>" --type "<optional-control-type>"
```

The replacement must keep the same UI verb and all non-locator payload and
property arguments. Only the primary selector and locator refinements such as
`--root`, `--type`, and `--class-name` may change; runtime window, app, output,
and JSON options are forbidden in the amendment. Action ID, step order,
assertions, expected outcome, and `requiredContractHash` remain frozen.

Amendments are cumulative, locally signed, generation-linked, and independently
anchored against rollback. Every new target probe and state attestation carries
the current per-state selector-binding hash. Rebinding clears the state's
target result; all earlier target probes and attestations for that state become
stale and must not be relabeled or reused.

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
For example `["invoke","Option-B"]` or
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
    { "id": "to-alternate", "command": ["invoke","Option-B"] },
    { "id": "return-initial", "command": ["invoke","Option-A"] }
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

## Operation intent and failure attribution

Before executing a build, replay, UI capture or diagnostic, the gateway seals
the intended `stateId` and `intendedSliceId` into its workflow operation.
The operation's existing `sliceId` is the accounting unit, which may be null
during bootstrap; it is not the intended semantic slice. Completion receipts
and recovered failures retain the start tuple even if active progress changes
during the command. Probe receipts also carry their planned `sliceId`.

The assessed active slice and frontier are recommendations, not runtime
authorization. `--state` may identify any state in the sealed plan, including a
state outside the current recommendation or one with explicit debt. The
gateway still binds every operation to that declared state and slice. It cannot
rebind an evidence-linked operation or receipt. A multi-state slice need not
pick an arbitrary state: a slice-only tuple is valid; `(null, null)` is
explicitly unbound. Unknown states/slices, a state without its declared slice,
or mismatched pairs are rejected, not repaired.

Interrupted-ledger recovery first uses the authenticated operation tuple.
Only legacy ID-less evidence may fall back to the persisted
`activeStateId`/`activeSliceId`, and only when that pair is internally
consistent with the sealed plan. Otherwise it remains unbound; plan order
never supplies a recovery state. Existing recovered tuples are preserved.
Legacy claimed nonempty attribution without operation provenance is not
silently promoted into authenticated attribution.

`record-diagnosis` copies the recorded failure tuple exactly. Diagnosis and
change-required frontiers do not acquire a different active tuple. Repeated
signatures are aggregated only within the same state/slice attribution;
recovery deduplicates operation-backed failures by operation identity.
Historical failed replay recovery authenticates the producing operation and
sealed build identity without requiring that build to remain the latest build.
This only preserves negative evidence; successful closure still requires the
complete current canonical build/replay/window chain.

Locally trusted historical parity preserves semantic resolution across target
changes and later unrelated failures. It does not grant evidence freshness,
clear a new mapped finding/failure, or authorize final closure. Stale resolved
states are revalidated in closure; only explicitly mapped failures reopen
semantic work.

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
semantic content regions, not decorative chrome or an application-specific heuristic.

Alternatively omit `region` and bind to UIA element bounds:

```json
{
  "id": "semantic-image",
  "kind": "visual-region-equals-source",
  "probeIndex": 1,
  "sourceEvidence": ".migration-evidence/source/image.png",
  "automationId": "ImageRegion",
  "boundsEvidence": ".migration-evidence/source/tree.json",
  "sourceCaptureEvidence": ".migration-evidence/source/capture.json",
  "boundsProbeIndex": 0,
  "boundsAfterProbeIndex": 2,
  "comparison": "pixel-exact"
}
```

The product requires exactly one element inside one UIA `Window`, finite
`x/y/width/height`, and a contained region. The enclosing `windows[].hwnd`
(and process metadata when present) must match the capture/replay. The
authenticated screenshot's genuine WinApp `coordinates.sourceBounds` and
`contentRect` map physical UIA coordinates to image pixels across window sizes,
DPI and encoder scaling/padding. Root dimensions and owner must match the
actual PNG and sealed probe. Composite/unknown mappings fail closed.
Source bounds and capture metadata must both be declared source evidence.

The before/after bounds probes must be authenticated `inspect` observations,
bracket the screenshot, and use the same window and process incarnation.
The required element's geometry must be unchanged across those observations.
The complete operation history in that bracket must contain only the
screenshot between the two witnesses. An agent cannot hide activity by
omitting receipts from `record-state`: every operation is signed with its
sequence and predecessor digest, and intervals are checked against that full
authenticated history. Deleting/reordering intermediate records breaks the
chain. Legacy unordered histories remain provisional and need a fresh planned
workflow; they are not silently upgraded to ordered provenance.
The latest committed sequence/digest is independently anchored in local CNG
state. Anchor values use independent named CNG state keys so persistence for
one workspace cannot overwrite another workspace's property bag; pre-existing
trusted anchor properties remain readable for compatibility.
The runner checks the head before authorizing/signing a successor and advances
it only on authenticated operation creation/finalization, never merely by
assessing a supplied JSON snapshot. Restoring a once-valid prefix between
ordinary gateway calls cannot hide intervening activity or create a new fork.
Changed window origin/size between inspect and capture fails the geometry
comparison. Explicit normalized regions apply directly to the current
authenticated image and work with legacy captures lacking coordinate metadata;
they never reuse cached physical bounds.

ROI sufficiency is generic contract data (`sufficiency`): minimum distinct
quantized colors, non-dominant content fraction and sample count. Defaults are
2 colors, 0.02 content fraction and 64 sample pixels; validation requires a
positive fraction no greater than 0.5, 2..4096 colors and 4..16777216 samples.
There is no application-specific minimum rectangle. Only pixels whose
centers fall inside the declared ROI count, so adjacent chrome cannot leak
into a fractional edge. Normalized pixel-center sampling preserves the
contract's region across resolution/DPI changes. Multiple visual assertions
cover independent semantic regions; every required region must pass.

Supported comparisons are `pixel-exact` (same ROI dimensions and composited
RGB pixels) and explicitly contracted `sampled-mae` (64x64 normalized sampling,
mean absolute RGB error bounded by `maximumMeanError`, capped at `0.10`).
Exact comparison requires matching pixel dimensions; sampled comparison
supports different image sizes with explicitly contracted tolerance.
Neither metric is a general
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

## Application/benchmark independence

Required contracts are generic input data, never benchmark adapters or a
catalog of application selectors. Reproduction fixtures do not drive product
execution or define thresholds. Synthetic grid, reversible-toggle and
structured-only fixtures exercise different data shapes and require no
application-specific IDs. Active-slice attribution is a separate dependent
feature and is not implemented here.
