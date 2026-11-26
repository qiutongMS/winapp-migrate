# WinApp Migrate

WinApp Migrate moves existing Windows applications to WinUI 3 and the Windows
App SDK through versioned migration pipelines. The current pipeline supports
C# UWP applications. This repository contains the agent Skill, deterministic
conversion tools, and the evidence-based verification engine.

Start here to understand the code. Use the [UWP pipeline reference](docs/uwp-pipeline.md)
for command options, host prerequisites, artifact schemas, and detailed
verification rules.

## How the system fits together

```text
Agent + UWP Skill
       |
       v
winapp-migrate CLI
       +--> UWP mechanical conversion ------> target files + migration report
       +--> UWP source/target gateways -----> released WinApp CLI
       |                                          |
       |                                   build / launch / UI output
       +--> shared evidence assessment <----------+
                        |
                 snapshot + report + human handoffs
```

- **The Skill** guides discovery and semantic code repairs. It does not grant
  acceptance by writing a successful status or a final response.
- **Mechanical conversion** inventories source files, transforms known C#/XAML
  patterns, reconciles project items, and reports remaining migration TODOs.
- **The UWP gateways** build the classic UWP source through Visual Studio
  MSBuild and collect source/target observations through WinApp CLI.
- **Shared enforcement** authenticates evidence, derives current surface
  results, and decides completion. Framework-specific transforms stay in the
  UWP pipeline.

Product validation and application acceptance are separate. A tested tool can
still report an app's migration as incomplete when current project/launch,
source inventory/mapping, or required target reachability evidence is missing.
UIA mismatch or source-capture limitations alone do not block this bounded
machine acceptance. Diagnostic/human work remains explicit and unverified;
passing does not establish full visual or business equivalence.

Acceptance supports migration in three ways: feed current diagnoses back to
existing TODOs (discover genuinely new obligations as TODOs), derive the final
criteria from original evidence, and return an honest completion/blocker.
There is no second live finding ledger or auxiliary telemetry gate. Budgets
and no-progress stopping belong to the calling host; stopping is not passing.
The host records its decision with `uwp closure stop --reason <reason>`;
the terminal incomplete result retains the outstanding gates.

## Code map

| Area | Start reading here | Responsibility |
| --- | --- | --- |
| Agent workflow and guard | [Skill](plugins/winapp-migrate/skills/uwp-to-winapp/SKILL.md), [hook](plugins/winapp-migrate/com.github.copilot/hooks/guard-skill-execution.ps1) | Agent procedure and declarative tool-use boundary |
| CLI routing | [Program.cs](src/WinAppMigration.Cli/Program.cs), [UwpCommand.cs](src/WinAppMigration.Cli/Pipelines/Uwp/UwpCommand.cs) | Construct dependencies, register commands, parse arguments, and return results |
| UWP report contracts | [MigrationReport.cs](src/Pipelines/Uwp/UwpMigration.Contracts/MigrationReport.cs) | Versioned report, inventory, project-item decisions, TODOs, and verification data |
| Mechanical migration | [MechanicalMigration.cs](src/Pipelines/Uwp/UwpMigration.Mechanical/MechanicalMigration.cs), [Verification](src/Pipelines/Uwp/UwpMigration.Mechanical/MechanicalMigration.Verification.cs) | Conversion orchestration, current-file verification, and report projections |
| Source baseline | [UwpSourceGateway.cs](src/Pipelines/Uwp/UwpMigration.Enforcement/UwpSourceGateway.cs), [SourceSurfaceInventory.cs](src/Pipelines/Uwp/UwpMigration.Enforcement/SourceSurfaceInventory.cs) | Source build/run/capture provenance and source surface discovery |
| UWP closure adapter | [UwpEnforcementRunner.cs](src/Pipelines/Uwp/UwpMigration.Enforcement/UwpEnforcementRunner.cs), [Navigation](src/Pipelines/Uwp/UwpMigration.Enforcement/UwpEnforcementRunner.Navigation.cs) | Load the UWP workspace, execute gateway operations, navigate, and synchronize reports |
| Shared assessment | [EnforcementEvaluator.cs](src/WinAppMigration.Enforcement/EnforcementEvaluator.cs), [WorkflowCompletion.cs](src/WinAppMigration.Enforcement/WorkflowCompletion.cs) | Current gates, surface summaries, next-work guidance, and completion classification |
| Surface proof and comparison | [StateClosureProtocol.cs](src/WinAppMigration.Enforcement/StateClosureProtocol.cs), [SurfaceTreeComparison.cs](src/WinAppMigration.Enforcement/SurfaceTreeComparison.cs) | Re-derive authenticated surface evidence and compare complete semantic UIA trees |
| TODO bookkeeping and checks | [TodoExecutionService.cs](src/Pipelines/Uwp/UwpMigration.Enforcement/TodoExecutionService.cs), [TodoCompletionContract.cs](src/Pipelines/Uwp/UwpMigration.Enforcement/TodoCompletionContract.cs) | Record implementation separately from current project/surface verification |

