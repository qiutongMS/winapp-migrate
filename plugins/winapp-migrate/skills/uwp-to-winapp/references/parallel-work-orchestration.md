# Parallel migration work orchestration

Use this protocol only for schema `1.6` `todoGraph` work. The product defines
work items, dependencies, touch sets, conflict keys, and execution waves. The
product also owns the signed execution ledger, immutable packet issuance,
isolated workspace creation, handoff validation, serial integration, and wave
completion gates. The Skill may schedule workers against product-issued
workspaces; benchmark code never selects, starts, merges, or retries migration
work.

## Preconditions

Before delegation, run:

```powershell
winapp-migrate uwp closure work next "<target>"
```

This command creates or validates
`.migration-evidence/enforcement/work-execution.json` and returns only the
earliest incomplete wave. Then:

1. require the returned graph and signed ledger to be valid;
2. establish the required state contract, capture every required source-oracle
   state, and complete the post-capture workflow assessment;
3. select only unresolved work items in the returned wave;
4. let `closure work issue` freeze the product target fingerprint and create
   one immutable-base workspace per item.

Do not flatten an invalid graph or infer missing dependencies. Rerun
`winapp-migrate uwp verify` after correcting the product-owned input.

## Issue a complete work packet

For each non-verification item, choose a product-listed strategy with proven
preconditions, or `custom:<description>` only when strategies are explicitly
non-exhaustive:

```powershell
winapp-migrate uwp closure work issue "<target>" `
  --work-item "<stable-id>" `
  --strategy "<strategy-id>"
```

The signed packet contains the parent TODO, stage, dependencies, authoritative
guidance, selected strategy, touch set, completion checks, graph hash, base
fingerprint, and isolated workspace path. Give one worker exactly this packet
and workspace. Do not reconstruct, broaden, or combine packets.

## Isolate concurrent work

`closure work issue` creates one product-owned immutable-base workspace per
work item. Workers may read source evidence and edit that workspace, but they
must not:

- edit the canonical target worktree;
- edit `migration-report.json`, mechanically owned evidence, required
  contracts, workflow progress, or closure attestations;
- invoke canonical build, replay, capture, record-state, or finalize;
- expand into another work item without returning the newly discovered
  dependency or finding to the parent.

The worker reports only claimed completion-check IDs and unresolved findings.
The parent converts the workspace into a signed, touch-set-validated handoff:

```powershell
winapp-migrate uwp closure work submit "<target>" `
  --work-item "<stable-id>" `
  --check "<completion-check-id>"
```

Use repeated `--unresolved "<finding>"` arguments instead of pretending a
blocked candidate is ready. If workers are unavailable, the parent edits each
issued workspace serially and still uses `submit`, `integrate`, and
`complete-wave`; it never edits the canonical target directly.

## Execute waves

- **Preparation:** independent items in one product-generated wave may run
  concurrently.
- **Integration:** candidate patches may be prepared concurrently only when the
  graph places them in the same wave. The parent still integrates them one at a
  time in stable work-item order.
- **Verification:** never delegate or parallelize this stage. The parent owns
  the integrated target and all gateway operations.

Do not start a later wave until `complete-wave` accepts the current wave. A
truthful blocking finding remains nonterminal and prevents later issuance.

## Integrate serially

For each prepared handoff, the parent runs:

```powershell
winapp-migrate uwp closure work integrate "<target>" `
  --work-item "<stable-id>"
```

The command verifies the signed packet and handoff, frozen base, graph hash,
candidate hashes, declared touch paths, current canonical fingerprint, overlap,
and stable item order before applying the candidate transactionally. A normal
disjoint sibling remains valid after an earlier integration; overlap, direct
canonical edits, or graph changes fail closed.

After every coherent non-verification wave:

```powershell
winapp-migrate uwp verify "<target>"
winapp-migrate uwp closure work complete-wave "<target>" --wave <index>
```

Integration resets mechanical verification. `complete-wave` requires a fresh
signed verification record for the current graph and target fingerprint, then
marks the wave's entries verified. A verification wave additionally requires a
fresh signed canonical build and is never issued to a worker.

Only after integration is stable may the parent perform the serialized
canonical sequence: build/replay attestation, state capture, record-state,
report synchronization, and finalization. The closure frontier remains
`work-item` until all non-verification work is verified, and final closure
remains blocked until every execution entry is verified. No worker result can
close a TODO, finding, state, or parity claim.
