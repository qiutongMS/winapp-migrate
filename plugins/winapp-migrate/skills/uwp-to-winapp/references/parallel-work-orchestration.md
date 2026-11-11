# Parallel migration work orchestration

Parallel work is an implementation optimization for schema `1.7` TODOs. It
does not determine migration correctness and is never required for closure.

The mechanical report supplies touch paths, project items, conflict keys, and
acceptance checks. Partition all non-terminal TODOs into implementation groups:

1. Put TODOs that touch the same file, project item, shared abstraction,
   startup path, or conflict key in the same group.
2. Run groups in parallel only when their implementation and acceptance
   surfaces are disjoint.
3. Give each group one owner; do not ask independent workers to edit the same
   canonical surface.
4. Record each TODO as implemented or record its rejected approach. Do not
   require workers to build the entire target.
5. Reconcile all groups into the canonical target before project-level
   stabilization.

Copilot CLI `/fleet` can run independent subagents and `/tasks` can display
their status. These commands do not detect file conflicts, isolate workspaces,
or prove completion. The parent agent retains those responsibilities.

## Delegate bounded work

Give each worker one conflict-free group with:

- stable TODO IDs and origins;
- authoritative facts and source invariants;
- allowed touch paths and known conflicts;
- candidate approaches and rejected approaches;
- forbidden shortcuts and concrete completion checks.

A worker reports changed paths, implemented TODOs, rejected approaches, and
new migration obligations. Its completion claim is not migration evidence.

## Stabilize globally

Do not build after each worker or TODO. After all currently implementable work
is integrated, run the canonical project build and replay. Fix all diagnostics
governed by one root cause before rebuilding.

A build or runtime symptom remains project-level unless evidence proves:

- an existing TODO implementation is wrong, in which case reject that
  approach and reopen the TODO; or
- mechanical analysis omitted app-owned migration work, in which case add a
  discovered TODO.

After a fresh canonical replay, verify planned states and run `finalize` once.
It derives each TODO's verified status from evidence.
