// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Contracts;
using UwpMigration.Mechanical;

namespace UwpMigration.Mechanical.Tests;

[TestClass]
public sealed class MigrationTodoGraphTests
{
    [TestMethod]
    public void Build_SchedulesPriorityWorkItemsByStageAndConflict()
    {
        var startup = Todo("UWMIG001");
        startup.WorkSlices =
        [
            Slice(
                "UWMIG001:prepare-startup-resources",
                "preparation",
                ["App.xaml", "App.xaml.cs"],
                ["startup-bootstrap"]),
            Slice(
                "UWMIG001:integrate-startup-resources",
                "integration",
                ["App.xaml", "App.xaml.cs"],
                ["startup-bootstrap"],
                "UWMIG001:prepare-startup-resources")
        ];
        var manifest = Todo("UWMIG002");
        manifest.WorkSlices =
        [
            Slice(
                "UWMIG002:prepare-manifest-declarations",
                "preparation",
                ["Package.appxmanifest"],
                ["manifest-declarations"]),
            Slice(
                "UWMIG002:integrate-manifest-declarations",
                "integration",
                ["Package.appxmanifest"],
                ["manifest-declarations"],
                "UWMIG002:prepare-manifest-declarations"),
            Slice(
                "UWMIG002:integrate-activation-routing",
                "integration",
                ["App.xaml.cs"],
                ["startup-bootstrap"],
                "UWMIG002:prepare-manifest-declarations")
        ];
        var dependencies = Todo("UWMIG003");
        dependencies.WorkSlices =
        [
            Slice(
                "UWMIG003:prepare-dependencies",
                "preparation",
                ["App.csproj"],
                ["dependency-contract"]),
            Slice(
                "UWMIG003:integrate-dependencies",
                "integration",
                ["App.csproj"],
                ["dependency-contract"],
                "UWMIG003:prepare-dependencies"),
            Slice(
                "UWMIG003:verify-integrated-target",
                "verification",
                [],
                [
                    "integrated-target-verification",
                    "target-build"
                ],
                "UWMIG003:integrate-dependencies")
        ];

        var graph = MigrationTodoGraphBuilder.Build(
            [startup, manifest, dependencies]);

        Assert.AreEqual("valid", graph.Status);
        Assert.HasCount(4, graph.ExecutionWaves);
        AssertWave(
            graph.ExecutionWaves[0],
            "preparation",
            "UWMIG001:prepare-startup-resources",
            "UWMIG002:prepare-manifest-declarations",
            "UWMIG003:prepare-dependencies");
        AssertWave(
            graph.ExecutionWaves[1],
            "integration",
            "UWMIG001:integrate-startup-resources",
            "UWMIG002:integrate-manifest-declarations",
            "UWMIG003:integrate-dependencies");
        AssertWave(
            graph.ExecutionWaves[2],
            "integration",
            "UWMIG002:integrate-activation-routing");
        AssertWave(
            graph.ExecutionWaves[3],
            "verification",
            "UWMIG003:verify-integrated-target");
        Assert.IsTrue(graph.Edges.Any(edge =>
            edge.PrerequisiteId
                == "UWMIG002:integrate-activation-routing"
            && edge.DependentId
                == "UWMIG003:verify-integrated-target"
            && edge.Kind == "verification"));
    }

    [TestMethod]
    public void Build_BroadTodoPathSlicesAreParallelWorkItems()
    {
        var locations = Enumerable.Range(1, 12)
            .Select(index => new MigrationLocation
            {
                Path = index <= 6 ? "Views/A.xaml.cs" : "Views/B.xaml.cs",
                Line = index
            })
            .ToList();
        var todo = Todo("UWMIG007");
        todo.Category = "windowing";
        todo.Locations = locations;

        var first = MigrationTodoGraphBuilder.Build([todo]);
        var sliceIds = todo.WorkSlices.Select(slice => slice.Id).ToArray();
        var second = MigrationTodoGraphBuilder.Build([todo]);

        Assert.AreEqual("valid", first.Status);
        Assert.AreEqual("valid", second.Status);
        Assert.HasCount(2, todo.WorkSlices);
        CollectionAssert.AreEqual(
            sliceIds,
            todo.WorkSlices.Select(slice => slice.Id).ToArray());
        AssertWave(
            first.ExecutionWaves.Single(),
            "preparation",
            sliceIds);
        Assert.IsTrue(todo.WorkSlices
            .SelectMany(slice => slice.TouchSet.ConflictKeys)
            .All(key => key.StartsWith("path:", StringComparison.Ordinal)));
        Assert.IsNotNull(todo.Guidance);
        Assert.IsNotEmpty(todo.Guidance.Facts);
        Assert.IsNotEmpty(todo.Guidance.CompletionChecks);
    }

