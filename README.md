# WinApp Migrate

WinApp Migrate is a collection of versioned pipelines for moving existing
Windows applications to WinUI 3 and the Windows App SDK. The first pipeline
supports C# UWP applications. A future WPF pipeline can reuse the product's
enforcement, evidence, replay, and release infrastructure without sharing
source-framework-specific transforms.

## Repository layout

```text
plugins/winapp-migrate/                    Agent Plugin and migration Skills
  skills/uwp-to-winapp/                    UWP-to-WinApp semantic workflow
src/WinAppMigration.Cli/                   `winapp-migrate` command line
src/WinAppMigration.Enforcement/           Shared evidence and closure engine
src/Pipelines/Uwp/UwpMigration.Contracts/ UWP report schema 1.4 contracts
src/Pipelines/Uwp/UwpMigration.Enforcement/ UWP report and WinApp CLI adapter
src/Pipelines/Uwp/UwpMigration.Mechanical/ Deterministic UWP conversion
tests/Pipelines/Uwp/                       UWP pipeline tests
```

The common Enforcement layer owns phase state, target fingerprints, evidence
freshness, diagnostic grouping, retry budgets, canonical replay, and closure
gates. The UWP adapter binds those shared contracts to schema 1.4 and invokes
released WinApp CLI commands. UWP-specific activation, package, project-item,
XAML, and C# behavior stays in the UWP pipeline rather than being prematurely
generalized.

## UWP pipeline

The UWP pipeline owns schema 1.4 reports, source and dependency inventory,
activation declarations, safe filesystem handling, deterministic XAML and C#
transforms, residual findings, target-project graph verification, MSBuild item
evaluation, and the six explicit project-item decision strategies.

```text
winapp-migrate uwp create <source> --output <target>
winapp-migrate uwp migrate <source> --output <target>
winapp-migrate uwp verify <target>
winapp-migrate uwp decide-project-item <target> <item> <strategy> <rationale>
winapp-migrate uwp enforce configure <target> --write-default
winapp-migrate uwp enforce configure <target> --policy <policy.json>
winapp-migrate uwp enforce assess <target>
winapp-migrate uwp enforce run <target>
winapp-migrate uwp enforce diagnose <target>
winapp-migrate uwp enforce ui <target> -- <winapp-ui-arguments>
winapp-migrate uwp enforce record-failure <target> <build|runtime> <signature> --evidence <path>
winapp-migrate uwp enforce record-diagnosis <target> <signature> --source-location <path:line> --target-location <path:line> --source-invariant <text> --hypothesis <text> --test <text> --evidence <path>
winapp-migrate uwp enforce sync-report <target>
```

`enforce run` invokes `winapp run` first with `--no-launch --json`, then with
`--no-build --detach --json` after a successful analyzer-enabled build. It
persists versioned attestations under `.migration-evidence/enforcement`,
groups build diagnostics, and refuses to repeat a failed build for an
unchanged target fingerprint. A repeated replay requires fresh discriminating
evidence: use `--force-replay --discriminating-evidence <target-relative-file>`;
configuration, architecture, runtime, framework, and repeatable MSBuild
properties are forwarded identically to build and replay. The runner does not
infer semantic success from a build or process launch: state evidence,
semantic findings, and the report summary must independently satisfy the
closure gates.

`enforce diagnose` preserves controlled foreground debug output. A runtime
failure opens a diagnosis frontier that requires fresh source/target locations,
the preserved source invariant, one falsifiable hypothesis, one discriminating
test, and target-contained evidence before another build or replay. Target UI
automation must use `enforce ui`, which requires a fresh canonical replay.
The runner resolves WinApp CLI from `--winapp`,
`WINAPP_MIGRATE_WINAPP_PATH`, or PATH, in that order, so raw `winapp.exe` need
not be exposed to the migration agent.

### Durable workflow and budgets

`enforce assess` resumes the persisted five-stage controller:
`source-oracle`, `mechanical-stabilization`, `target-bootstrap`,
`semantic-convergence`, and `closure`. Snapshot `phase` and `stage` are the
controller's stage, while `frontier.kind` explains the next action. A semantic
correction requires a new build/replay **within semantic-convergence**, not a
restart of mechanical work. Closure can reopen semantic convergence.
For a fresh workflow, source readiness enters mechanical stabilization even
when report mechanical verification has passed: the initial analyzer-attested
build is charged there. A fresh build enters target bootstrap, where the initial
replay is charged. Before semantic convergence, a target change requiring a new
build returns to mechanical stabilization; existing counters are retained.

