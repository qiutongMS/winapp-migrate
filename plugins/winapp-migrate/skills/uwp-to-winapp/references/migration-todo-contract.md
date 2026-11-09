# Migration TODO execution contract

Schema `1.6` keeps deterministic migration facts in Tier 1. Every TODO has:

- a stable ID, priority, and status;
- authoritative detection `facts`, separate from a confirmed, ambiguous, or
  provisional `classification`;
- non-exhaustive candidate strategies with preconditions and risks;
- forbidden shortcuts, prose completion conditions, and typed
  `completionChecks`;
- affected states/features and expected touch paths/project items/conflict
  keys;
- zero or more TODO IDs in `dependsOn`.

The TODO is the canonical unit of migration work. There is no separate work-item
graph or product-owned execution schedule. A TODO is ready when it is
non-terminal and every TODO named by `dependsOn` is terminal. The agent may
parallelize ready TODOs only when their paths, project items, conflict keys, and
acceptance checks are independent. Otherwise it runs them serially.

A TODO status is terminal when it is `verified`, `accepted`, `completed`,
`resolved`, or `done`. Required TODOs remain closure debt until canonical state
evidence moves them to a terminal status.

An unknown dependency, duplicate TODO ID, self-dependency, cycle, invalid
classification, or malformed TODO contract fails report loading. Do not infer a
missing dependency or edit mechanically owned facts to make a report load.

Candidate strategies are not an allowlist. A different strategy is valid when
it preserves the source invariant, satisfies every typed completion check, and
violates no forbidden shortcut.

TODO dependencies coordinate implementation; closure evaluates results. Map
each required TODO to every state needed to prove its completion conditions.
Only canonical build, replay, and state evidence can move that TODO to a
terminal verified status. Agent or worker completion claims are not migration
evidence.