    [TestMethod]
    public void Build_ConflictingSlicesNeverShareWave()
    {
        var todo = Todo("UWMIG002");
        todo.WorkSlices =
        [
            Slice("UWMIG002:first", "preparation", ["App.xaml.cs"], []),
            Slice("UWMIG002:second", "preparation", ["App.xaml.cs"], [])
        ];

        var graph = MigrationTodoGraphBuilder.Build([todo]);

        Assert.AreEqual("valid", graph.Status);
        Assert.HasCount(2, graph.ExecutionWaves);
        Assert.IsTrue(graph.ExecutionWaves.All(wave =>
            wave.WorkItemIds.Count == 1));
    }

    [TestMethod]
    public void Build_ProjectItemAndConflictKeyCollisionsAreSerialized()
    {
        var todo = Todo("UWMIG003");
        todo.WorkSlices =
        [
            SliceWithTouch(
                "UWMIG003:a-project-item",
                projectItems: ["PackageReference"]),
            SliceWithTouch(
                "UWMIG003:b-project-item",
                projectItems: ["PackageReference"]),
            SliceWithTouch(
                "UWMIG003:c-conflict-key",
                conflictKeys: ["target-project-graph"]),
            SliceWithTouch(
                "UWMIG003:d-conflict-key",
                conflictKeys: ["target-project-graph"])
        ];

        var graph = MigrationTodoGraphBuilder.Build([todo]);

        Assert.AreEqual("valid", graph.Status);
        Assert.HasCount(2, graph.ExecutionWaves);
        AssertWave(
            graph.ExecutionWaves[0],
            "preparation",
            "UWMIG003:a-project-item",
            "UWMIG003:c-conflict-key");
        AssertWave(
            graph.ExecutionWaves[1],
            "preparation",
            "UWMIG003:b-project-item",
            "UWMIG003:d-conflict-key");
    }

    [TestMethod]
    public void Build_IntersectingWildcardPathsAreSerialized()
    {
        var todo = Todo("UWMIG090");
        todo.WorkSlices =
        [
            Slice(
                "UWMIG090:left",
                "preparation",
                [@"SRC\A*.CSPROJ"],
                []),
            Slice(
                "UWMIG090:right",
                "preparation",
                ["src/*b.csproj"],
                [])
        ];

        var graph = MigrationTodoGraphBuilder.Build([todo]);

        Assert.AreEqual("valid", graph.Status);
        Assert.HasCount(2, graph.ExecutionWaves);
        Assert.IsTrue(graph.ExecutionWaves.All(wave =>
            wave.WorkItemIds.Count == 1));
    }

    [TestMethod]
    public void Build_DisjointExactWindowsPathsRemainParallel()
    {
        var todo = Todo("UWMIG090");
        todo.WorkSlices =
        [
            Slice(
                "UWMIG090:left",
                "preparation",
                [@"src\A.csproj"],
                []),
            Slice(
                "UWMIG090:right",
                "preparation",
                ["SRC/B.csproj"],
                [])
        ];

        var graph = MigrationTodoGraphBuilder.Build([todo]);

        Assert.AreEqual("valid", graph.Status);
        AssertWave(
            graph.ExecutionWaves.Single(),
            "preparation",
            "UWMIG090:left",
            "UWMIG090:right");
    }