The strict schema `1.0` policy and progress are atomically persisted as
`.migration-evidence/enforcement/policy.json` and `progress.json`.
Configure the policy before the first controlled operation or recorded
failure; subsequent policy changes (including direct file replacement) fail
closed. `configure --write-default` writes the balanced policy to the target
and stdout; edit a copy and install it with `configure --policy <file>`.
Assessment starts wall-clock budgets, including time between CLI invocations.
Run the first `enforce assess` immediately after the state plan is created and
before source capture; otherwise the source-oracle phase has not entered the
durable workflow clock. Run it again immediately after source completion and
after each slice completion, before beginning work in the next budget unit.
Pre-controller workspaces import the available attestations and attempts for
start times and counters; unavailable historical operations are not invented.

| Stage | Soft / hard minutes |
| --- | --- |
| source-oracle | 20 / 30 |
| mechanical-stabilization | 15 / 25 |
| target-bootstrap | 10 / 15 |
| semantic-convergence | 15 / 25 **per slice** |
| closure | 15 / 25 |

The default total hard limit is 150 minutes, workflow-wide. Each budget unit
(each stage, and each semantic slice) independently permits **one**
10-minute measured-progress extension, capped by its original hard deadline
and the workflow total hard deadline. The strict policy field
`maximumProgressExtensionsPerUnit` accepts 0 or 1 (balanced default: 1);
the former `maximumProgressExtensions` name is rejected.
Progress is derived from target changes, fewer unresolved required states,
completed diagnosis/slice evidence, or new discriminating evidence—not an
agent's estimate of completion. Soft expiry returns `stage-budget`. A hard
limit or operation ceiling blocks required work durably; optional work is
reported as `continue-with-debt`. Independent slices may continue, but debt
never satisfies a dependency or marks a state verified. Total hard expiry
permits no further controlled operations.

Each stage/slice separately counts external operations with these balanced ceilings:

| Stage | Builds | Replays | Diagnostic runs | Hypotheses | UI actions |
| --- | --- | --- | --- | --- | --- |
| source-oracle | 0 | 0 | 0 | 0 | 0 |
| mechanical-stabilization | 3 | 0 | 0 | 0 | 0 |
| target-bootstrap | 0 | 2 | 1 | 1 | 0 |
| semantic-convergence (per slice) | 3 | 2 | 1 | 1 | 40 |
| closure | 0 | 1 | 0 | 0 | 10 |

Bootstrap diagnostic runs/hypotheses require a bootstrap runtime failure.
Closure permits only explicitly justified final replay/UI operations: supply
new `--discriminating-evidence <path>` to `enforce run --force-replay` or
`enforce ui <target> --discriminating-evidence <path> -- <UI arguments>`.
Report synchronization is not a counted external operation. Incompatible
stage operations are rejected before invocation even under custom policies;
their ceilings must remain zero.

Operations are charged **before** invocation, so interruption or a new `run`
cannot reset ceilings. The snapshot
includes active slice/state IDs, elapsed time, deadlines, extension decisions,
per-unit ceilings/counts (including `budget.extensionsUsed` for the active
unit), and total operation counts.

State-plan schema `1.0` additively accepts `slice`, `dependencies` (slice IDs),
and `criticality` (`required` or `optional`) on each state. Defaults are the
state ID and required work, respectively. States sharing a slice must share
criticality; their dependencies are combined and must reference existing
slices without cycles. Only the first dependency-ready incomplete slice is
active. Required source observations need captured evidence before target work,
and required blocked/unverified states do not satisfy closure.

An active runtime failure exposes `diagnosis-required`. A diagnosis is bound
to its signature, state/slice, and failing target fingerprint; it cannot be
overwritten or repeated for the same signature/fingerprint. After diagnosis,
`change-required` prevents another build/replay until the target changes.
Repeated debug/hypothesis work and replay without new discriminating evidence
are refused. `--force-replay` and `--max-attempts` bypass none of these guards.

The UWP report remains explicitly versioned as schema 1.4. A future WPF
pipeline will start with its own pipeline schema; it will not reinterpret the
UWP schema as a universal migration contract.

## Repository relationships

```text
microsoft/win-dev-skills
  catalog only
       |
       | pins a released Plugin path, tag, and commit
       v
winapp-migrate
  Skills + pipeline contracts + mechanical transforms + enforcement
       |
       | invokes released commands; does not copy their implementation
       v
microsoft/winappCli
  project creation, build, package, registration, launch, diagnostics, and UI
```

- `win-dev-skills` discovers and pins the released `winapp-migrate` Plugin. It
  does not own or copy migration implementation.
- `winapp-migrate` owns migration-specific state, transforms, verification,
  evidence, replay, and closure.
- WinApp CLI owns general Windows application creation, build, deployment,
  runtime diagnosis, and UI automation. It does not depend on
  `winapp-migrate`.
- Static WinUI diagnostics remain a separately versioned analyzer package.
  Migration pipelines may require it in target projects, but do not bundle or
  fork its implementation.

The dependency direction is intentionally one-way: `winapp-migrate` may invoke
WinApp CLI, while WinApp CLI remains usable without any migration product.
