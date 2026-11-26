# UWP pipeline reference

This is the detailed command and evidence reference. Start with the
[repository code guide](../README.md) for architecture, entrypoints, and
developer commands. For the agent's migration procedure, read the
[UWP Skill](../plugins/winapp-migrate/skills/uwp-to-winapp/SKILL.md).

## Host prerequisites

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

## Commands and pipeline artifacts

The UWP pipeline owns schema 1.7 reports, source and dependency inventory,
activation declarations, safe filesystem handling, deterministic XAML and C#
transforms, residual findings, target-project graph verification, MSBuild item
evaluation, and the six explicit project-item decision strategies.

Schema 1.7 also emits structured TODO guidance: authoritative detection facts remain separate from provisional classification, strategies are non-exhaustive, completion checks and touch/conflict sets are typed, and TODO origin distinguishes mechanically predicted from runtime-discovered migration work. Invalid TODO IDs, origins, discoveries, or classifications fail closed. See [the migration TODO workflow](../plugins/winapp-migrate/skills/uwp-to-winapp/SKILL.md#4-implement-migration-todos).

## Mechanical safety and supported inventory profile

Mechanical input is one explicit entry directory, not arbitrary solution discovery: at most one top-level C# project and one top-level Windows foundation manifest are accepted, and project XML is checked before scaffold writes. A manifest-backed partial inventory or a manifest-less project explicitly declaring UAP is supported, but neither establishes a runnable source baseline; unrelated projects, ambiguous entries and arbitrary `<Project/>` inputs are rejected. Nested project files are preserved as references, not silently incorporated into the entry compilation.

The official `winui` template owns target SDK, minimum OS, package identity and desktop bootstrap. `UWMIG020` records declared target profile facts and requires evaluated version/deployment decisions before feature prescriptions. Packaged and unpackaged activation or versioned background/OAuth/capture support must be checked against the selected release; historical unsupported lists are not a universal policy. MessageDialog ownership/initialization can remain valid; ContentDialog, MVVM, HTTP, navigation and binding modernization are optional choices, not mechanical requirements.

C# namespace edits use syntax nodes and project declaration/shadow guards, not whole-file replacement; emitted platform namespace/type references are rooted where necessary. Scaffold root qualification leaves application namespace declarations and their matching XAML identities unchanged, including names under Microsoft.UI or Microsoft.Windows. Literals, comments, XAML text and ordinary business resource keys remain unchanged. XAML edits are restricted to actual namespace declarations, and `.resw` edits to recognized WinRT attached-property key syntax. These are conservative syntax-based migration rules, not a general semantic compiler or proof that every SDK API maps one-to-one. Reflection/serialized type text receives provisional `UWMIG019` consumer review, not a substring-based compatibility failure or an instruction to rename business data.

The mechanical dispatcher rewrite is limited to a proven Page's `this`/unqualified inherited access with no conflicting source or partial members; other receivers and callback dispatch remain TODOs. RunAsync is not blindly replaced with TryEnqueue: owner, ordering, await completion, errors, enqueue rejection and cancellation require source-specific evidence. Unknown encodings are not repaired by lossy decoding; source bytes are retained and inspection failures are visible. Window/view, display, picker and selector guidance distinguishes detection from API identity, effective resource scope, branch reachability and SDK-specific behavior.

Physical preservation is separate from active SDK item ownership. Classic explicit Compile/Page declarations and the supported literal/recursive forms establish source activity; inactive, nested-project, conditional or otherwise unproven build inputs and Directory.Build/Directory.Packages controls are held under `.uwp-source` rather than activated by SDK globs. SDK default Compile ownership is not inferred when source exclusion properties, conditional/default-item switches or custom build controls make it uncertain; reliable explicit inclusions remain independent of default exclusions. This can defer valid default-owned files until an explicit port rather than risk activating excluded source. Conflicting MainWindow definitions and potentially rebinding callers/code-behind pairs are retained as references until an explicit port preserves their original Page/type/content intent alongside the target Window bootstrap. `UWMIG018` is the semantic source-build obligation for custom imports/properties, ownership and startup collisions; use the TODO workflow, not `decide-project-item`, for these construction decisions.

Content/PRIResource accounting retains attribute/element metadata, refuses unknown operation/conditional semantics and duplicate declarations, and verifies the same authoritative evaluated path, metadata and unique ownership facts used by explicit decisions. A `.resw` extension alone does not prove default PRI ownership. Stable item identity includes ancestor and ordered Choose context, so semantic source changes reopen decisions; strengthened identity may require re-review of decisions from older implementations. `sourceDeclaration` and `sourceContext` preserve the current original-source declaration and branch evidence for deferred items, without executing source imports.

The target graph queries evaluated ProjectReference edges recursively as well as checking discovered nested default-item roots. Missing/external references and accidental SDK compilation/generation overlap remain failures; legitimate shared None/content and intentionally linked/explicit shared sources are not inherently errors. Default-item checks are bounded to recognized SDK definition origins; unusual SDK/build providers still require the target profile and build review rather than a blanket single-physical-owner policy.

TODO and guidance locations refer to target-relative evidence, normally `.uwp-source/<original-relative-path>.reference` for preserved source files; activation analysis's explicitly named source locations and dependency project names retain original-source-root identity. The preserved files are historical snapshots, while current project-item declaration/context facts come from `source.root`; do not assume they are identical after source edits or overwrite a frozen source baseline. Binary Reference/HintPath declarations and excluded DLL assets are explicit dependency evidence. Public `.cer` files are not private signing keys and their runtime/trust use is reviewed; private key material remains excluded.

Inventory counts describe path disposition for eligible inventoried files, not byte equality, complete opaque-directory coverage, active compilation or behavioral equality. `inventory.coverageStatus` and `uninspectedSourcePaths` explicitly expose source-root-relative reparse paths that were not traversed, with `UWMIG022` scope review. Target inspection failures prevent a clean mechanical result. `--portable-report` relocates target-root addressing only; it does not remove the source checkout dependency, make all evidence portable, or establish byte-for-byte reproducibility.

Creation can retain semantic TODOs and is not compatibility or behavioral acceptance. Verification checks current known incompatible contracts, evaluated project items, graph obligations and inspection failures; passing it does not prove compilation, resource precedence, activation payloads, side effects or runtime behavior. Resource/startup TODOs require separate semantic review in addition to surface binding. UIA structure proves neither resource/lifecycle equality nor picker selection/cancellation. Persisted UIA-only resource/startup contracts remain readable and usable; assessment derives a human-validation handoff for missing semantic review without rewriting their completion contracts or frozen source baseline. Passing UIA alone cannot automatically verify those TODOs.

### Mechanical test portfolio

Keep the behavioral matrix for aliases/shadows/interpolation, encoding, metadata and conditional identity, decision reopen, graph exclusions, path/link safety, report locks and process cancellation. CLI template/evaluator doubles test orchestration contracts, not real UWP/WinUI acceptance; the historical `MigrationAcceptance` tag alone is not runtime evidence. The shared source fixture models a classic UAP project rather than a WinUI target, and the template projection retains typed Application/Window bootstrap assignment. Focused safety tests independently cover business-string preservation, receiver identity, unsupported encodings, semantic TODO shape and startup/inactive ownership, with selected real MSBuild reference/metadata/PRI checks and transformed-code compilation. Shared template setup is consolidated instead of multiplying whole-create fixtures; compiled contract stubs establish C# binding/grammar only, and real app SDK builds remain a separate validation boundary.

## Command syntax

```text
winapp-migrate uwp create <source> --output <target>
winapp-migrate uwp verify <target>
winapp-migrate uwp decide-project-item <target> <item> <strategy> <rationale>
winapp-migrate uwp todo assess <target> [todo-id]
winapp-migrate uwp todo status <target>
winapp-migrate uwp todo bind-surface <target> <todo-id> --surface <surface-id> --check <surface-check-id> --check-id <id> --summary <proof>
winapp-migrate uwp todo record <target> <todo-id> --outcome <implemented|failed> --summary <text>
winapp-migrate uwp todo discover <target> <todo-id> --category <category> --priority <priority> --summary <text> --reason <text> --phase <phase> --signature <signature> --evidence <path>
winapp-migrate uwp source inventory <target>
winapp-migrate uwp source run <target>
winapp-migrate uwp source diagnose <target>
winapp-migrate uwp source capture <target> -- <winapp-ui-arguments>
winapp-migrate uwp closure assess <target>
winapp-migrate uwp closure seal-contract <target> [--extend]
winapp-migrate uwp closure bind-source <target> --surface <surface-id> --probe <source-probe.json> [--probe <source-probe.json>...]
winapp-migrate uwp closure navigate <target> --surface <surface-id> --window <hwnd>
winapp-migrate uwp closure rebind-selector <target> --surface <surface-id> --action <action-id> --reason <text> -- <replacement-ui-command>
winapp-migrate uwp closure attest <target>
winapp-migrate uwp closure diagnose <target>
winapp-migrate uwp closure capture <target> -- <winapp-ui-arguments>
winapp-migrate uwp closure capture <target> --surface <surface-id> -- <winapp-ui-arguments>
winapp-migrate uwp closure record-surface <target> --surface <surface-id> --probe <probe.json> [--probe <probe.json>...]
winapp-migrate uwp closure stop <target> --reason <budget-exhausted|no-progress|external-blocker>
winapp-migrate uwp closure finalize <target> --strict
```

`uwp create` also accepts `--package-identity <guid>` to override template-generated package identity values and `--portable-report` to record `target.root` as `.`. Together these options let callers stabilize the package identity and relocate the target directory without rewriting product artifacts after the migration completes.

## Source baseline

`source run` resolves and builds the exact source project from
`migration-report.json`, records the Visual Studio MSBuild command, selected
SDK, build output, and resolved loose AppX layout, then delegates that layout
to WinApp CLI. It captures the desktop window inventory before and after
launch and authorizes only newly observed source-linked HWNDs for `source capture`.
The source process identity includes both PID and process start time. An
authorized window must belong directly to that process or contain its native
descendant window; an unrelated new desktop window is not sufficient. Capture
rechecks the live source identity and root-window ownership. Source
commands remain available while the workflow is nonterminal, including after
initial source capture completes; they persist launch, diagnostic, UI, stdout, and stderr
provenance below the report-declared source evidence root. The run capability
is bound to a deterministic fingerprint of the source tree, so any source
code, XAML, project, or resource change rejects later capture.
`source diagnose` provides the bounded foreground `--debug-output` fallback.
Other source UI actions require `-w` or `--window` with an HWND authorized by
the current source run.

Supplemental source probes are diagnostic evidence, not replacements for a
bound surface's immutable source/navigation contract. Existing baseline
receipts and target proof are not rewritten. If baseline inputs cannot be
reproduced, establish comparable inputs in a fresh workspace rather than
overwriting the frozen baseline. Input-review-only comparisons remain
`needs-work`, expose `completion.inputReviewSurfaceIds`, and use nonterminal
`input-review-required` disposition (`retryable: false`). Diagnostic capture
remains available; unchanged build/replay retries are rejected. Date-like text
alone proves neither dynamic inputs nor an application defect, and is never
ignored. Independent actionable surfaces and confirmed application differences
remain actionable.

## TODO implementation and verification

TODO assessments separate `implementationStatus`, `verificationStatus`, and `verificationScope`. Implementation and failure records are bookkeeping, not proof or permission to capture a surface. Only explicit completion checks define verification scope: project checks use current project/build evidence; surface checks map exact required surfaces to `target-reachable` and/or `uia-structure-match`. Project-only TODOs need no surface binding. Business obligations remain explicit human-validation items rather than invented homepage bindings or hidden UI acceptance gates. One shared current signed surface proof serves every mapped TODO; there is no development pass, readiness milestone, or duplicate TODO verification receipt. Surface assessments preserve `comparisonDiagnostics` and their repair ownership/actions; input mismatches direct comparable capture instead of application repair. Resolved TODOs may leave the current report without invalidating their retained implementation history. `todo status` combines workflow telemetry with implementation progress, current scoped verification, failures, and human handoffs.

Startup/resource TODOs initially contain `surface-binding` requests addressed to the TODO, not invented surface IDs. `todo bind-surface` resolves a request against an actual required inventory surface while preserving all requested checks and existing project/concrete surface obligations. Until resolved, the request remains pending rather than verified or an out-of-scope business handoff.

## Canonical build and replay

`closure attest` invokes `winapp run` first with `--no-launch --json`, then with `--no-build --detach --json` after a successful analyzer-enabled build. It persists versioned attestations and diagnostics. Build/replay retries require no diagnosis, intent file, or tool-owned retry budget; do not retry them to satisfy UIA comparison. Use `--force-replay` when a fresh launch is genuinely needed before completion. Committed proof schema `3.0` retains authenticated observations across replays of the same canonical build; legacy `2.0` remains replay-bound. Canonical build inputs are forwarded identically to build/replay; changed inputs invalidate prior proof. A build or launch alone does not establish inventory or required target reachability.

`closure diagnose` optionally preserves fingerprint-bound debug output, never canonical build or surface proof. Actual failures retain receipts and attribution. Feed confirmed repair evidence into existing TODOs; discover only genuinely new obligations. Malformed successful CLI output or unavailable identity remains unverified collection, not an application defect. Target capture requires a current canonical replay. Snapshots report `verificationScope: "ui-surface-reachability"`, independent reachability/UIA results, and human handoffs; `--full` adds durable history. Raw outputs and immutable probe manifests are preserved.

## Surface contracts and navigation

Before source capture or workflow operations, seal the source inventory. Required-contract schema `3.0` freezes required/excluded accounting, definitions, dependencies, and criticality; plan schema remains `3.0`. Declare an actual target route before capture. Optional `bind-source` independently freezes a gateway-authenticated source route and complete baseline; an existing anchor cannot be deleted to bypass it. Target proof retains optional route/source snapshots for dependent navigation without requiring source capture. Legacy contract schema `2.0` retains frozen-navigation rules. Selector rebinding remains locator-only and requires fresh target evidence. See [the surface guide](../plugins/winapp-migrate/skills/uwp-to-winapp/references/ui-surface-parity.md).

`closure navigate` verifies the current target starting state, executes the declared route, stops at the first failed action, and records honest reachability and optional comparison. A route without `startSurfaceId` must precede navigation in the canonical process. Same-platform start-state authentication is retained, but UWP/WinUI parity is not a prerequisite. Missing/invalid required target evidence remains incomplete; a model's final response never substitutes for the product checkpoint or authorizes extra model calls.

UIA-only limitations do not cause a tool-blocked or input-review continuation once required target reachability is proved. Machine completion is bounded to current project/launch, full source inventory/mapping, and authenticated required target reachability; diagnostics and human obligations remain unverified. Telemetry reports only elapsed time and the signed operation total, not a parallel acceptance ledger. Lock contention returns retryable `enforcement-busy` JSON.

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

Portable checks against an embedded key establish signature integrity only, not locally trusted provenance or closure success. A required contract must also have been established for the current workspace before source/workflow activity; copying a signed contract or bundle does not create that association. Another user/machine does not inherit the local trust anchor. Capture and `record-surface` require live app-window identity; navigation always inspects and verifies the current start state. A delegated PickerHost that closes during select/cancel uses its signed pre/post owner-chain receipt instead. Successful recording archives the authenticated build/replay artifacts and independently commits the surface-proof digest in the workspace's local trust anchor. Later assessment revalidates the archived operation chain, receipts, hashes, contracts, selector generation, canonical build/input identity, and validator-build comparison identity, without requiring old windows to remain alive or rechecking a route against a later mutable parent proof. A current canonical process is still required for new capture and overall strict closure. Legacy uncommitted proofs do not gain durable trust. Unsupported or unsigned operation histories fail closed; they cannot be reconstructed, reset, or used to sign a new accepted history.

`closure finalize` projects report observations from the current authenticated assessment, not historical status strings in the evidence plan. Verified, stale, unsupported, and missing surfaces can coexist in an honest partial report; invalid target evidence is not projected as a successful observation. `--strict` still fails until all closure gates pass. Synchronization does not modify or re-sign historical proof.

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

Optional UIA diagnostics compare complete semantic trees per window: type,
AutomationId, semantic name/value, hierarchy, visibility, and stable state
properties. It ignores coordinates, dimensions, selectors, and runtime
identity. Screenshots remain diagnostic only. Property assertions, pixel
comparisons, behavior transitions, restoration, timing, lifecycle, pointer
input, accessibility quality, and business logic do not participate in this
closure contract.
`finalize` synchronizes mapped TODOs and report summaries against the original
signed evidence. There is no separate live semantic-finding completion system.
Legacy unfinished finding obligations are carried into TODOs or explicit human
handoffs without treating historical evidence as current proof. Successful
imports retain the original input and a byte-preserving `.imported` archive;
consumed input does not recreate retired TODOs. Unsupported or unreadable input
remains an explicit human-review item, not an extra acceptance gate.
Authenticated mismatch and inconclusive results can also be synchronized without declaring closure complete. Stale or modified evidence is reported as unverified, never promoted to success; `--strict` returns a nonzero exit code until full closure passes.
Surface attestations contain product-derived source/target tree hash manifests.
Every assessment re-derives those manifests from the selected original
evidence. Derived source/target copies are not completion conditions; changing
the original evidence still invalidates its proof. Comparison-rule identity is
versioned with parsing, normalization and comparison semantics, not unrelated
assembly builds.
Caller-authored hashes do not authorize replacement evidence.
Report synchronization also compares every source/target surface's ID, status,
and reason with the derived plan. Matching aggregate statuses or IDs alone
cannot hide failed/blocked or contradictory per-state observations.
`evidence-valid`, `surface-reachable`, and `surface-parity-verified` remain distinct product-derived levels. Current authenticated reachability closes a required target even when diagnostic parity is failed/unverified. Existing TODO UIA assertions retain diagnostic handoffs without driving machine repair retries. Unattempted or invalidated target evidence remains pending; unreachable targets remain incomplete. Missing source trees, mismatched trees, and unsupported comparison preserve honest diagnostics without becoming extra gates. See [UI Surface Reachability](../plugins/winapp-migrate/skills/uwp-to-winapp/references/ui-surface-parity.md).

The comparator aligns supported native UWP/WinUI window hosts without flattening application controls or merging windows. Unknown host boundaries require tool support instead of speculative app changes. Authenticated `comparisonDiagnostics` identify application differences, possible input-comparability issues, and unsupported evidence, with raw source/target node locations and bounded expected/actual values. The closure frontier names the repair owner and next action, and the comparison index displays the same diagnostics. Date-like text is never silently ignored; it remains a mismatch until comparable evidence actually matches.
Recognized legacy/modern `NavigationView` provider shapes route structural differences to tool-side review instead of assuming missing application controls. This diagnostic does not normalize control trees or establish parity.
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

## Durable migration closure

`closure assess` derives the next evidence frontier from current mechanical checks, analyzer build, canonical launch, required surfaces, scoped TODO checks, and report synchronization. The historical stages `source-oracle`, `mechanical-stabilization`, `target-bootstrap`, `semantic-convergence`, and `closure` remain progress telemetry; they neither authorize operations nor override missing evidence. Target changes require fresh build/replay evidence without erasing implementation history. Slice dependencies remain implementation guidance, not extra acceptance conditions. Source operations validate their own paths, immutable contract, signed history and applicable terminal disposition under one lock; they do not require a full target assessment, so an incomplete target project does not prevent observing the source.

`uwp verify` also checks current target C#, XAML, and project files for known incompatible UWP contracts, including `Window.Current`, UWP window/dispatcher APIs, view-bound calls, unsupported XAML controls, and UWP project settings. Findings include rule IDs, paths, lines, and required resolutions in `mechanicalVerification.incompatibleUwpResiduals` and repair TODO `UWMIG017`. Verification fails until the findings are repaired; marking a TODO complete cannot clear them. `closure` independently repeats this check against current files, so an older passing report cannot authorize acceptance. This belongs to mechanical/build compatibility within the existing first acceptance criterion, not a fifth criterion.

`uwp create` records these remaining semantic migration tasks without refusing to create the initial workspace. Initial mechanical creation is not final compatibility acceptance. Comments, string examples, build/evidence directories, and `.uwp-source` are excluded from the new API checks; valid desktop WinRT APIs are not banned wholesale. In particular, HWND-initialized `Windows.UI.Popups.MessageDialog` remains supported. These checks cover known incompatible contracts, not all possible runtime behavior; the analyzer-enabled build and required surface evidence remain necessary.

Successful source and surface-bound target `inspect --json` captures automatically attempt an advisory screenshot of the same HWND, with a five-second timeout and process-identity checks. Each probe retains its image or an explicit missing-image reason in `comparison-capture.json`. Open `.migration-evidence/comparison/index.html` after surface capture/recording or finalization to view paired screenshots, UIA links, capture provenance, and the existing per-surface result. Ordinary assessment does not regenerate the index. Pairing requires matching raw UIA bytes and target fingerprint. Matching current-replay images are preferred; an exactly matched historical image may be displayed with an explicit historical label, never as current acceptance proof. Modified or ambiguous images are not substituted. Screenshot collection checks fresh nonempty output and hashes, not a bespoke image decoder. Screenshot or index failures do not change acceptance, and no pixel comparison or extra verification step is introduced.

Progress schema `3.0` is atomically persisted as `.migration-evidence/enforcement/progress.json`. Only schema `3.0` is accepted; older or malformed histories fail closed without rewriting artifacts. Missing progress after operations have started also fails closed, including when the independent operation-chain anchor is the only remaining record. There is no history import, reconstruction, or retired-authorization recovery. Reports remain schema `1.7`; TODO progress and surface plans use their independent schemas `3.0`; committed surface proofs use schema `3.0` with replay-bound legacy `2.0` support. Build/replay attestations and the execution guard retain their independent schemas `1.0`.

Progress persists signed operation history, observation times, and disposition, not a second stage/event ledger or completed-slice cache. Stages and next-work guidance are derived from current evidence and authenticated history; obsolete auxiliary fields are ignored. Snapshot `telemetry` contains only `elapsedMinutes` and `operationCount`; these never authorize, cancel, defer, or terminate otherwise valid work. Elapsed time is the overall workflow wall time; operation totals are derived from the signed operation history. There is no product wall-clock deadline, progress extension, or fixed per-stage operation ceiling. Host wall time, idle timeout, turns, and tokens belong to the calling host or benchmark.

The gateway preserves signed operation history, anti-rollback anchors, provenance, fingerprints, raw-output hashes, and collection-time process/window ownership. Repeated foreground diagnostics retain distinct original receipts and raw output; repair reasoning belongs in the existing TODO rather than another lifecycle. These authenticity constraints do not impose stage authorization, retry budgets, mandatory diagnosis, or intent-file ceremonies. Unchanged tool-blocked comparisons require a relevant change before build/replay retries. An unchanged and still-valid probe sequence can reuse its original signed surface proof instead of generating another receipt.

TODO recording keeps concise implementation, failure, and evidence history without approach selection or a rejected-strategy retry state machine. Legacy approach metadata remains readable and inert; it neither authorizes retries nor establishes acceptance. Implementation history survives target changes and does not replace current project or surface verification.

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
| tool-blocked (unchanged comparisons need tool support) | no | no | not-ready |
| input-review-required (comparable capture inputs need review) | no | no | not-ready |
| complete (all closure gates passed) | yes | no | complete |
| stopped (host budget, no-progress, or external-blocker decision) | yes | no | incomplete |
| infrastructure-failure (invalid artifacts or process invocation failure) | yes | yes | failed |

`allowedOperations` lists candidate operation kinds, not stage permissions; each gateway still checks its actual artifact, build/replay, and provenance prerequisites. Frontiers are guidance. Terminal dispositions list no operations. A `complete` disposition is retained only while the current build/replay freshness chain, surface attestations, mapped fixed checks, and report closure remain valid; stale evidence reopens it. Other terminal dispositions remain immutable and operations reject them before external tools run. `assess` is observational. Infrastructure `retryable` classifies failure, not permission to reset a terminal run; no reset or snapshot-management facility is provided.

The calling host decides when to stop and records that decision with `closure stop --reason budget-exhausted`, `no-progress`, or `external-blocker`. This command does not calculate thresholds or schedule more agent turns. It preserves the failed or unverified gates at stopping time and returns a nonzero exit code for incomplete migration even when the stop was recorded successfully. Later assessment retains those outstanding results; editing the target does not revive the stopped run. An already terminal result is not replaced or downgraded.

Only fixed machine-verifiable checks determine UI acceptance. Unmapped or out-of-scope business findings and TODOs remain explicit diagnostics/human handoffs rather than hidden blockers or automatically verified work. There is no `--max-attempts` option, development-readiness command, or required terminal TODO milestone.

The UWP report remains explicitly versioned as schema 1.7. A future WPF
pipeline will start with its own pipeline schema; it will not reinterpret the
UWP schema as a universal migration contract.