    [TestMethod]
    public void Build_WindowsPathAliasesAndGlobAliasesAreSerialized()
    {
        var todo = Todo("UWMIG090");
        todo.WorkSlices =
        [
            Slice(
                "UWMIG090:exact-alias",
                "preparation",
                [@"src\feature\..\shared.cs"],
                []),
            Slice(
                "UWMIG090:exact-canonical",
                "preparation",
                ["SRC/shared.cs"],
                []),
            Slice(
                "UWMIG090:glob-alias",
                "preparation",
                [@"src\feature\..\A*.csproj"],
                []),
            Slice(
                "UWMIG090:glob-canonical",
                "preparation",
                ["SRC/A*.CSPROJ"],
                []),
            Slice(
                "UWMIG090:wildcard-navigation",
                "preparation",
                ["foo*/../bar.txt"],
                []),
            Slice(
                "UWMIG090:wildcard-canonical",
                "preparation",
                ["foox/bar.txt"],
                [])
        ];

        var graph = MigrationTodoGraphBuilder.Build([todo]);

        Assert.AreEqual("valid", graph.Status);
        Assert.AreNotEqual(
            WaveIndex(graph, "UWMIG090:exact-alias"),
            WaveIndex(graph, "UWMIG090:exact-canonical"));
        Assert.AreNotEqual(
            WaveIndex(graph, "UWMIG090:glob-alias"),
            WaveIndex(graph, "UWMIG090:glob-canonical"));
        Assert.AreNotEqual(
            WaveIndex(graph, "UWMIG090:wildcard-navigation"),
            WaveIndex(graph, "UWMIG090:wildcard-canonical"));
    }

    [TestMethod]
    public void Build_UnknownWorkItemDependencyIsSurfacedWithoutWaves()
    {
        var todo = Todo("UWMIG099");
        todo.WorkSlices =
        [
            Slice(
                "UWMIG099:custom",
                "integration",
                ["App.xaml"],
                [],
                "UWMIG999:missing")
        ];

        var graph = MigrationTodoGraphBuilder.Build([todo]);

        Assert.AreEqual("invalid", graph.Status);
        Assert.IsEmpty(graph.ExecutionWaves);
        Assert.AreEqual(
            "unknown-work-item-dependency",
            graph.Issues.Single().Code);
    }

    [TestMethod]
    public void Build_InvalidRawNodesAndReferencesAreRejectedBeforeAugmentation()
    {
        var nullId = Todo("temporary-null");
        nullId.Id = null!;
        var whitespaceId = Todo("temporary-whitespace");
        whitespaceId.Id = " ";
        var invalidTodoReference = Todo("UWMIG090");
        invalidTodoReference.DependsOn = [" "];
        var invalidSlices = Todo("UWMIG091");
        var invalidDependency = Slice(
            "UWMIG091:dependency",
            "integration",
            [],
            [],
            "UWMIG091:prepare");
        invalidDependency.Dependencies.Single().WorkItemId = " ";
        invalidSlices.WorkSlices =
        [
            Slice(" ", "preparation", [], []),
            Slice("UWMIG091:stage", null!, [], []),
            invalidDependency,
            null!
        ];
        var invalidNestedShape = Todo("UWMIG092");
        invalidNestedShape.Locations = [null!];
        invalidNestedShape.TouchSet = null!;

        var graph = MigrationTodoGraphBuilder.Build(
            [
                null!,
                nullId,
                whitespaceId,
                invalidTodoReference,
                invalidSlices,
                invalidNestedShape
            ]);

        Assert.AreEqual("invalid", graph.Status);
        Assert.IsEmpty(graph.ExecutionWaves);
        CollectionAssert.IsSubsetOf(
            new[]
            {
                "invalid-todo-node",
                "invalid-todo-id",
                "invalid-todo-dependency-reference",
                "invalid-work-item-id",
                "invalid-work-item-stage",
                "invalid-work-item-dependency-reference",
                "invalid-work-item-node",
                "invalid-location-node",
                "invalid-touch-set"
            },
            graph.Issues.Select(issue => issue.Code).ToArray());
    }

    [TestMethod]
    public void NormalizeForWrite_PreservesTypedIssuesForNullRawNodes()
    {
        var invalidReferences = Todo("UWMIG090");
        invalidReferences.DependsOn = null!;
        var invalidNestedShape = Todo("UWMIG091");
        invalidNestedShape.Locations = [null!];
        var report = new MigrationReport
        {
            Source = new MigrationProject { Root = "source" },
            Target = new MigrationProject { Root = "target" },
            Todos = [null!, invalidReferences, invalidNestedShape]
        };

        MigrationReportStore.NormalizeForWrite(report);

        Assert.AreEqual("invalid", report.TodoGraph.Status);
        Assert.IsEmpty(report.TodoGraph.ExecutionWaves);
        Assert.IsTrue(report.TodoGraph.Issues.Any(issue =>
            issue.Code == "invalid-todo-node"));
        Assert.IsTrue(report.TodoGraph.Issues.Any(issue =>
            issue.Code == "invalid-todo-dependency-reference"));
        Assert.IsTrue(report.TodoGraph.Issues.Any(issue =>
            issue.Code == "invalid-location-node"));
    }