The project boundary is intentional: `WinAppMigration.Enforcement` owns common
evidence and closure logic; `UwpMigration.Enforcement` adapts it to UWP reports
and source tooling. A future pipeline should use its own contracts and
transforms rather than reinterpret the UWP schema as a universal one.

Several large types are partial. Follow the related `MechanicalMigration.*.cs`
and `UwpEnforcementRunner.*.cs` files instead of assuming that the main file
contains the entire implementation. Tests also split `RunnerAcceptanceTests`
and `EnforcementEvaluatorTests` across files; a filename is not always a test
class name.

## Follow a command through the code

Every command starts at `Program.cs` and is registered by `UwpCommand`.
These are the four useful paths to learn first:

| Command family | Main path | Result to inspect |
| --- | --- | --- |
| `uwp create`, `verify`, `decide-project-item` | [MigrateCommand](src/WinAppMigration.Cli/Pipelines/Uwp/MigrateCommand.cs) / [MigrateVerifyCommand](src/WinAppMigration.Cli/Pipelines/Uwp/MigrateVerifyCommand.cs) / [MigrateProjectItemDecisionCommand](src/WinAppMigration.Cli/Pipelines/Uwp/MigrateProjectItemDecisionCommand.cs) -> mechanical engine and verification | Target files and `migration-report.json` |
| `uwp source` | [UwpSourceCommand](src/WinAppMigration.Cli/Pipelines/Uwp/UwpSourceCommand.cs) -> source gateway; inventory uses `UwpEnforcementRunner` and `SourceSurfaceInventory` | Source launch/capture receipts under the report-declared source evidence root |
| `uwp closure` | [UwpEnforceCommand](src/WinAppMigration.Cli/Pipelines/Uwp/UwpEnforceCommand.cs) -> `UwpEnforcementRunner` -> shared assessment and surface proof | `.migration-evidence/enforcement/state.json`, build/replay attestations, surface results, and synchronized report |
| `uwp todo` | [UwpTodoCommand](src/WinAppMigration.Cli/Pipelines/Uwp/UwpTodoCommand.cs) -> `TodoExecutionService` and current closure assessment | Implementation history, scoped verification, and human handoffs |

`closure attest` obtains an analyzer-enabled canonical build and launch.
`source capture` and `closure capture` collect observations; `bind-source`
freezes an observed source route, and `record-surface` derives target proof.
`assess` evaluates current evidence; `finalize --strict` requires all closure
gates to pass. These are different operations, not interchangeable ways to
mark a migration complete.

Inspect these artifacts when debugging:

- `migration-report.json`: source/target paths, mechanical findings, TODOs, and
  synchronized observations.
- `.migration-evidence/state-plan.json`: required/excluded surfaces and their
  source, navigation, target, and comparison definitions.
- `.migration-evidence/enforcement/`: current snapshot, progress, canonical
  build/replay artifacts, and authenticated operation history.
- `.migration-evidence/comparison/index.html`: diagnostic screenshot/UIA
  comparison, including explicitly labeled matching historical images.

Do not repair verification by editing generated status strings or signing
replacement evidence. Fix the app, capture inputs, or responsible code and
collect the evidence required by the relevant gateway.

## Find the code and tests for your task

