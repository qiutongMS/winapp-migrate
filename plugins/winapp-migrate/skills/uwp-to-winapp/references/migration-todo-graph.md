# Migration TODO guidance and graph

Schema `1.6` keeps deterministic migration planning in Tier 1. Every TODO has:

- authoritative detection `facts`, separate from a confirmed, ambiguous, or
  provisional `classification`;
- non-exhaustive candidate strategies with preconditions and risks;
- forbidden shortcuts, prose completion conditions, and typed
  `completionChecks`;
- affected states/features, expected touch paths/project items, and conflict
  keys;
- stable `dependsOn` edges and optional path-based `workSlices`.

`todoGraph.edges` records prerequisite-to-dependent edges.
`todoGraph.executionWaves` is the deterministic topological view: TODOs in one
wave have no dependency edge between them and may be considered for later
parallel scheduling after their touch-set conflict keys are checked. The
current product does not launch parallel benchmark or migration work.

An unknown dependency, duplicate ID, self-edge, or cycle makes
`todoGraph.status` `invalid`, records a typed issue, and emits no execution
waves. Do not flatten or ignore the invalid graph. Correct the mechanically
owned producer or restore the missing canonical TODO, then rerun
`winapp-migrate uwp verify`.

Candidate strategies are not an allowlist. A different strategy is valid only
when it preserves the source invariant, satisfies every typed completion check,
and violates no forbidden shortcut. Detection facts must not be edited to make
that strategy appear applicable.

## Generated example

For a source with a project file plus preserved `App.xaml` and `App.xaml.cs`,
this generated report excerpt orders dependency compatibility before startup
integration, then manifest activation:

```json
{
  "todos": [
    {
      "id": "UWMIG003",
      "dependsOn": [],
      "guidance": {
        "reasonCode": "source-dependency-contract-requires-decision",
        "facts": [
          {
            "id": "source-package-reference-count",
            "authority": "authoritative-detection"
          }
        ],
        "classification": {
          "status": "provisional"
        },
        "candidateStrategiesExhaustive": false
      },
      "touchSet": {
        "conflictKeys": [
          "dependency-contract",
          "target-project-graph"
        ]
      }
    },
    {
      "id": "UWMIG001",
      "dependsOn": [ "UWMIG003" ]
    },
    {
      "id": "UWMIG002",
      "dependsOn": [ "UWMIG001" ]
    }
  ],
  "todoGraph": {
    "status": "valid",
    "edges": [
      {
        "prerequisiteId": "UWMIG003",
        "dependentId": "UWMIG001"
      },
      {
        "prerequisiteId": "UWMIG001",
        "dependentId": "UWMIG002"
      }
    ],
    "executionWaves": [
      { "index": 0, "todoIds": [ "UWMIG003" ] },
      { "index": 1, "todoIds": [ "UWMIG001" ] },
      { "index": 2, "todoIds": [ "UWMIG002" ] }
    ],
    "issues": []
  }
}
```