    [TestMethod]
    public void Build_InvalidLegacyPriorityDependenciesAreNotDiscarded()
    {
        var unknown = Todo("UWMIG001");
        unknown.WorkSlices =
        [
            Slice("UWMIG001:work", "preparation", [], [])
        ];
        unknown.DependsOn = ["UWMIG999"];
        var unknownGraph = MigrationTodoGraphBuilder.Build([unknown]);
        Assert.AreEqual("invalid", unknownGraph.Status);
        Assert.IsEmpty(unknownGraph.ExecutionWaves);
        Assert.AreEqual("UWMIG999", unknown.DependsOn.Single());
        Assert.IsTrue(unknownGraph.Issues.Any(issue =>
            issue.Code == "unknown-dependency"));

        var self = Todo("UWMIG002");
        self.WorkSlices =
        [
            Slice("UWMIG002:work", "preparation", [], [])
        ];
        self.DependsOn = ["UWMIG002"];
        var selfGraph = MigrationTodoGraphBuilder.Build([self]);
        Assert.AreEqual("invalid", selfGraph.Status);
        Assert.IsEmpty(selfGraph.ExecutionWaves);
        Assert.IsTrue(selfGraph.Issues.Any(issue =>
            issue.Code == "self-dependency"));

        var first = Todo("UWMIG001");
        first.WorkSlices =
        [
            Slice("UWMIG001:work", "preparation", [], [])
        ];
        first.DependsOn = ["UWMIG003"];
        var second = Todo("UWMIG003");
        second.WorkSlices =
        [
            Slice("UWMIG003:work", "preparation", [], [])
        ];
        second.DependsOn = ["UWMIG001"];
        var cycleGraph = MigrationTodoGraphBuilder.Build([first, second]);
        Assert.AreEqual("invalid", cycleGraph.Status);
        Assert.IsEmpty(cycleGraph.ExecutionWaves);
        Assert.IsTrue(cycleGraph.Issues.Any(issue =>
            issue.Code == "legacy-dependency-cycle"));
    }

    [TestMethod]
    public void Build_ValidLegacyPriorityDependencyKeepsPreparationParallel()
    {
        var startup = Todo("UWMIG001");
        startup.WorkSlices =
        [
            Slice("UWMIG001:work", "preparation", [], [])
        ];
        startup.DependsOn = ["UWMIG003"];
        var dependencies = Todo("UWMIG003");
        dependencies.WorkSlices =
        [
            Slice("UWMIG003:work", "preparation", [], [])
        ];

        var graph = MigrationTodoGraphBuilder.Build(
            [startup, dependencies]);

        Assert.AreEqual("valid", graph.Status);
        CollectionAssert.IsSubsetOf(
            new[]
            {
                "UWMIG001:prepare-startup-resources",
                "UWMIG003:prepare-dependencies"
            },
            graph.ExecutionWaves[0].WorkItemIds);
        Assert.IsTrue(graph.Edges.Any(edge =>
            edge.PrerequisiteId == "UWMIG003:integrate-dependencies"
            && edge.DependentId
                == "UWMIG001:integrate-startup-resources"
            && edge.Kind == "integration"));
    }