| Task | Implementation | Useful tests |
| --- | --- | --- |
| Change a C#/XAML transform or project-item rule | [MechanicalMigration.Xaml.cs](src/Pipelines/Uwp/UwpMigration.Mechanical/MechanicalMigration.Xaml.cs), [ProjectItemDecisions](src/Pipelines/Uwp/UwpMigration.Mechanical/MechanicalMigration.ProjectItemDecisions.cs), [UwpCompatibility](src/Pipelines/Uwp/UwpMigration.Mechanical/MechanicalMigration.UwpCompatibility.cs) | [Mechanical tests](tests/Pipelines/Uwp/UwpMigration.Mechanical.Tests/), [MigrateProjectItemDecisionTests.cs](tests/Pipelines/Uwp/UwpMigration.Mechanical.Tests/MigrateProjectItemDecisionTests.cs) |
| Fix source window ownership or route binding | `UwpSourceGateway`, [WindowOwnerResolver](src/WinAppMigration.Enforcement/WindowOwnerResolver.cs), [SourceNavigationContracts](src/WinAppMigration.Enforcement/SourceNavigationContracts.cs) | [SourceGatewayTests.cs](tests/Pipelines/Uwp/UwpMigration.Enforcement.Tests/SourceGatewayTests.cs), [SourceNavigationBindingTests.cs](tests/Pipelines/Uwp/UwpMigration.Enforcement.Tests/SourceNavigationBindingTests.cs) |
| Investigate a UIA mismatch | `SurfaceTreeComparison` and its product-derived `SurfaceComparisonDiagnostic` records | [SurfaceTreeComparisonTests.cs](tests/Pipelines/Uwp/UwpMigration.Enforcement.Tests/SurfaceTreeComparisonTests.cs) |
| Change evidence, completion, or report synchronization | `StateClosureProtocol`, `EnforcementEvaluator`, `WorkflowCompletion`, and `UwpEnforcementRunner` | [TrustedSurfaceClosureTests.cs](tests/Pipelines/Uwp/UwpMigration.Enforcement.Tests/TrustedSurfaceClosureTests.cs), [SurfaceClosureRunnerTests.cs](tests/Pipelines/Uwp/UwpMigration.Enforcement.Tests/SurfaceClosureRunnerTests.cs), [WorkflowCompletionTests.cs](tests/Pipelines/Uwp/UwpMigration.Enforcement.Tests/WorkflowCompletionTests.cs) |
| Change TODO behavior or command handling | `TodoExecutionService`, `TodoCompletionContract`, and the relevant CLI command | [TodoExecutionTests.cs](tests/Pipelines/Uwp/UwpMigration.Enforcement.Tests/TodoExecutionTests.cs), [PluginHookTests.cs](tests/Pipelines/Uwp/UwpMigration.Enforcement.Tests/PluginHookTests.cs), [CliStandardHandleTests.cs](tests/Pipelines/Uwp/UwpMigration.Enforcement.Tests/CliStandardHandleTests.cs) |

## Build, run, and test

Use Windows, the .NET 10 SDK, and PowerShell 7 (`pwsh`). Running real classic
UWP source baselines additionally requires the Visual Studio UWP workload,
compatible Windows SDK, sideloading permissions, and released WinApp CLI.
See [host prerequisites](docs/uwp-pipeline.md#host-prerequisites).

From the repository root:

```powershell
dotnet restore WinAppMigration.slnx
dotnet build WinAppMigration.slnx --configuration Release --no-restore
dotnet run --project src\WinAppMigration.Cli\WinAppMigration.Cli.csproj --configuration Release --no-build -- uwp --help
dotnet test --solution WinAppMigration.slnx --configuration Release --no-restore
```

The repository selects Microsoft.Testing.Platform in `global.json`; use
`--solution` or `--project` explicitly. For a focused change, run the relevant
project and selector rather than a migration benchmark:

```powershell
dotnet test --project tests\Pipelines\Uwp\UwpMigration.Enforcement.Tests\UwpMigration.Enforcement.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName~SurfaceTreeComparisonTests'
dotnet test --project tests\Pipelines\Uwp\UwpMigration.Mechanical.Tests\UwpMigration.Mechanical.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName~MigrateProjectItemDecisionTests'
```

Release builds enforce recommended analyzers and treat warnings as errors.
Tests use temporary workspaces and test doubles; Windows-specific regressions
also exercise native window/process APIs. Passing these tests is not a
replacement for acceptance evidence from a real migrated application.
The optional [XAML defer-load closure script](tests/Pipelines/Uwp/Test-XamlDeferLoadStrategyClosure.ps1)
operates on an explicitly supplied real-app fixture and requires an interactive
desktop; it is not part of the default test suite.

## Detailed references and related repositories

- [UWP pipeline reference](docs/uwp-pipeline.md): commands, prerequisites,
  retry/disposition semantics, trust, schemas, diagnostics, and durable proof.
- [UWP Skill](plugins/winapp-migrate/skills/uwp-to-winapp/SKILL.md): the agent's
  end-to-end migration procedure.
- [UI Surface Reachability and UIA diagnostics](plugins/winapp-migrate/skills/uwp-to-winapp/references/ui-surface-parity.md):
  surface inventory, observed routes, capture, comparison, and fixed checks.
- [Surface-plan schema](plugins/winapp-migrate/skills/uwp-to-winapp/references/surface-plan-v3.schema.json):
  machine-readable surface plan shape.
- [Hook reference](plugins/winapp-migrate/com.github.copilot/hooks/README.md):
  supported host payloads and guard integration.

```text
microsoft/win-dev-skills    ->    winapp-migrate    ->    microsoft/winappCli
catalog and release pin        migration product        general Windows tooling
```

`win-dev-skills` discovers and pins the released Plugin; it does not own or
copy migration implementation. WinApp CLI owns general packaging,
registration, launch, diagnostics, and UI automation for built app layouts,
not migration-specific classic UWP project build logic. The dependency is
one-way: this product invokes WinApp CLI, which remains usable independently.
Static WinUI diagnostics come from a separately versioned analyzer package,
not a bundled or forked implementation here.
