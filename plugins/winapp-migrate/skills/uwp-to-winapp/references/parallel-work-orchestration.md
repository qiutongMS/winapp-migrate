# Parallel migration work orchestration

Parallel work is an optional efficiency technique for schema `1.6` TODOs. It
does not determine migration correctness and is never required for closure.

The mechanical report supplies dependencies, touch sets, conflict keys, and
acceptance checks. The parent agent uses that metadata to choose work:

1. List required, non-terminal TODOs whose `dependsOn` TODOs are terminal.
2. Prefer the smallest coherent set that advances observable behavior.
3. Run serially when independence is uncertain.
4. Parallelize only ready items with disjoint touch sets and independent
   acceptance checks.
5. Integrate results into the canonical target and verify before treating a
   TODO as complete.

Copilot CLI `/fleet` can run independent subagents and `/tasks` can display
their status. These commands do not make dependency decisions, detect file
conflicts, isolate workspaces, or prove completion. The parent agent retains
those responsibilities.

## Delegate bounded work

Give each worker one ready TODO with:

- its stable ID and completed dependencies;
- authoritative facts and source invariants;
- allowed touch paths and known conflicts;
- candidate strategies and forbidden shortcuts;
- concrete acceptance checks.

A worker must report changed paths, completed checks, and unresolved findings.
Its completion claim is not migration evidence. The parent reviews overlap,
reconciles conflicts, and runs canonical verification.

## Integrate conservatively

Parallel preparation may be useful for disjoint project, dependency, resource,
or manifest work. Startup/lifetime changes, shared project files, generated
code boundaries, and overlapping XAML/code-behind pairs usually require serial
integration.

After integrating a logical group, run the smallest relevant mechanical or
build check. Use canonical replay and state capture only from the parent.
If a conflict, missing dependency, or new semantic finding appears, return the
affected TODOs to serial work. Falling back to serial execution affects only
speed, not the validity of the migration.