    [TestMethod]
    public void Build_LegacyDependencyTargetsRealIntegrationBeforeVerification()
    {
        var prerequisite = Todo("UWMIG090");
        prerequisite.WorkSlices =
        [
            Slice("UWMIG090:prepare", "preparation", [], [])
        ];
        var dependent = Todo("UWMIG091");
        dependent.DependsOn = ["UWMIG090"];
        dependent.WorkSlices =
        [
            Slice("UWMIG091:integrate", "integration", [], [])
        ];

        var graph = MigrationTodoGraphBuilder.Build(
            [prerequisite, dependent]);

        Assert.AreEqual("valid", graph.Status);
        Assert.IsTrue(graph.Edges.Any(edge =>
            edge.PrerequisiteId == "UWMIG090:prepare"
            && edge.DependentId == "UWMIG091:integrate"
            && edge.Kind == "integration"));
        Assert.IsFalse(graph.Edges.Any(edge =>
            edge.PrerequisiteId == "UWMIG090:prepare"
            && edge.DependentId.EndsWith(
                ":verify-integrated-target",
                StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Build_LegacyGeneratedWorkItemDependenciesArePreserved()
    {
        var prerequisite = Todo("UWMIG090");
        prerequisite.WorkSlices =
        [
            Slice("UWMIG090:work", "preparation", [], [])
        ];
        var startup = Todo("UWMIG001");
        startup.WorkSlices =
        [
            Slice(
                "UWMIG001:work",
                "preparation",
                [],
                [],
                "UWMIG090:work")
        ];

        var graph = MigrationTodoGraphBuilder.Build(
            [prerequisite, startup]);

        Assert.AreEqual("valid", graph.Status);
        CollectionAssert.AreEqual(
            new[] { "UWMIG001:work" },
            startup.WorkSlices.Select(slice => slice.Id).ToArray());
        Assert.IsTrue(graph.Edges.Any(edge =>
            edge.PrerequisiteId == "UWMIG090:work"
            && edge.DependentId == "UWMIG001:work"
            && edge.Kind == "preparation"));
    }

    [TestMethod]
    public void Build_LegacyTodoDependencyPrefersIntegrationOverVerification()
    {
        var prerequisite = Todo("UWMIG090");
        prerequisite.WorkSlices =
        [
            Slice("UWMIG090:prepare", "preparation", [], []),
            Slice(
                "UWMIG090:integrate",
                "integration",
                [],
                [],
                "UWMIG090:prepare")
        ];
        var dependent = Todo("UWMIG091");
        dependent.DependsOn = ["UWMIG090"];
        dependent.WorkSlices =
        [
            Slice("UWMIG091:prepare", "preparation", [], []),
            Slice(
                "UWMIG091:integrate",
                "integration",
                [],
                [],
                "UWMIG091:prepare"),
            Slice(
                "UWMIG091:verify",
                "verification",
                [],
                [],
                "UWMIG091:integrate")
        ];

        var graph = MigrationTodoGraphBuilder.Build(
            [prerequisite, dependent]);

        Assert.AreEqual("valid", graph.Status);
        Assert.IsTrue(graph.Edges.Any(edge =>
            edge.PrerequisiteId == "UWMIG090:integrate"
            && edge.DependentId == "UWMIG091:integrate"
            && edge.Kind == "integration"));
        Assert.IsFalse(graph.Edges.Any(edge =>
            edge.PrerequisiteId == "UWMIG090:integrate"
            && edge.DependentId == "UWMIG091:verify"));
    }

    [TestMethod]
    public void Build_DuplicateWorkItemIdIsRejected()
    {
        var first = Todo("UWMIG001");
        first.WorkSlices = [Slice("shared:work", "preparation", [], [])];
        var second = Todo("UWMIG002");
        second.WorkSlices = [Slice("shared:work", "preparation", [], [])];

        var graph = MigrationTodoGraphBuilder.Build([first, second]);

        Assert.AreEqual("invalid", graph.Status);
        Assert.IsTrue(graph.Issues.Any(issue =>
            issue.Code == "duplicate-work-item-id"));
    }

    [TestMethod]
    public void Build_InvalidClassificationStatusAndConfidenceAreRejected()
    {
        var todo = Todo("UWMIG001");
        todo.Guidance!.Classification!.Status = "guessed";
        todo.Guidance.Classification.Candidates.Single().Confidence =
            "certain";

        var graph = MigrationTodoGraphBuilder.Build([todo]);

        Assert.AreEqual("invalid", graph.Status);
        CollectionAssert.IsSubsetOf(
            new[]
            {
                "invalid-classification-status",
                "invalid-classification-confidence"
            },
            graph.Issues.Select(issue => issue.Code).ToArray());
    }

    [TestMethod]
    public void Build_SelfAndCyclicDependenciesAreRejected()
    {
        var self = Todo("UWMIG001");
        self.WorkSlices =
        [
            Slice(
                "UWMIG001:self",
                "preparation",
                [],
                [],
                "UWMIG001:self")
        ];
        var selfGraph = MigrationTodoGraphBuilder.Build([self]);
        Assert.AreEqual("self-dependency", selfGraph.Issues.Single().Code);

        var cycle = Todo("UWMIG002");
        cycle.WorkSlices =
        [
            Slice(
                "UWMIG002:a",
                "preparation",
                [],
                [],
                "UWMIG002:b"),
            Slice(
                "UWMIG002:b",
                "preparation",
                [],
                [],
                "UWMIG002:a")
        ];
        var cycleGraph = MigrationTodoGraphBuilder.Build([cycle]);
        Assert.AreEqual("invalid", cycleGraph.Status);
        Assert.IsTrue(cycleGraph.Issues.Any(issue =>
            issue.Code == "dependency-cycle"));
    }

    [TestMethod]
    public void Build_LaterStagePrerequisiteIsUnschedulable()
    {
        var todo = Todo("UWMIG001");
        todo.WorkSlices =
        [
            Slice(
                "UWMIG001:prepare",
                "preparation",
                [],
                [],
                "UWMIG001:integrate"),
            Slice(
                "UWMIG001:integrate",
                "integration",
                [],
                [])
        ];

        var graph = MigrationTodoGraphBuilder.Build([todo]);

        Assert.AreEqual("invalid", graph.Status);
        Assert.IsTrue(graph.Issues.Any(issue =>
            issue.Code == "unschedulable-stage-dependency"));
    }

    [TestMethod]
    public void Build_DependencyKindMustMatchDependentStage()
    {
        var todo = Todo("UWMIG001");
        var integrate = Slice(
            "UWMIG001:integrate",
            "integration",
            [],
            [],
            "UWMIG001:prepare");
        integrate.Dependencies.Single().Kind = "verification";
        todo.WorkSlices =
        [
            Slice("UWMIG001:prepare", "preparation", [], []),
            integrate
        ];

        var graph = MigrationTodoGraphBuilder.Build([todo]);

        Assert.AreEqual("invalid", graph.Status);
        Assert.IsTrue(graph.Issues.Any(issue =>
            issue.Code == "invalid-dependency-kind"));
    }

    private static void AssertWave(
        MigrationTodoExecutionWave wave,
        string stage,
        params string[] workItemIds)
    {
        Assert.AreEqual(stage, wave.Stage);
        CollectionAssert.AreEqual(workItemIds, wave.WorkItemIds);
    }

    private static int WaveIndex(
        MigrationTodoGraph graph,
        string workItemId) =>
        graph.ExecutionWaves.Single(wave =>
            wave.WorkItemIds.Contains(
                workItemId,
                StringComparer.Ordinal)).Index;

    private static MigrationTodo Todo(string id) =>
        new()
        {
            Id = id,
            Category = "test",
            Priority = "required",
            Summary = $"Resolve {id}.",
            Reason = $"{id} was detected.",
            Guidance = new MigrationRepairGuidance
            {
                ReasonCode = "test",
                Classification = new MigrationGuidanceClassification
                {
                    Status = "provisional",
                    Summary = "Test classification.",
                    Candidates =
                    [
                        new()
                        {
                            Type = "test",
                            Confidence = "medium",
                            Reason = "Test candidate."
                        }
                    ]
                }
            }
        };

    private static MigrationTodoWorkSlice Slice(
        string id,
        string stage,
        List<string> paths,
        List<string> conflictKeys,
        string? dependency = null) =>
        new()
        {
            Id = id,
            Summary = id,
            Stage = stage,
            Dependencies = dependency is null
                ? []
                :
                [
                    new()
                    {
                        WorkItemId = dependency,
                        Kind = stage
                    }
                ],
            TouchSet = new MigrationTodoTouchSet
            {
                Paths = paths,
                ConflictKeys = conflictKeys
            }
        };

    private static MigrationTodoWorkSlice SliceWithTouch(
        string id,
        List<string>? projectItems = null,
        List<string>? conflictKeys = null) =>
        new()
        {
            Id = id,
            Summary = id,
            Stage = "preparation",
            TouchSet = new MigrationTodoTouchSet
            {
                ProjectItems = projectItems ?? [],
                ConflictKeys = conflictKeys ?? []
            }
        };
}
