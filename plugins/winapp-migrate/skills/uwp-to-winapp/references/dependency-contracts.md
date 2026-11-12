# Dependency replacements

Read when a source package or project cannot be carried forward unchanged. The report's `dependencyAnalysis` supplies inventory and inspection gaps, not a package recommendation. Account for unresolved `inspectionIssues` before treating that inventory as complete.

## Inspect the consumed contract

A matching package name or namespace does not establish WinUI 3 compatibility. Inspect what the application actually consumes:

- constructed and inherited types, interfaces, called members, callbacks, and registration lifetimes;
- XAML controls, dependency properties, generated bindings, templates, and resource keys;
- model notifications, collection behavior, serialization, and persisted data;
- dispatcher, window, package identity, and deployment assumptions.

Include reflection, XAML-only references, and configured entry points. Follow project references into the projects that own consumed behavior; a successful startup does not account for dependencies used by other features.

## Choose a replacement

Use a target-compatible package when its consumed API and behavior match. Where the capability exists behind a different API, adapt it at an app-owned boundary. Otherwise port maintainable source or implement an equivalent target-owned capability.

A replacement control library can require changes to bindings, dependency properties, events, and resources even when its controls serve the same purpose. Map those consumed contracts rather than stopping at a successful package restore or compiling call sites.

Keep unsupported members and fallbacks explicit in the linked TODO. A failed package search establishes a replacement gap, not proof that the source feature has no desktop equivalent.
