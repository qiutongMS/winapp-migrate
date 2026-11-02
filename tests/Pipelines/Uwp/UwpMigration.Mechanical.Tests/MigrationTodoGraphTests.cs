// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Contracts;
using UwpMigration.Mechanical;

namespace UwpMigration.Mechanical.Tests;

[TestClass]
public sealed class MigrationTodoGraphTests
{
    [TestMethod]
    public void Build_CreatesDeterministicTopologicalWaves()
    {
        var todos = new List<MigrationTodo>
        {
            Todo("UWMIG001", ["UWMIG003"]),
            Todo("UWMIG002", ["UWMIG001"]),
            Todo("UWMIG003", []),
            Todo("UWMIG007", [])
        };

        var graph = MigrationTodoGraphBuilder.Build(todos);

        Assert.AreEqual("valid", graph.Status);
        Assert.HasCount(3, graph.ExecutionWaves);
        CollectionAssert.AreEqual(
            new[] { "UWMIG003", "UWMIG007" },
            graph.ExecutionWaves[0].TodoIds);
        CollectionAssert.AreEqual(
            new[] { "UWMIG001" },
            graph.ExecutionWaves[1].TodoIds);
        CollectionAssert.AreEqual(
            new[] { "UWMIG002" },
            graph.ExecutionWaves[2].TodoIds);
        Assert.HasCount(2, graph.Edges);
    }

    [TestMethod]
    public void Build_UnknownDependencyIsSurfacedWithoutWaves()
    {
        var graph = MigrationTodoGraphBuilder.Build(
            [Todo("UWMIG001", ["UWMIG999"])]);

        Assert.AreEqual("invalid", graph.Status);
        Assert.IsEmpty(graph.ExecutionWaves);
        Assert.AreEqual(
            "unknown-dependency",
            graph.Issues.Single().Code);
    }

    [TestMethod]
    public void Build_CycleIsSurfacedWithoutFlattening()
    {
        var graph = MigrationTodoGraphBuilder.Build(
            [
                Todo("UWMIG001", ["UWMIG002"]),
                Todo("UWMIG002", ["UWMIG001"])
            ]);

        Assert.AreEqual("invalid", graph.Status);
        Assert.IsEmpty(graph.ExecutionWaves);
        Assert.AreEqual(
            "dependency-cycle",
            graph.Issues.Single().Code);
    }

    [TestMethod]
    public void Build_BroadTodoGetsStablePathWorkSlicesAndStructuredFallback()
    {
        var locations = Enumerable.Range(1, 12)
            .Select(index => new MigrationLocation
            {
                Path = index <= 6 ? "Views/A.xaml.cs" : "Views/B.xaml.cs",
                Line = index
            })
            .ToList();
        var todo = Todo("UWMIG007", []);
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
        Assert.IsNotNull(todo.Guidance);
        Assert.IsFalse(todo.Guidance.CandidateStrategiesExhaustive);
        Assert.IsNotEmpty(todo.Guidance.Facts);
        Assert.IsNotEmpty(todo.Guidance.CandidateStrategies);
        Assert.IsNotEmpty(todo.Guidance.CompletionChecks);
        Assert.IsNotEmpty(todo.TouchSet.ConflictKeys);
        Assert.IsTrue(todo.WorkSlices
            .SelectMany(slice => slice.TouchSet.ConflictKeys)
            .All(key => key.StartsWith("path:", StringComparison.Ordinal)));
        Assert.AreEqual(
            todo.WorkSlices.Count,
            todo.WorkSlices
                .SelectMany(slice => slice.TouchSet.ConflictKeys)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    private static MigrationTodo Todo(
        string id,
        List<string> dependencies) =>
        new()
        {
            Id = id,
            Category = "test",
            Priority = "required",
            Summary = $"Resolve {id}.",
            Reason = $"{id} was detected.",
            DependsOn = dependencies
        };
}
