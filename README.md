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

The common closure layer owns phase state, target fingerprints, evidence
freshness, diagnostic grouping, canonical replay, and closure
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
winapp-migrate uwp closure assess <target>
winapp-migrate uwp closure attest <target>
winapp-migrate uwp closure diagnose <target>
winapp-migrate uwp closure capture <target> -- <winapp-ui-arguments>
winapp-migrate uwp closure record-failure <target> <build|runtime> <signature> --evidence <path>
winapp-migrate uwp closure record-diagnosis <target> <signature> --source-location <path:line> --target-location <path:line> --source-invariant <text> --hypothesis <text> --test <text> --evidence <path>
winapp-migrate uwp closure finalize <target>
```

`uwp create` / `uwp migrate` also accept `--package-identity <guid>` to
override template-generated package identity values and `--portable-report`
to record `target.root` as `.`. Together these options let callers stabilize
the package identity and relocate the target directory without rewriting
product artifacts after the migration completes.

`closure attest` invokes `winapp run` first with `--no-launch --json`, then with
`--no-build --detach --json` after a successful analyzer-enabled build. It
persists versioned attestations under `.migration-evidence/enforcement`,
groups build diagnostics, and refuses to repeat a failed build for an
unchanged target fingerprint. A repeated replay requires fresh discriminating
evidence: use `--force-replay --discriminating-evidence <target-relative-file>`;
configuration, architecture, runtime, framework, and repeatable MSBuild
properties are forwarded identically to build and replay. The closure gateway does not
infer semantic success from a build or process launch: state evidence,
semantic findings, and the report summary must independently satisfy the
closure gates.

`closure diagnose` preserves controlled foreground debug output as a
fingerprint-bound development probe that cannot satisfy canonical closure
gates. A runtime
failure opens a diagnosis frontier that requires fresh source/target locations,
the preserved source invariant, one falsifiable hypothesis, one discriminating
test, and target-contained evidence before another build or replay. Target UI
automation must use `closure capture`, which requires a fresh canonical replay.
Snapshot commands emit bounded receipts by default and accept `--full` for the
durable workflow history. `closure capture` persists raw output and an immutable
per-probe manifest while returning a bounded receipt; `--raw-output` restores
inline forwarding. During closure, one fresh state-intent evidence file may
authorize a sequence of distinct UI commands for the same fingerprint and
canonical replay, while an immediate exact repeat is rejected.
The gateway resolves WinApp CLI from `--winapp`,
`WINAPP_MIGRATE_WINAPP_PATH`, or PATH, in that order, so raw `winapp.exe` need
not be exposed to the migration agent.

The former `uwp enforce`, `run`, `ui`, and `sync-report` names remain
compatibility aliases. `configure` and `wait` are deprecated compatibility
commands; policy limits and elapsed time no longer control closure.

### Durable migration closure

`closure assess` resumes the persisted five-stage controller:
`source-oracle`, `mechanical-stabilization`, `target-bootstrap`,
`semantic-convergence`, and `closure`. Snapshot `phase` and `stage` are the
controller's stage, while `frontier.kind` explains the next action. A semantic
correction requires a new build/replay **within semantic-convergence**, not a
restart of mechanical work. Semantic resolution is independent of evidence
freshness: a target change makes resolved slices' evidence stale, but the
semantic scheduler stays on unresolved dependency-ready work. Stale resolved
slices still satisfy semantic dependencies (deferred debt does not). Closure
selects stale resolved slices for revalidation; an actual failed comparison,
runtime regression, or open semantic finding can reopen semantic work before
the run becomes terminal.
For a fresh workflow, source readiness enters mechanical stabilization even
when report mechanical verification has passed. A fresh analyzer-attested
build enters target bootstrap, and a fresh replay enters semantic convergence
or closure. Before semantic convergence, a target change requiring a new build
returns to mechanical stabilization.

Progress schema `3.0` is atomically persisted as
`.migration-evidence/enforcement/progress.json`. Schema `2.0` imports preserve
operation records, evidence events, slices, fingerprints, and activation
epochs while clearing retired extension, blocked-budget, and time/count
terminal state. Safe schema `1.0` histories also import; ambiguous histories
fail closed without rewriting the original artifact.

Elapsed time and aggregate operation totals remain observable telemetry but
never authorize, cancel, defer, or terminate otherwise valid work. There is no
product wall-clock deadline, progress extension, or fixed per-stage operation
ceiling. Progressive target changes may use as many informative gateway
operations as needed. Host wall time, idle timeout, turns, and tokens belong to
the calling host or benchmark.

The gateway still rejects waste deterministically: an unchanged failed target
cannot rebuild; repeated replay needs fresh discriminating evidence; evidence
content cannot be reused by touching or renaming it; and diagnosis/hypothesis
cannot repeat for the same signature and target fingerprint. Stage
compatibility also remains fixed: source oracle permits no target operation,
mechanical stabilization permits build only, bootstrap requires a runtime
failure before diagnosis, and closure replay/UI requires explicit fresh
evidence.

Build and replay outputs are classified as canonical attestations. Diagnostic
and UI outputs are classified as development probes. Both carry target
fingerprint and producer provenance, but only fresh canonical attestations can
satisfy build, replay, parity, or finalization gates. Raw target WinApp CLI
output cannot substitute for a gateway attestation. Read-only source baseline
capture remains outside this target-only boundary.

State-plan schema `1.0` additively accepts `slice`, `dependencies` (slice IDs),
and `criticality` (`required` or `optional`) on each state. Defaults are the
state ID and required work, respectively. States sharing a slice must share
criticality; their dependencies are combined and must reference existing
slices without cycles. Only the first dependency-ready semantically unresolved slice is
active. Required source observations need captured evidence before target work,
and required blocked/unverified states do not satisfy closure.

Snapshots (`state.json`, snapshot schema `1.0`) add `disposition`, also persisted
in progress, with stable `kind`, `reasonCode`, `terminal`, `retryable`,
`allowedOperations`, and `finalization` fields:

| Kind | Terminal | Retryable | Finalization |
| --- | --- | --- | --- |
| continue | no | yes | not-ready |
| continue-with-debt | no | yes | debt |
| complete (all closure gates passed) | yes | no | complete |
| blocked-required (required dependency or evidence is genuinely blocked) | yes | no | blocked |
| infrastructure-failure (invalid artifacts or process invocation failure) | yes | yes | failed |

`allowedOperations` lists stage-compatible operation kinds; evidence,
freshness, diagnosis, and frontier prerequisites still apply. Deferred and
terminal dispositions list none. Terminal dispositions
are immutable on later observations, including after a target change. All
operational entry points reject a terminal run before invoking external tools;
`assess` remains observational. Infrastructure `retryable` classifies the
failure, not permission to retry the same terminal run. This release does not
provide a terminal-run reset or snapshot-management facility. Perform final
falsification before synchronizing the report into terminal completion.

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
