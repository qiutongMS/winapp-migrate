// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Contracts;

namespace UwpMigration.Mechanical;

internal static class MigrationTodoWorkPlanFactory
{
    internal static List<MigrationTodoWorkSlice> AppResources(
        IReadOnlyList<MigrationLocation> locations) =>
        [
            new()
            {
                Id = "UWMIG001:prepare-startup-resources",
                Summary =
                    "Prepare the application resource and startup patch from preserved UWP App files.",
                Stage = "preparation",
                Locations = [.. locations],
                TouchSet = StartupTouchSet()
            },
            new()
            {
                Id = "UWMIG001:integrate-startup-resources",
                Summary =
                    "Integrate the prepared resource and startup patch into the target-owned bootstrap.",
                Stage = "integration",
                Dependencies =
                [
                    new()
                    {
                        WorkItemId =
                            "UWMIG001:prepare-startup-resources",
                        Kind = "integration"
                    }
                ],
                Locations = [.. locations],
                TouchSet = StartupTouchSet()
            }
        ];

    internal static List<MigrationTodoWorkSlice> Manifest(
        IReadOnlyList<MigrationLocation> locations) =>
        [
            new()
            {
                Id = "UWMIG002:prepare-manifest-declarations",
                Summary =
                    "Prepare capability and activation declaration changes for Package.appxmanifest.",
                Stage = "preparation",
                Locations = [.. locations],
                TouchSet = new MigrationTodoTouchSet
                {
                    Paths = ["Package.appxmanifest"],
                    ConflictKeys = ["manifest-declarations"]
                }
            },
            new()
            {
                Id = "UWMIG002:integrate-manifest-declarations",
                Summary =
                    "Integrate reviewed capability and activation declarations into Package.appxmanifest.",
                Stage = "integration",
                Dependencies =
                [
                    new()
                    {
                        WorkItemId =
                            "UWMIG002:prepare-manifest-declarations",
                        Kind = "integration"
                    }
                ],
                Locations = [.. locations],
                TouchSet = new MigrationTodoTouchSet
                {
                    Paths = ["Package.appxmanifest"],
                    ConflictKeys = ["manifest-declarations"]
                }
            },
            new()
            {
                Id = "UWMIG002:integrate-activation-routing",
                Summary =
                    "Integrate activation payload routing into the target-owned App.xaml.cs startup path.",
                Stage = "integration",
                Dependencies =
                [
                    new()
                    {
                        WorkItemId =
                            "UWMIG002:prepare-manifest-declarations",
                        Kind = "integration"
                    }
                ],
                Locations = [.. locations],
                TouchSet = new MigrationTodoTouchSet
                {
                    Paths = ["App.xaml.cs"],
                    ConflictKeys =
                    [
                        "activation-routing",
                        "startup-bootstrap"
                    ]
                }
            }
        ];

    internal static List<MigrationTodoWorkSlice> Dependencies(
        IReadOnlyList<MigrationLocation> locations) =>
        [
            new()
            {
                Id = "UWMIG003:prepare-dependencies",
                Summary =
                    "Prepare dependency compatibility decisions and the target project patch.",
                Stage = "preparation",
                Locations = [.. locations],
                TouchSet = DependencyPreparationTouchSet()
            },
            new()
            {
                Id = "UWMIG003:integrate-dependencies",
                Summary =
                    "Integrate approved dependency and project-graph changes.",
                Stage = "integration",
                Dependencies =
                [
                    new()
                    {
                        WorkItemId = "UWMIG003:prepare-dependencies",
                        Kind = "integration"
                    }
                ],
                Locations = [.. locations],
                TouchSet = DependencyIntegrationTouchSet()
            },
            IntegratedVerification(
                "UWMIG003:verify-integrated-target",
                locations)
        ];

    internal static MigrationTodoWorkSlice IntegratedVerification(
        string id,
        IReadOnlyList<MigrationLocation> locations) =>
        new()
        {
            Id = id,
            Summary =
                "Run the single integrated build and behavior verification plan after priority integrations.",
            Stage = "verification",
            Locations = [.. locations],
            TouchSet = new MigrationTodoTouchSet
            {
                ConflictKeys =
                [
                    "integrated-target-verification",
                    "target-build"
                ]
            }
        };

    private static MigrationTodoTouchSet StartupTouchSet() =>
        new()
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
        };

    private static MigrationTodoTouchSet DependencyPreparationTouchSet() =>
        new()
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
            ConflictKeys = ["dependency-contract"]
        };

    private static MigrationTodoTouchSet DependencyIntegrationTouchSet()
    {
        var touchSet = DependencyPreparationTouchSet();
        touchSet.ConflictKeys.Add("target-project-graph");
        return touchSet;
    }
}
