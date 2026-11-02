# Parallel migration work orchestration

Use this protocol only for schema `1.6` `todoGraph` work. The product defines
work items, dependencies, touch sets, conflict keys, and execution waves. The
Skill may execute independent patch preparation concurrently; benchmark code
never selects, starts, merges, or retries migration work.

## Preconditions

Before delegation:

1. require `todoGraph.status` to be `valid` with no issues;
2. establish the required state contract and complete the initial workflow
   assessment;
3. select only unresolved work items in the earliest dependency-ready wave;
4. freeze one wave base revision (a commit when available) and product target
   fingerprint used by every worker in that wave.

Do not flatten an invalid graph or infer missing dependencies. Rerun
`winapp-migrate uwp verify` after correcting the product-owned input.

## Build a complete work packet

Give each worker exactly one stable work-item ID and include:

- parent TODO ID, stage, dependencies, affected states/features;
- authoritative facts and mechanically applied transforms;
- current classification and its confidence;
- the selected strategy with proven preconditions, risks, forbidden shortcuts,
  and completion checks;
- effective touch paths, project items, and conflict keys;
- wave base revision/fingerprint and the expected handoff format.

Candidate strategies remain non-exhaustive. A worker may choose another
strategy only when its handoff explains how it preserves the source invariant,
respects every forbidden shortcut, and satisfies the completion checks.

## Isolate concurrent work

Create one isolated worktree or equivalent immutable-base workspace per
concurrent work item. Workers may read source evidence and prepare target
patches, but they must not:

- edit the canonical target worktree;
- edit `migration-report.json`, mechanically owned evidence, required
  contracts, workflow progress, or closure attestations;
- invoke canonical build, replay, capture, record-state, or finalize;
- expand into another work item without returning the newly discovered
  dependency or finding to the parent.

Each worker returns its work-item ID, base commit/fingerprint, commit or patch,
changed paths, chosen strategy, local static checks, unresolved findings, and
the completion checks it believes the patch enables. A worker result is a
candidate patch, never completion evidence.

If isolated workers are unavailable, execute the same work items serially in
graph order. Never simulate parallelism by allowing multiple workers to edit
one target directory.

## Execute waves

- **Preparation:** independent items in one product-generated wave may run
  concurrently.
- **Integration:** candidate patches may be prepared concurrently only when the
  graph places them in the same wave. The parent still integrates them one at a
  time in stable work-item order.
- **Verification:** never delegate or parallelize this stage. The parent owns
  the integrated target and all gateway operations.

Do not start a later wave until every required dependency in the current wave
has either integrated successfully or produced a truthful blocking finding.

## Integrate serially

For each candidate patch, the parent must:

1. confirm its base matches the frozen wave base, the current integration head
   descends from that base, and its work-item ID still matches the graph;
2. reject undeclared path/project-item/conflict-key expansion until the graph
   is replanned;
3. integrate one patch, resolve no semantic conflict by guesswork, and inspect
   the resulting diff;
4. retain the parent TODO/finding linkage and completion checks;
5. invalidate remaining candidates only when an earlier integration overlaps
   their declared paths/project items/conflict keys or changes their graph
   contract; a normal disjoint sibling merge does not invalidate the wave base.

After a coherent integration wave, run mechanical `verify` when mechanically
governed files changed, then reload the report. If work-item IDs, dependencies,
touch sets, conflict keys, or waves changed, discard stale packets and schedule
from the refreshed graph.

Only after integration is stable may the parent perform the serialized
canonical sequence: build/replay attestation, state capture, record-state,
report synchronization, and finalization. No parallel patch result can close a
TODO, finding, state, or parity claim.
