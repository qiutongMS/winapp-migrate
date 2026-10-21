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
src/Pipelines/Uwp/UwpMigration.Contracts/ UWP report schema 1.4 contracts
src/Pipelines/Uwp/UwpMigration.Mechanical/ Deterministic UWP conversion
tests/Pipelines/Uwp/                       UWP pipeline tests
```

The common Enforcement layer will own phase state, evidence freshness, retry
budgets, canonical replay, and closure gates. It will be added only after its
contract is defined. UWP-specific activation, package, project-item, XAML, and
C# behavior stays in the UWP pipeline rather than being prematurely generalized.

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
```

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
