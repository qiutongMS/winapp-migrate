# Migration TODO guidance and graph

Schema `1.6` keeps deterministic migration planning in Tier 1. Every TODO has:

- authoritative detection `facts`, separate from a confirmed, ambiguous, or
  provisional `classification`;
- non-exhaustive candidate strategies with preconditions and risks;
- forbidden shortcuts, prose completion conditions, and typed
  `completionChecks`;
- affected states/features, expected touch paths/project items, and conflict
  keys;
- stable canonical IDs plus schedulable `workSlices`.

Every work slice has a `preparation`, `integration`, or `verification` stage.
Its typed dependencies refer to stable work-item IDs. `todoGraph.edges` records
those prerequisite-to-dependent relationships, while `todoGraph.workItems`
retains the canonical parent TODO ID and effective touch set.

The planner packs ready work items into deterministic waves only when their
touch paths, project items, and conflict keys do not overlap. Conflicting items
are serialized. Windows paths are normalized case-insensitively with lexical
`.` and `..` segments collapsed; glob pairs are expanded against known exact
touch paths and otherwise remain conflicting unless their fixed prefixes or
suffixes prove them disjoint. Preparation is not blocked by a later integration or
verification prerequisite: for example, startup/resource preparation,
dependency preparation, and manifest declaration preparation can proceed
together even though their integrated result is verified later. The current
product reports this plan but does not spawn agents or benchmark work.

An unknown node or dependency, duplicate work-item ID, invalid
classification/status confidence, self-edge, cycle, or unschedulable stage or
touch conflict makes
`todoGraph.status` `invalid`, records a typed issue, and emits no execution
waves. Do not flatten or ignore the invalid graph. Correct the mechanically
owned producer or restore the missing canonical TODO, then rerun
`winapp-migrate uwp verify`.

Candidate strategies are not an allowlist. A different strategy is valid only
when it preserves the source invariant, satisfies every typed completion check,
and violates no forbidden shortcut. Detection facts must not be edited to make
that strategy appear applicable.

## Generated example

For a source with dependencies, preserved `App` files, and manifest activation,
the generated plan begins with parallel preparation, serializes the two
`App.xaml.cs` integrations, then emits one integrated verification item:

```json
{
  "todoGraph": {
    "status": "valid",
    "workItems": [
      {
        "id": "UWMIG001:prepare-startup-resources",
        "todoId": "UWMIG001",
        "stage": "preparation"
      },
      {
        "id": "UWMIG002:prepare-manifest-declarations",
        "todoId": "UWMIG002",
        "stage": "preparation"
      },
      {
        "id": "UWMIG003:prepare-dependencies",
        "todoId": "UWMIG003",
        "stage": "preparation"
      }
    ],
    "executionWaves": [
      {
        "index": 0,
        "stage": "preparation",
        "workItemIds": [
          "UWMIG001:prepare-startup-resources",
          "UWMIG002:prepare-manifest-declarations",
          "UWMIG003:prepare-dependencies"
        ]
      },
      {
        "index": 1,
        "stage": "integration",
        "workItemIds": [
          "UWMIG001:integrate-startup-resources",
          "UWMIG002:integrate-manifest-declarations",
          "UWMIG003:integrate-dependencies"
        ]
      },
      {
        "index": 2,
        "stage": "integration",
        "workItemIds": [
          "UWMIG002:integrate-activation-routing"
        ]
      },
      {
        "index": 3,
        "stage": "verification",
        "workItemIds": [
          "UWMIG003:verify-integrated-target"
        ]
      }
    ],
    "issues": []
  }
}
```
