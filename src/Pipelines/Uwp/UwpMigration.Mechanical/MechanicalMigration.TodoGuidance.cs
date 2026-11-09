// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Contracts;

namespace UwpMigration.Mechanical;

public static partial class MechanicalMigration
{
    public sealed partial class Engine
    {
        private static MigrationTodo CreateAppResourcesTodo(
            IReadOnlyList<string> preservedStartup,
            bool hasSourceProject)
        {
            var locations = preservedStartup.Select(path => new MigrationLocation
            {
                Path = NormalizePath(Path.Combine(
                    ".uwp-source",
                    path + ".reference"))
            }).ToList();
            return new MigrationTodo
            {
                Id = "UWMIG001",
                Category = "app-resources",
                Priority = "required",
                Summary =
                    "Merge application resources and startup behavior from the preserved UWP App files",
                Reason =
                    "The WinUI startup files were kept because UWP App.xaml and App.xaml.cs cannot safely overwrite the desktop bootstrap.",
                Locations = locations,
                AffectedStates = ["app-launch"],
                AffectedFeatures =
                [
                    "application-startup",
                    "application-resources"
                ],
                TouchSet = new MigrationTodoTouchSet
                {
                    Paths =
                    [
                        "App.xaml",
                        "App.xaml.cs",
                        "MainWindow.xaml",
                        "MainWindow.xaml.cs"
                    ],
                    ConflictKeys =
                    [
                        "app-resources",
                        "startup-bootstrap"
                    ]
                },
                Guidance = new MigrationRepairGuidance
                {
                    ReasonCode = "preserved-uwp-app-contract-requires-merge",
                    Facts = locations.Select(location =>
                    {
                        var resourceFile = location.Path.EndsWith(
                            "App.xaml.reference",
                            StringComparison.OrdinalIgnoreCase);
                        return new MigrationGuidanceFact
                        {
                            Id =
                                $"preserved-startup-reference:{location.Path}",
                            Summary = resourceFile
                                ? $"The source application resource file is preserved at {location.Path}; its keys and merged dictionaries must be mapped into target App.xaml."
                                : $"The source startup implementation is preserved at {location.Path}; its launch, activation, and window side effects must be mapped into target App.xaml.cs/MainWindow.",
                            Locations = [location]
                        };
                    }).Append(new MigrationGuidanceFact
                    {
                        Id = "target-startup-ownership",
                        Summary = hasSourceProject
                            ? "The target template owns App.xaml, App.xaml.cs, MainWindow.xaml, and MainWindow.xaml.cs; dependency preparation may proceed independently, while integrated build verification waits for dependency integration."
                            : "The target template owns App.xaml, App.xaml.cs, MainWindow.xaml, and MainWindow.xaml.cs."
                    }).ToList(),
                    Classification = new MigrationGuidanceClassification
                    {
                        Status = "ambiguous",
                        Summary =
                            "The preserved files prove source startup/resource behavior exists, but resource-only and lifecycle responsibilities must be separated by source inspection.",
                        Candidates =
                        [
                            new()
                            {
                                Type = "resource-merge",
                                Confidence = "high",
                                Reason =
                                    "App.xaml commonly owns application resource dictionaries and theme resources."
                            },
                            new()
                            {
                                Type = "startup-lifecycle-port",
                                Confidence = "medium",
                                Reason =
                                    "App.xaml.cs may own launch, activation, or window initialization behavior."
                            }
                        ],
                        DiscriminatingTests =
                        [
                            "Compare source and target App resources by key and merged-dictionary URI.",
                            "Launch the target through every source activation path and verify the first window/page and startup side effects."
                        ]
                    },
                    CandidateStrategiesExhaustive = false,
                    CandidateStrategies =
                    [
                        new()
                        {
                            Id = "merge-resource-contract",
                            Summary =
                                "Merge source application resources into the target-owned WinUI App resource graph.",
                            Actions =
                            [
                                "Inventory source App.xaml resources and merged dictionaries.",
                                "Port only resources used by the migrated target and preserve lookup keys and ordering."
                            ],
                            Preconditions =
                            [
                                "The source resource key, URI, and override ordering are known."
                            ],
                            Risks =
                            [
                                "Changing dictionary order can alter resource resolution or theme overrides."
                            ]
                        },
                        new()
                        {
                            Id = "port-startup-contract",
                            Summary =
                                "Port source launch and activation behavior into the target-owned desktop bootstrap.",
                            Actions =
                            [
                                "Trace source launch/activation handlers and window creation.",
                                "Integrate required behavior without replacing the WinUI template bootstrap."
                            ],
                            Preconditions =
                            [
                                "The source startup invariant and owning target Window are known."
                            ],
                            Risks =
                            [
                                "Copying the UWP App class can reintroduce Window.Current or invalid lifecycle assumptions."
                            ]
                        }
                    ],
                    ForbiddenShortcuts =
                    [
                        "Do not overwrite the WinUI App.xaml.cs or MainWindow bootstrap with the preserved UWP file.",
                        "Do not drop source resource dictionaries because the target launches without them.",
                        "Do not treat a successful build as proof that startup side effects or resource lookup are preserved."
                    ],
                    CompletionConditions =
                    [
                        "Every source application resource consumed by a reachable target state resolves with equivalent precedence.",
                        "Every source startup or activation side effect has an explicit target owner.",
                        "The canonical app-launch state passes with the intended initial window and content."
                    ],
                    CompletionChecks =
                    [
                        new()
                        {
                            Id = "app-resource-contract-accounted",
                            Summary =
                                "Every preserved App resource entry is mapped to a target resource or explicit exclusion decision.",
                            VerificationKind = "resource-contract-accounting",
                            Target = ".uwp-source/App.xaml.reference",
                            Expected = "all-consumed-resources-accounted"
                        },
                        new()
                        {
                            Id = "startup-replay-passed",
                            Summary =
                                "The canonical application launch state verifies the target startup invariant.",
                            VerificationKind = "state-assertion",
                            Target = "app-launch",
                            Expected = "passed"
                        }
                    ]
                }
            };
        }

