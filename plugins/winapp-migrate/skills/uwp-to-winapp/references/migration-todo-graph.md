# Migration TODO guidance and graph

Schema `1.6` keeps deterministic migration planning in Tier 1. Every TODO has:

- authoritative detection `facts`, separate from a confirmed, ambiguous, or
  provisional `classification`;
- non-exhaustive candidate strategies with preconditions and risks;
- forbidden shortcuts, prose completion conditions, and typed
  `completionChecks`;
- affected states/features, expected touch paths/project items, and conflict
  keys;
- stable canonical IDs plus optional `workSlices`.

Each work slice has a `preparation`, `integration`, or `verification` stage.
Its dependencies refer to stable work-item IDs. `todoGraph.edges` records those
prerequisite relationships, while `todoGraph.workItems` retains the parent TODO
and effective touch set.

The planner suggests deterministic execution waves by grouping ready items
whose paths, project items, and conflict keys do not overlap. These waves are
advisory. The agent may select any incomplete item whose dependencies are
complete, may combine compatible serial work, and may fall back to serial
execution whenever independence is unclear.

An unknown dependency, duplicate work-item ID, self-edge, cycle, invalid stage,
or unschedulable conflict makes `todoGraph.status` `invalid` and records a typed
issue. Treat that as a mechanical planning defect: do not infer missing
dependencies or claim the graph valid. The migration may still proceed
serially from canonical TODO facts while the planning limitation is reported.

Candidate strategies are not an allowlist. A different strategy is valid when
it preserves the source invariant, satisfies every typed completion check, and
violates no forbidden shortcut. Never edit detection facts merely to make a
strategy appear applicable.

The graph coordinates work; closure evaluates results. No TODO, wave, worker,
or agent status proves build, replay, state parity, or final completion.
