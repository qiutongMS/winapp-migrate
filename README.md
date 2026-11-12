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
src/Pipelines/Uwp/UwpMigration.Contracts/ UWP report schema 1.7 contracts
src/Pipelines/Uwp/UwpMigration.Enforcement/ UWP source builder, report, and WinApp CLI adapter
src/Pipelines/Uwp/UwpMigration.Mechanical/ Deterministic UWP conversion
tests/Pipelines/Uwp/                       UWP pipeline tests
```

The common closure layer owns phase state, target fingerprints, evidence
freshness, diagnostic grouping, canonical replay, and closure
gates. The UWP adapter binds those shared contracts to schema 1.7 and invokes
released WinApp CLI commands. UWP-specific activation, package, project-item,
XAML, and C# behavior stays in the UWP pipeline rather than being prematurely
generalized.

### Host prerequisites

Running a classic UWP source baseline requires:

- Windows 10 or Windows 11 with an active interactive desktop.
- Visual Studio with the **Universal Windows Platform development** workload,
  including Visual Studio MSBuild and the UWP build tools.
- A Windows 10 SDK compatible with the source project's
  `TargetPlatformMinVersion`. The source gateway prefers the project's exact
  `TargetPlatformVersion` when installed, otherwise it selects the newest
  compatible installed UAP SDK.
- Developer Mode, or an equivalent policy that permits trusted app
  sideloading.
- NuGet connectivity for restore when required framework packages are not
  already cached.
- Released WinApp CLI v0.8.0 or later, available through `--winapp`,
  `WINAPP_MIGRATE_WINAPP_PATH`, or PATH.

`winapp-migrate` owns classic UWP project detection, SDK selection, Visual
Studio MSBuild restore/build, loose AppX layout resolution, and preparation of
restored UWP framework dependencies. It then passes the existing loose layout
to released WinApp CLI for staging, registration, activation, diagnostics, and
UI automation. WinApp CLI does not need classic UWP `.csproj` build support.

## UWP pipeline

The UWP pipeline owns schema 1.7 reports, source and dependency inventory,
activation declarations, safe filesystem handling, deterministic XAML and C#
transforms, residual findings, target-project graph verification, MSBuild item
evaluation, and the six explicit project-item decision strategies.

Schema 1.7 also emits structured TODO guidance: authoritative detection facts remain separate from provisional classification, strategies are non-exhaustive, completion checks and touch/conflict sets are typed, and TODO origin distinguishes mechanically predicted from runtime-discovered migration work. Invalid TODO IDs, origins, discoveries, or classifications fail closed. See [the migration TODO workflow](plugins/winapp-migrate/skills/uwp-to-winapp/SKILL.md#4-implement-migration-todos).

```text
winapp-migrate uwp create <source> --output <target>
winapp-migrate uwp verify <target>
winapp-migrate uwp decide-project-item <target> <item> <strategy> <rationale>
winapp-migrate uwp todo assess <target> [todo-id]
winapp-migrate uwp todo status <target>
winapp-migrate uwp todo bind-surface <target> <todo-id> --surface <surface-id> --check <surface-check-id> --check-id <id> --summary <proof>
winapp-migrate uwp todo select-approach <target> <todo-id> <approach-key> --reason <rationale>
winapp-migrate uwp todo record <target> <todo-id> --outcome <implemented|failed> --summary <text>
winapp-migrate uwp todo discover <target> <todo-id> --category <category> --priority <priority> --summary <text> --reason <text> --phase <phase> --signature <signature> --evidence <path>
winapp-migrate uwp source run <target>
winapp-migrate uwp source diagnose <target>
winapp-migrate uwp source capture <target> -- <winapp-ui-arguments>
winapp-migrate uwp closure assess <target>
winapp-migrate uwp closure seal-contract <target> [--extend]
winapp-migrate uwp closure rebind-selector <target> --surface <surface-id> --action <action-id> --reason <text> -- <replacement-ui-command>
winapp-migrate uwp closure attest <target>
winapp-migrate uwp closure diagnose <target>
winapp-migrate uwp closure capture <target> -- <winapp-ui-arguments>
winapp-migrate uwp closure capture <target> --surface <surface-id> -- <winapp-ui-arguments>
winapp-migrate uwp closure record-surface <target> --surface <surface-id> --probe <probe.json> [--probe <probe.json>...]
winapp-migrate uwp closure record-failure <target> <build|runtime> <signature> --evidence <path>
winapp-migrate uwp closure record-diagnosis <target> <signature> --source-location <path:line> --target-location <path:line> --source-invariant <text> --hypothesis <text> --test <text> --evidence <path>
winapp-migrate uwp closure finalize <target> --strict
```

`uwp create` also accepts `--package-identity <guid>` to override template-generated package identity values and `--portable-report` to record `target.root` as `.`. Together these options let callers stabilize the package identity and relocate the target directory without rewriting product artifacts after the migration completes.

`source run` resolves and builds the exact source project from
`migration-report.json`, records the Visual Studio MSBuild command, selected
SDK, build output, and resolved loose AppX layout, then delegates that layout
to WinApp CLI. It captures the desktop window inventory before and after
launch and authorizes only newly created HWNDs for `source capture`. Source
commands run while the source-oracle gate remains incomplete and the workflow
is nonterminal; they persist launch, diagnostic, UI, stdout, and stderr
provenance below the report-declared source evidence root. The run capability
is bound to a deterministic fingerprint of the source tree, so any source
code, XAML, project, or resource change rejects later capture.
`source diagnose` provides the bounded foreground `--debug-output` fallback.
Other source UI actions require `-w` or `--window` with an HWND authorized by
the current source run.

TODO assessments separate `implementationStatus`, `verificationStatus`, and `verificationScope`. Implementation and optional strategy receipts are bookkeeping, not proof or permission to capture a surface. Only explicit completion checks define verification scope: project checks use current project/build evidence; surface checks map exact required surfaces to `target-reachable` and/or `uia-structure-match`. Project-only TODOs need no surface binding. Business obligations remain explicit human-validation items rather than invented homepage bindings or hidden UI acceptance gates. One shared current signed surface proof serves every mapped TODO; there is no development pass, readiness milestone, or duplicate TODO verification receipt. Rejected approaches retain their signature/evidence/retry condition, and reselecting one requires retry evidence without restricting build or replay. `todo status` combines workflow history and telemetry with implementation progress, current scoped verification, failures, and human handoffs.

`closure attest` invokes `winapp run` first with `--no-launch --json`, then with `--no-build --detach --json` after a successful analyzer-enabled build. It persists versioned attestations under `.migration-evidence/enforcement` and groups diagnostics. Explicit build/replay retries require no diagnosis, target change, intent file, or retry budget. Use `--force-replay` to relaunch a current successfully built target; earlier process-bound surface proof becomes stale. Configuration, architecture, runtime, framework, and repeatable MSBuild properties are forwarded identically to build and replay. A successful build or launch alone never proves UI parity.

`closure diagnose` optionally preserves foreground debug output as a fingerprint-bound diagnostic probe, never canonical build or parity proof. Recorded failures and diagnoses preserve attribution but do not authorize later operations. Target UI automation uses `closure capture` after a fresh canonical replay; repeated inspection is permitted. Snapshot receipts explicitly report `verificationScope: "ui-surface-parity"`, current surface results, findings, and `humanValidationItems`; `--full` includes durable history. Capture persists raw output and an immutable per-probe manifest; `--raw-output` forwards the original output. Optional `--discriminating-evidence` files are diagnostic notes, not intent or retry prerequisites.

Before source capture or workflow operations, establish a schema `3.0` surface inventory with `closure seal-contract`. An earlier status query does not prevent initial sealing; actual source/evidence activity and independently anchored operation history still do. The independently anchored required hash freezes required/excluded accounting, source definitions, navigation commands, probe indexes, and ordering; only append-only pre-start extensions are allowed. `closure rebind-selector` may repair only a navigation action's locator while preserving its verb, payload, order, and required hash. The signed, rollback-resistant amendment invalidates earlier target probes for that surface. See [the surface plan and capture guide](plugins/winapp-migrate/skills/uwp-to-winapp/references/ui-surface-parity.md).

For temporary `closure attest` diagnostics, set `WINAPP_MIGRATE_TRACE=1`. The command prints its JSONL log path to stderr and writes flushed events under `.migration-evidence/enforcement/diagnostics`. Events distinguish lock acquisition, workspace loading/fingerprinting, build/replay, child exit, stdout/stderr draining, snapshot writes, CLI serialization/return, and process exit. Child waits emit five-second heartbeats with exit/stream state. Logs contain timing and process metadata, not captured output, and are diagnostic only; they do not change acceptance, timeouts, JSON stdout, or evidence freshness. Unset the variable to disable them.

To close a surface, use `capture --surface <id>` for its sealed navigation
sequence and final complete `inspect --json`, then pass the canonical
target-relative receipt paths to `record-surface`. Surface-bound inspect and
navigation commands require exactly one explicit
`-w/--window`. Windows checks that HWND's owner against the canonical replay
process identity (**PID plus process start time**) before and after the command.
The only delegated exception is Windows `PickerHost`: its executable must be
under `SystemApps` and its live owner chain must reach an authorized source or
canonical replay window. Selection or cancellation may close the picker after
the command; the signed receipt preserves that ephemeral binding. Source,
unrelated-process, untrusted lookalike, and reused-PID HWNDs are rejected.
Replay identity comes from the OS through the resolver, never a caller-provided
timestamp; legacy PID-only replay/probe receipts must be re-attested/recaptured.
Use **unbound** `capture -- list-windows --json` for discovery. The product
checks receipt seals, workflow membership, raw-output hashes, surface/source
contract identity, the recorded HWND/process/command binding, success, and
current fingerprint/replay freshness. It derives
`passed`/`verified` and writes a sealed surface attestation; editing those status
strings or receipt markers alone cannot establish closure.
Closure trust uses the persistent named Windows user CNG key
`WinAppMigration.StateClosure.v1`. Verification loads its public key
independently, requires the artifact key to match, and authenticates operation
IDs/digests, build/replay attestations and raw outputs, probe receipts, and
surface attestations before full re-derivation. An attacker-supplied key cannot
establish that local provenance. These seals provide **same-user tamper-evidence**,
not an OS security boundary against arbitrary malicious code running as that
user (which can use the user's signing key).

Portable checks against an embedded key establish signature integrity only, not locally trusted provenance or closure success. A required contract must also have been established for the current workspace before source/workflow activity; copying a signed contract or bundle does not create that association. Another user/machine does not inherit the local trust anchor. Closure verification re-resolves every app-owned HWND and process start time: `record-surface`, `finalize`, and assessments fail closed when that window/process is gone or reused. A delegated PickerHost that closes during select/cancel is verified from its signed pre/post owner-chain receipt instead. Historical signature integrity is not a substitute for required live app-window verification. Unsupported or unsigned operation histories fail closed; they cannot be reconstructed, reset, or used to sign a new accepted history.

Window and output options accept WinApp's separated, `=`, and `:` forms
with either alias (for example `-w 42`, `--window=42`, `-w:42`,
`-o path`, `--output=path`, `-o:path`). Screenshots may be captured for diagnostics but do not participate in UI
Surface Parity closure.
The pinned WinApp CLI defaults to `screenshot.png` in its process working
directory. A surface-bound screenshot without `-o/--output` runs inside its
sealed per-probe working directory, so the real default works without
overwriting target assets or changing the target fingerprint. Explicit output
paths remain target-relative (or contained absolute paths); unbound captures
retain WinApp's default in the target directory.

UI Surface Parity compares complete semantic UIA trees per window: type,
AutomationId, semantic name/value, hierarchy, visibility, and stable state
properties. It ignores coordinates, dimensions, selectors, and runtime
identity. Screenshots remain diagnostic only. Property assertions, pixel
comparisons, behavior transitions, restoration, timing, lifecycle, pointer
input, accessibility quality, and business logic do not participate in this
closure contract.
`finalize` reuses the canonical evidence materializer and synchronizes mapped
TODOs and report summaries. Findings resolve only when all their mapped fixed
surface checks are satisfied; prose-only findings remain open.
Surface attestations contain product-derived source/target tree hash manifests.
Every assessment re-derives those manifests and requires canonical JSON copies
to match the selected original evidence byte-for-byte. Replacing a canonical
tree invalidates completion without changing application fingerprints.
Caller-authored hashes do not authorize replacement evidence.
Report synchronization also compares every source/target surface's ID, status,
and reason with the derived plan. Matching aggregate statuses or IDs alone
cannot hide failed/blocked or contradictory per-state observations.
`evidence-valid`, `surface-reachable`, and `surface-parity-verified` are distinct product-derived levels. A TODO/finding mapped only to reachability can satisfy that narrower check despite a tree mismatch; app acceptance always requires full parity for every required surface. Unattempted or invalidated evidence is `not-run`, a confirmed mismatch/unreachable result is repairable failure, and an attempted comparison without trustworthy complete trees is `human-validation-required`. See [UI Surface Parity](plugins/winapp-migrate/skills/uwp-to-winapp/references/ui-surface-parity.md).
The gateway resolves WinApp CLI from `--winapp`,
`WINAPP_MIGRATE_WINAPP_PATH`, or PATH, in that order, so raw `winapp.exe` need
not be exposed to the migration agent.

The Agent Plugins package ships a Copilot `preToolUse` guard. Once a session
enters a schema 1.7 migration workspace, direct `winapp run`, `winapp build`,
and `winapp ui` shell calls are denied even if the agent later changes its
working directory. Informational `--help` calls remain available. Source and
target operations must use their respective product gateways; hosts and
benchmarks remain responsible only for observing whether this product
boundary was loaded and obeyed.

Only the documented current commands and artifact protocols are supported. Historical command aliases, policy configuration, and closure waiting APIs are not available.

### Durable migration closure

`closure assess` derives the next evidence frontier from current mechanical checks, analyzer build, canonical launch, required surfaces, fixed finding checks, and report synchronization. The historical stages `source-oracle`, `mechanical-stabilization`, `target-bootstrap`, `semantic-convergence`, and `closure` remain progress telemetry; they neither authorize operations nor override missing evidence. Target changes require fresh build/replay evidence without erasing implementation history. Slice dependencies remain implementation guidance, not extra acceptance conditions.

`uwp verify` also checks current target C#, XAML, and project files for known incompatible UWP contracts, including `Window.Current`, UWP window/dispatcher APIs, view-bound calls, unsupported XAML controls, and UWP project settings. Findings include rule IDs, paths, lines, and required resolutions in `mechanicalVerification.incompatibleUwpResiduals` and repair TODO `UWMIG017`. Verification fails until the findings are repaired; marking a TODO complete cannot clear them. `closure` independently repeats this check against current files, so an older passing report cannot authorize acceptance. This belongs to mechanical/build compatibility within the existing first acceptance criterion, not a fifth criterion.

`uwp create` records these remaining semantic migration tasks without refusing to create the initial workspace. Initial mechanical creation is not final compatibility acceptance. Comments, string examples, build/evidence directories, and `.uwp-source` are excluded from the new API checks; valid desktop WinRT APIs are not banned wholesale. In particular, HWND-initialized `Windows.UI.Popups.MessageDialog` remains supported. These checks cover known incompatible contracts, not all possible runtime behavior; the analyzer-enabled build and required surface evidence remain necessary.

Successful source and surface-bound target `inspect --json` captures automatically attempt an advisory screenshot of the same HWND, with a five-second timeout and process-identity checks. Each probe retains its image or an explicit missing-image reason in `comparison-capture.json`. Open `.migration-evidence/comparison/index.html` after assessment or surface recording to view paired screenshots, UIA links, capture provenance, and the existing per-surface result. Pairing requires matching raw UIA bytes and, for targets, the current replay and target fingerprint; stale or ambiguous images are not substituted. Screenshot or index failures do not change acceptance, and no pixel comparison or extra verification step is introduced.

Progress schema `3.0` is atomically persisted as `.migration-evidence/enforcement/progress.json`. Only schema `3.0` is accepted; older or malformed histories fail closed without rewriting artifacts. Missing progress after operations have started also fails closed, including when the independent operation-chain anchor is the only remaining record. There is no history import, reconstruction, or retired-authorization recovery. Reports remain schema `1.7`, TODO progress and surface plans use their independent schemas `3.0`, and closure evidence and the execution guard use their independent schemas `1.0`.

Elapsed time and aggregate operation totals remain observable in snapshot `telemetry` but never authorize, cancel, defer, or terminate otherwise valid work. There is no product wall-clock deadline, progress extension, or fixed per-stage operation ceiling. Progressive target changes may use as many informative gateway operations as needed. Host wall time, idle timeout, turns, and tokens belong to the calling host or benchmark.

The gateway preserves signed operation history, anti-rollback anchors, provenance, fingerprints, raw-output hashes, and process/window ownership. Optional diagnosis/hypothesis records remain deduplicated for the same failure tuple. These authenticity constraints do not impose stage authorization, retry budgets, mandatory diagnosis, target-change gates, or intent-file ceremonies. An unchanged and still-valid probe sequence can reuse its original signed surface proof instead of generating another receipt.

Build and replay outputs are classified as canonical attestations. Diagnostic
and UI outputs are classified as development probes. Both carry target
fingerprint and producer provenance, but only fresh canonical attestations can
satisfy build, replay, parity, or finalization gates. Raw WinApp CLI output cannot substitute for a source or target gateway
artifact. Source baseline capture uses `uwp source`; target closure uses
`uwp closure`.

Surface-plan schema `3.0` accounts for every source definition as a required
or explicitly excluded surface. Required surfaces may retain `slice` and
`dependencies` for implementation ordering, but their criticality is always
required. Callers may explicitly capture any declared surface. Unavailable
source trees remain explicit human-validation work and cannot satisfy closure.

Snapshots (`state.json`, snapshot schema `1.0`) add `disposition`, also persisted
in progress, with stable `kind`, `reasonCode`, `terminal`, `retryable`,
`allowedOperations`, and `finalization` fields:

| Kind | Terminal | Retryable | Finalization |
| --- | --- | --- | --- |
| continue | no | yes | not-ready |
| complete (all closure gates passed) | yes | no | complete |
| infrastructure-failure (invalid artifacts or process invocation failure) | yes | yes | failed |

`allowedOperations` lists candidate operation kinds, not stage permissions; each gateway still checks its actual artifact, build/replay, and provenance prerequisites. Frontiers are guidance. Terminal dispositions list no operations. A `complete` disposition is retained only while the current build/replay freshness chain, surface attestations, mapped fixed checks, and report closure remain valid; stale evidence reopens it. Other terminal dispositions remain immutable and operations reject them before external tools run. `assess` is observational. Infrastructure `retryable` classifies failure, not permission to reset a terminal run; no reset or snapshot-management facility is provided.

Only fixed machine-verifiable checks determine UI acceptance. Unmapped or out-of-scope business findings and TODOs remain explicit diagnostics/human handoffs rather than hidden blockers or automatically verified work. There is no `--max-attempts` option, development-readiness command, or required terminal TODO milestone.

The UWP report remains explicitly versioned as schema 1.7. A future WPF
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
       | passes built layouts to released runtime/UI commands
       v
microsoft/winappCli
  general app tooling, package registration, launch, diagnostics, and UI
```

- `win-dev-skills` discovers and pins the released `winapp-migrate` Plugin. It
  does not own or copy migration implementation.
- `winapp-migrate` owns migration-specific state, transforms, verification,
  evidence, replay, closure, and the classic UWP source build adapter needed
  to establish a migration baseline.
- WinApp CLI owns general Windows application tooling plus registration,
  activation, runtime diagnosis, and UI automation for an existing loose AppX
  layout. It does not contain migration-specific classic UWP project build
  knowledge and does not depend on `winapp-migrate`.
- Static WinUI diagnostics remain a separately versioned analyzer package.
  Migration pipelines may require it in target projects, but do not bundle or
  fork its implementation.

The dependency direction is intentionally one-way: `winapp-migrate` may invoke
WinApp CLI, while WinApp CLI remains usable without any migration product.