        private static MigrationTodo CreateProjectDependenciesTodo(
            IReadOnlyList<string> sourceProjects,
            MigrationDependencyAnalysis dependencyAnalysis)
        {
            var locations = sourceProjects.Select(path => new MigrationLocation
            {
                Path = NormalizePath(Path.Combine(
                    ".uwp-source",
                    Path.GetFileName(path) + ".reference"))
            }).ToList();
            var facts = new List<MigrationGuidanceFact>();
            foreach (var (package, index) in dependencyAnalysis.PackageReferences
                .OrderBy(package => package.SourceProject, StringComparer.Ordinal)
                .ThenBy(package => package.Id, StringComparer.Ordinal)
                .ThenBy(package => package.Version, StringComparer.Ordinal)
                .Select((package, index) => (package, index)))
            {
                facts.Add(new MigrationGuidanceFact
                {
                    Id = $"package-reference:{index}",
                    Summary =
                        $"Package '{package.Id}' version '{package.Version ?? "<unspecified>"}' is declared by '{package.SourceProject}' with resolution '{package.ResolutionStatus}' and compatibility '{package.CompatibilityStatus}'"
                        + (string.IsNullOrWhiteSpace(package.Condition)
                            ? "."
                            : $" under condition '{package.Condition}'."),
                    Locations =
                    [
                        new()
                        {
                            Path = package.SourceProject,
                            Line = package.Line
                        }
                    ]
                });
            }
            foreach (var (projectReference, index) in dependencyAnalysis.ProjectReferences
                .OrderBy(reference => reference.SourceProject, StringComparer.Ordinal)
                .ThenBy(reference => reference.Include, StringComparer.Ordinal)
                .Select((reference, index) => (reference, index)))
            {
                facts.Add(new MigrationGuidanceFact
                {
                    Id = $"project-reference:{index}",
                    Summary =
                        $"Project reference '{projectReference.Include}' from '{projectReference.SourceProject}' resolves as '{projectReference.ResolvedPath ?? "<unresolved>"}' with resolution '{projectReference.ResolutionStatus}', compatibility '{projectReference.CompatibilityStatus}', and outsideSourceRoot={projectReference.OutsideSourceRoot.ToString().ToLowerInvariant()}.",
                    Locations =
                    [
                        new()
                        {
                            Path = projectReference.SourceProject,
                            Line = projectReference.Line
                        }
                    ]
                });
            }
            foreach (var (issue, index) in dependencyAnalysis.Issues
                .OrderBy(issue => issue.SourceProject, StringComparer.Ordinal)
                .ThenBy(issue => issue.Kind, StringComparer.Ordinal)
                .Select((issue, index) => (issue, index)))
            {
                facts.Add(new MigrationGuidanceFact
                {
                    Id = $"dependency-inspection-issue:{index}",
                    Summary =
                        $"Dependency inspection issue '{issue.Kind}' affects '{issue.SourceProject}': {issue.Reason}",
                    Locations =
                    [
                        new() { Path = issue.SourceProject }
                    ]
                });
            }
            if (facts.Count == 0)
            {
                facts.Add(new MigrationGuidanceFact
                {
                    Id = "source-project-inventory",
                    Summary =
                        $"Dependency analysis inspected {dependencyAnalysis.Projects.Count} source project(s) and found no package/project references or inspection issues.",
                    Locations = locations
                });
            }
            return new MigrationTodo
            {
                Id = "UWMIG003",
                Category = "project-dependencies",
                Priority = "required",
                Summary =
                    "Review UWP project references, linked content, and dependencies",
                Reason =
                    "Use dependencyAnalysis for the source project-reference graph. Project/package references and files linked from outside the source root require an explicit target-compatibility decision before they can be migrated safely.",
                Locations = locations,
                AffectedStates = ["target-build"],
                AffectedFeatures =
                [
                    "dependency-contracts",
                    "project-graph"
                ],
                TouchSet = new MigrationTodoTouchSet
                {
                    Paths =
                    [
                        "*.csproj",
                        "Directory.Packages.props",
                        "Directory.Build.props"
                    ],
                    ProjectItems =
                    [
                        "PackageReference",
                        "ProjectReference",
                        "Content",
                        "PRIResource"
                    ],
                    ConflictKeys =
                    [
                        "dependency-contract",
                        "target-project-graph"
                    ]
                },
                Guidance = new MigrationRepairGuidance
                {
                    ReasonCode = "source-dependency-contract-requires-decision",
                    Facts = facts,
                    Classification = new MigrationGuidanceClassification
                    {
                        Status = dependencyAnalysis.Issues.Count == 0
                            ? "provisional"
                            : "ambiguous",
                        Summary =
                            "Dependency declarations are authoritative; target compatibility remains provisional until the consumed API contract is compared.",
                        Candidates =
                        [
                            new()
                            {
                                Type = "target-compatible-dependency",
                                Confidence = "medium",
                                Reason =
                                    "A source package or project may have a WinUI-compatible release with a compatible consumed contract."
                            },
                            new()
                            {
                                Type = "target-owned-adapter-or-port",
                                Confidence = "medium",
                                Reason =
                                    "Contract differences may require an adapter, source port, or equivalent target implementation."
                            }
                        ],
                        DiscriminatingTests =
                        [
                            "Inventory the exact source dependency members consumed by reachable application code.",
                            "Build and replay the dependent feature against the selected target contract."
                        ]
                    },
                    CandidateStrategiesExhaustive = false,
                    CandidateStrategies =
                    [
                        new()
                        {
                            Id = "use-compatible-target-dependency",
                            Summary =
                                "Use a target-compatible package or project only when its consumed contract is compatible.",
                            Actions =
                            [
                                "Compare consumed source members, events, async behavior, and ownership assumptions.",
                                "Reference the compatible target dependency and verify each affected feature."
                            ],
                            Preconditions =
                            [
                                "A maintained target-compatible dependency exists.",
                                "Its consumed contract is proven compatible."
                            ],
                            Risks =
                            [
                                "Matching package names or type shapes can hide semantic incompatibilities."
                            ]
                        },
                        new()
                        {
                            Id = "create-target-owned-compatibility-boundary",
                            Summary =
                                "Use a target-owned adapter, maintainable source port, or equivalent implementation for an incompatible contract.",
                            Actions =
                            [
                                "Define the minimum consumed source invariant.",
                                "Implement the compatibility boundary in the migrated solution and validate it through its consumers."
                            ],
                            Preconditions =
                            [
                                "The consumed source contract and target ownership boundary are known."
                            ],
                            Risks =
                            [
                                "Type-shaped shims can compile while dropping events, state changes, errors, or lifetime behavior."
                            ]
                        }
                    ],
                    ForbiddenShortcuts =
                    [
                        "Do not delete a dependency or linked item solely to obtain a clean build.",
                        "Do not substitute a similarly named package without comparing the consumed contract.",
                        "Do not keep target project references to source-checkout or machine-specific paths."
                    ],
                    CompletionConditions =
                    [
                        "Every source package and project reference has a recorded target compatibility decision.",
                        "Every consumed dependency contract is implemented by the selected target dependency, adapter, port, or equivalent.",
                        "The target project graph is contained and the analyzer-enabled build succeeds."
                    ],
                    CompletionChecks =
                    [
                        new()
                        {
                            Id = "dependency-decisions-complete",
                            Summary =
                                "Every inventoried dependency has a target compatibility decision.",
                            VerificationKind = "report-collection-status",
                            Target = "dependencyAnalysis",
                            Expected = "resolved"
                        },
                        new()
                        {
                            Id = "target-project-graph-contained",
                            Summary =
                                "The target project graph has no incomplete ownership or portability issue.",
                            VerificationKind = "report-field",
                            Target = "mechanicalVerification.targetProjectGraph.status",
                            Expected = "passed"
                        },
                        new()
                        {
                            Id = "target-build-passed",
                            Summary =
                                "The analyzer-enabled target build succeeds after dependency decisions.",
                            VerificationKind = "build-attestation",
                            Target = "target-build",
                            Expected = "passed"
                        }
                    ]
                }
            };
        }

