# Migration TODO execution contract

Schema `1.7` keeps deterministic migration facts in Tier 1. Every TODO has:

- a stable ID, priority, status, and `origin`;
- authoritative detection facts, separate from a confirmed, ambiguous, or
  provisional classification;
- non-exhaustive candidate approaches with preconditions and risks;
- forbidden shortcuts, prose completion conditions, and typed completion
  checks;
- affected states/features and expected touch paths, project items, and
  conflict keys.

`origin` is `mechanical` for predicted work. A migration obligation first
proven during implementation, build, runtime, or behavior verification is
added through `todo discover` with `origin=discovered`, a stable signature,
and evidence. A compiler diagnostic or runtime symptom is not automatically a
TODO: add one only when the evidence proves omitted app-owned migration work.

TODOs do not contain execution dependencies. Build coupling does not establish
an implementation order. Partition implementation by `touchSet`: TODOs with
disjoint paths, project items, conflict keys, and acceptance conditions may be
implemented in parallel; overlapping work is integrated by one owner.

`winapp-migrate uwp todo` stores a non-authoritative progress receipt at
`.migration-evidence/enforcement/todo-progress.json`. It records the current
approach, accepted implementation, rejected approaches, development-state and
canonical-state verification milestones. It does not schedule work
or authorize closure. Deleting it loses resume and negative-attempt memory,
not migration truth.

Start or resume a TODO with:

```powershell
winapp-migrate uwp todo assess "<target>" "<todo-id>"
winapp-migrate uwp todo select-approach "<target>" "<todo-id>" "<approach-key>" --reason "<rationale>"
```

Record implementation without requiring a build:

```powershell
winapp-migrate uwp todo record "<target>" "<todo-id>" --outcome implemented --summary "<change>" --evidence "<path>"
```

After integration, record exploratory state results from product-generated
state-bound probes:

```powershell
winapp-migrate uwp todo record-development "<target>" --state "<state-id>" `
  --summary "<attempt>" --evidence "<probe.json>"
```

Use `winapp-migrate uwp todo status "<target>"` as the project progress view.
It combines the authoritative closure workflow stage, budget, last
observation, frontier, and disposition with TODO/state counts, the next
incremental state frontier, current action, latest TODO failure signature,
and next checkpoint.

When a semantic TODO has no mechanically knowable proof state, `todo assess`
returns `bind-state`. Use `todo bind-state` to name one exact state and one or
more existing assertion IDs;
never map the TODO to all states merely to make it visible to finalization.

TODO progress is intentionally multi-stage:

- `implemented`: the selected code approach has an accepted implementation
  receipt;
- `development-validated`: every exploratory state assigned by the TODO's
  completion checks passes for the current target fingerprint;
- `verified`: current canonical state evidence and global finalize prove the
  TODO completion contract.

The product derives development outcomes from trusted current probes. Exact
assertion parity produces `development-validated`; a conclusive mismatch
produces `blocked`; unsupported, ambiguous, unavailable, safety-denied, or
otherwise inconclusive automation produces `human-validation-required` plus a
structured human validation item. Human output never authorizes strict
closure. Target changes matching the TODO touch
set or an upstream state owner's touch set produce `stale`, while unrelated
files retain the passed result. Neither discards the implementation receipt or
claims canonical closure.

State-plan `todoIds` are read-only compatibility metadata. They do not define
ownership, participate in state contract fingerprints, or change completion.
The product derives ownership from exact `completionChecks`
(`state-assertion`, with `state-set` and `affectedStates` retained only as
legacy report compatibility).

Canonical state recording is TODO-gated: every required TODO must be
`development-validated` for its current scope. Existing closure failures and
semantic findings remain independently authoritative. Once ready, use one
complete ordered canonical pass followed by one finalize operation.

When an approach fails, preserve it as negative knowledge:

```powershell
winapp-migrate uwp todo record "<target>" "<todo-id>" --outcome failed --summary "<failure>" --failure-signature "<signature>" --retry-condition "<changed-condition>" --evidence "<path>"
```

The exact rejected approach cannot be selected again without
`--retry-evidence`. Unrelated target changes never invalidate an implementation
receipt. Global target fingerprints still control canonical build, replay, and
state evidence freshness.

After all currently implementable TODOs are recorded, use one project-level
`closure attest`. Record build/runtime signatures and diagnoses through the
existing closure failure protocol, and record unresolved behavioral root
causes in the semantic finding ledger. Neither creates TODO dependencies;
unchanged signatures must not cause blind repeated builds or launches.

If stabilization or behavior evidence reveals an omitted migration obligation,
add a discovered TODO:

```powershell
winapp-migrate uwp todo discover "<target>" "<stable-id>" --category "<category>" --priority required --summary "<work>" --reason "<reason>" --phase "<implementation|build|runtime|behavior>" --signature "<signature>" --evidence "<path>"
```

Map every required TODO to all states needed to prove its completion
conditions. `finalize` runs once and derives each TODO status independently
from current canonical build, replay, and state evidence. Agent, worker, or
implementation receipts cannot mark a TODO verified.
