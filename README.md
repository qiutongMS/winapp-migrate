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
src/Pipelines/Uwp/UwpMigration.Contracts/ UWP report schema 1.5 contracts
src/Pipelines/Uwp/UwpMigration.Enforcement/ UWP report and WinApp CLI adapter
src/Pipelines/Uwp/UwpMigration.Mechanical/ Deterministic UWP conversion
tests/Pipelines/Uwp/                       UWP pipeline tests
```

The common closure layer owns phase state, target fingerprints, evidence
freshness, diagnostic grouping, canonical replay, and closure
gates. The UWP adapter binds those shared contracts to schema 1.5 and invokes
released WinApp CLI commands. UWP-specific activation, package, project-item,
XAML, and C# behavior stays in the UWP pipeline rather than being prematurely
generalized.

## UWP pipeline

The UWP pipeline owns schema 1.5 reports, source and dependency inventory,
activation declarations, safe filesystem handling, deterministic XAML and C#
transforms, residual findings, target-project graph verification, MSBuild item
evaluation, and the six explicit project-item decision strategies.

```text
winapp-migrate uwp create <source> --output <target>
winapp-migrate uwp migrate <source> --output <target>
winapp-migrate uwp verify <target>
winapp-migrate uwp decide-project-item <target> <item> <strategy> <rationale>
winapp-migrate uwp source run <target>
winapp-migrate uwp source diagnose <target>
winapp-migrate uwp source capture <target> -- <winapp-ui-arguments>
winapp-migrate uwp closure assess <target>
winapp-migrate uwp closure attest <target>
winapp-migrate uwp closure diagnose <target>
winapp-migrate uwp closure capture <target> -- <winapp-ui-arguments>
winapp-migrate uwp closure capture <target> --state <state-id> -- <winapp-ui-arguments>
winapp-migrate uwp closure record-state <target> --state <state-id> --probe <probe.json> [--probe <probe.json>...]
winapp-migrate uwp closure record-failure <target> <build|runtime> <signature> --evidence <path>
winapp-migrate uwp closure record-diagnosis <target> <signature> --source-location <path:line> --target-location <path:line> --source-invariant <text> --hypothesis <text> --test <text> --evidence <path>
winapp-migrate uwp closure finalize <target>
```

`uwp create` / `uwp migrate` also accept `--package-identity <guid>` to
override template-generated package identity values and `--portable-report`
to record `target.root` as `.`. Together these options let callers stabilize
the package identity and relocate the target directory without rewriting
product artifacts after the migration completes.

`source run` resolves the exact source project from `migration-report.json`,
captures the desktop window inventory before and after launch, and authorizes
only newly created HWNDs for `source capture`. Source commands run only while
the durable controller is at `source-oracle`; they persist launch,
diagnostic, UI, stdout, and stderr provenance below the report-declared source
evidence root. The run capability is bound to a deterministic fingerprint of
the source tree, so any source code, XAML, project, or resource change rejects
later capture. `source diagnose` provides the bounded foreground
`--debug-output` fallback. Other source UI actions require `-w` or `--window`
with an HWND authorized by the current source run.

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
To close a state, declare its machine-checkable `assertions` before capture,
use `capture --state <id>` for every probe in its ordered sequence, then pass
the canonical target-relative receipt paths to `record-state`. State-bound
inspect, screenshot, and action commands require exactly one explicit
`-w/--window`. Windows checks that HWND's owner against the canonical replay
process identity (**PID plus process start time**) before and after the command.
Source, other-process, and reused-PID HWNDs are rejected. Replay identity comes
from the OS through the resolver, never a caller-provided timestamp; legacy
PID-only replay/probe receipts must be re-attested/recaptured.
Use **unbound** `capture -- list-windows --json` for discovery. The product
checks receipt seals, workflow membership, raw-output hashes, state/source
contract identity, the recorded HWND/process/command binding, success, and
current fingerprint/replay freshness. It derives
`passed`/`verified` and writes a sealed state attestation; editing those status
strings or receipt markers alone cannot establish closure.
Closure trust uses the persistent named Windows user CNG key
`WinAppMigration.StateClosure.v1`. Verification loads its public key
independently, requires the artifact key to match, and authenticates operation
IDs/digests, build/replay attestations and raw outputs, probe receipts, and
state attestations before full re-derivation. An attacker-supplied key cannot
establish that local provenance. These seals provide **same-user tamper-evidence**,
not an OS security boundary against arbitrary malicious code running as that
user (which can use the user's signing key).

Portable checks against an embedded key establish signature integrity only,
not locally trusted provenance or closure success. Moving a bundle under the
same user can retain trust, but another user/machine does not automatically
inherit the local trust anchor. Closure verification also re-resolves every
bound HWND and process start time: `record-state`, `finalize`, and assessments
fail closed when the original window/process is gone or reused. Historical
signature integrity is not a substitute for that live verification. Legacy
unsigned/unbound operation chains require re-attestation/recapture.

Window and output options accept WinApp's separated, `=`, and `:` forms
with either alias (for example `-w 42`, `--window=42`, `-w:42`,
`-o path`, `--output=path`, `-o:path`). Every successful screenshot must
produce a fresh valid PNG that is snapshotted and hashed in its probe receipt;
exit zero without that evidence is rejected.
The pinned WinApp CLI defaults to `screenshot.png` in its process working
directory. A state-bound screenshot without `-o/--output` runs inside its
sealed per-probe working directory, so the real default works without
overwriting target assets or changing the target fingerprint. Explicit output
paths remain target-relative (or contained absolute paths); unbound captures
retain WinApp's default in the target directory.

The assertion contract compares mapped source observations with genuine
`inspect` trees or single-property `get-property --json` results. Selection and
keyboard focus require `get-property`; they are not inspect fields. The
selector and native `--property` name must match the assertion contract.
The returned `elementId` must be a nonempty string, but may be a generated
runtime slug rather than the requested AutomationId; the sealed command and
HWND/process/replay binding establish the target, not returned-ID equality.
The known `element` member is accepted and validated as WinApp's typed
`UiElement` detail, without permitting verdict fields or malformed collections.
Native `"True"`/`"False"` strings and expansion enums normalize to their
typed inspect equivalents; invented verdict fields are never assertions.
It does not infer visual parity, timing, lifecycle, or other prose claims.
Unsupported assertions remain unverified. Low-information PNGs are classified
rather than treated as visual proof. Legal 16-bit/interlaced images whose
information content is not analyzed are `ValidUnclassified`, not corrupt:
they may supplement verified nonvisual assertions but never prove visual
information or parity. Corrupt PNGs remain invalid. Verified nonvisual assertions can close
without a screenshot, and intentional restoration similarity is not rejected.
`finalize` reuses the canonical evidence materializer and synchronizes mapped
TODOs and report summaries. Findings resolve only when all their mapped
`completionAssertions` are satisfied; prose-only findings remain open.
State attestations contain product-derived source/target evidence hash
manifests. Every assessment re-derives those manifests and requires canonical
screenshot/JSON copies to match the selected original evidence byte-for-byte.
Replacing a canonical file—even with another valid image/tree—or injecting an
unbound optional screenshot invalidates completion without changing application
fingerprints. Finalize restores canonical copies; caller-authored hashes do not
authorize them.
Report synchronization also compares every source/target state's ID, status,
and reason with the derived plan. Matching aggregate statuses or IDs alone
cannot hide failed/blocked or contradictory per-state observations.
See [the assertion contract and recording protocol](plugins/winapp-migrate/skills/uwp-to-winapp/references/behavioral-validation.md).
The gateway resolves WinApp CLI from `--winapp`,
`WINAPP_MIGRATE_WINAPP_PATH`, or PATH, in that order, so raw `winapp.exe` need
not be exposed to the migration agent.

The Agent Plugins package ships a Copilot `preToolUse` guard. Once a session
enters a schema 1.5 migration workspace, direct `winapp run`, `winapp build`,
and `winapp ui` shell calls are denied even if the agent later changes its
working directory. Informational `--help` calls remain available. Source and
target operations must use their respective product gateways; hosts and
benchmarks remain responsible only for observing whether this product
boundary was loaded and obeyed.

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
satisfy build, replay, parity, or finalization gates. Raw WinApp CLI output cannot substitute for a source or target gateway
artifact. Source baseline capture uses `uwp source`; target closure uses
`uwp closure`.

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
terminal dispositions list none. A `complete` disposition is retained only
while the entire current-target build/replay freshness chain, state
attestations, findings, and report closure remain valid. Target changes or
stale/missing canonical artifacts automatically reopen invalid completion;
an old replay cannot preserve success. Other terminal dispositions remain
immutable. All operational entry points reject a currently terminal run before invoking external tools;
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

The UWP report remains explicitly versioned as schema 1.5. A future WPF
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