        private static MigrationTodo CreateManifestTodo(
            string targetRoot,
            IReadOnlyList<string> manifests,
            List<string> activationCategories,
            MigrationActivationAnalysis activationAnalysis)
        {
            var locations = manifests.Select(path => new MigrationLocation
            {
                Path = NormalizePath(Path.GetRelativePath(targetRoot, path))
            }).ToList();
            var features = activationCategories.Count == 0
                ? ["manifest-capabilities"]
                : activationCategories
                    .Select(category => $"activation:{category}")
                    .Prepend("manifest-capabilities")
                    .ToList();
            return new MigrationTodo
            {
                Id = "UWMIG002",
                Category = "manifest",
                Priority = "required",
                Summary =
                    "Review UWP manifest capabilities and extensions for WinUI 3 desktop",
                Reason =
                    "Safe activation declarations may be migrated mechanically, but application lifecycle routing and runtime activation behavior still require semantic implementation and validation.",
                Locations = locations,
                AffectedStates = activationCategories.Count == 0
                    ? ["app-launch"]
                    : activationCategories
                        .Select(category => $"activation:{category}")
                        .ToList(),
                AffectedFeatures = features,
                TouchSet = new MigrationTodoTouchSet
                {
                    Paths =
                    [
                        "Package.appxmanifest",
                        "App.xaml.cs"
                    ],
                    ConflictKeys =
                    [
                        "activation-routing",
                        "startup-bootstrap"
                    ]
                },
                Guidance = new MigrationRepairGuidance
                {
                    ReasonCode = "manifest-contract-requires-desktop-review",
                    Facts = new List<MigrationGuidanceFact>
                    {
                        new()
                        {
                            Id = "preserved-source-manifest",
                            Summary =
                                $"The UWP manifest was preserved for review at {string.Join(", ", locations.Select(location => location.Path))}.",
                            Locations = locations
                        },
                        new()
                        {
                            Id = "activation-contract-analysis",
                            Summary =
                                $"Activation analysis found {activationAnalysis.Contracts.Count} contract(s), {activationAnalysis.Issues.Count} issue(s), and status '{activationAnalysis.Status}'.",
                            Locations = locations
                        }
                    }.Concat(activationAnalysis.Contracts
                        .OrderBy(contract => contract.Id, StringComparer.Ordinal)
                        .Select(contract => new MigrationGuidanceFact
                        {
                            Id = $"activation-contract:{contract.Id}",
                            Summary =
                                $"Activation contract '{contract.Id}' category '{contract.Category}' uses source schema '{contract.SourceSchema}', migration status '{contract.MigrationStatus}', target schema '{contract.TargetSchema ?? "<none>"}', and verification status '{contract.VerificationStatus}'.",
                            Locations = [contract.SourceLocation]
                        }))
                        .Concat(activationAnalysis.Issues
                            .OrderBy(issue => issue.Kind, StringComparer.Ordinal)
                            .Select((issue, index) => new MigrationGuidanceFact
                            {
                                Id = $"activation-inspection-issue:{index}",
                                Summary =
                                    $"Activation inspection issue '{issue.Kind}' with severity '{issue.Severity}': {issue.Reason}",
                                Locations = issue.Location is null
                                    ? [.. locations]
                                    : [issue.Location]
                            }))
                        .ToList(),
                    Classification = new MigrationGuidanceClassification
                    {
                        Status = activationAnalysis.Issues.Count == 0
                            ? "confirmed"
                            : "ambiguous",
                        Summary =
                            "Manifest declarations are confirmed source facts; desktop declaration support and lifecycle routing require separate validation.",
                        Candidates =
                        [
                            new()
                            {
                                Type = "mechanically-migrated-declaration",
                                Confidence = activationAnalysis.Status == "passed"
                                    ? "high"
                                    : "medium",
                                Reason =
                                    "Supported protocol and file-association declarations may already be present in the target manifest."
                            },
                            new()
                            {
                                Type = "semantic-activation-routing",
                                Confidence = "high",
                                Reason =
                                    "Runtime activation routing remains application-owned even when the declaration migrates mechanically."
                            }
                        ],
                        DiscriminatingTests =
                        [
                            "Compare each source capability/extension with the target manifest declaration and desktop support contract.",
                            "Invoke every declared activation category and verify target routing, payload handling, and visible state."
                        ]
                    },
                    CandidateStrategiesExhaustive = false,
                    MechanicallyAppliedTransforms =
                    [
                        new()
                        {
                            Id = "UWMIG-ACTIVATION-MANIFEST",
                            Summary =
                                "Supported deterministic activation declarations were migrated before semantic review."
                        }
                    ],
                    CandidateStrategies =
                    [
                        new()
                        {
                            Id = "retain-supported-desktop-declaration",
                            Summary =
                                "Retain the mechanically migrated declaration when Windows App SDK desktop support and source semantics match.",
                            Actions =
                            [
                                "Verify the target manifest schema and declaration payload.",
                                "Connect the activation callback to the target-owned startup/window routing path."
                            ],
                            Preconditions =
                            [
                                "The declaration category is supported for the selected deployment model.",
                                "The source activation payload contract is known."
                            ],
                            Risks =
                            [
                                "A valid declaration without runtime routing can launch the app but lose the activation payload."
                            ]
                        },
                        new()
                        {
                            Id = "implement-desktop-equivalent-contract",
                            Summary =
                                "Use an explicit desktop capability, registration, or application-owned equivalent when the UWP declaration is not directly portable.",
                            Actions =
                            [
                                "Identify the desktop authority and lifecycle contract for the feature.",
                                "Implement registration and runtime handling in the target solution."
                            ],
                            Preconditions =
                            [
                                "Authoritative platform evidence identifies the required desktop contract."
                            ],
                            Risks =
                            [
                                "Inventing manifest namespaces or interop contracts can create declarations that package but never work."
                            ]
                        }
                    ],
                    ForbiddenShortcuts =
                    [
                        "Do not copy the UWP manifest over the WinUI package manifest.",
                        "Do not invent unsupported manifest namespaces or interop interfaces.",
                        "Do not mark activation complete after declaration migration without replaying the activation payload."
                    ],
                    CompletionConditions =
                    [
                        "Every source capability and extension has a target declaration, explicit equivalent, or evidence-backed exclusion.",
                        "Every activation category routes its payload to the intended target state.",
                        "Target manifest verification and activation replay pass."
                    ],
                    CompletionChecks =
                    [
                        new()
                        {
                            Id = "activation-contracts-verified",
                            Summary =
                                "All mechanically migrated activation contracts remain verified.",
                            VerificationKind = "report-field",
                            Target = "mechanicalVerification.activationContracts.reviewRequiredContracts",
                            Expected = "0"
                        },
                        new()
                        {
                            Id = "activation-state-replay",
                            Summary =
                                "Each affected activation state passes canonical replay.",
                            VerificationKind = "state-set",
                            Target = "affectedStates",
                            Expected = "all-passed"
                        }
                    ]
                }
            };
        }
    }
}
