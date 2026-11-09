// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Contracts;

namespace UwpMigration.Mechanical.Tests;

[TestClass]
public sealed class MigrationTodoValidationTests
{
    [TestMethod]
    public void NormalizeForWrite_PreservesCanonicalTodoDependencies()
    {
        var prerequisite = Todo("UWMIG001");
        var dependent = Todo("UWMIG002");
        dependent.DependsOn = ["UWMIG001", "UWMIG001"];
        var report = Report(prerequisite, dependent);

        MigrationReportStore.NormalizeForWrite(report);

        CollectionAssert.AreEqual(
            new[] { "UWMIG001" },
            dependent.DependsOn);
        Assert.IsNotNull(dependent.Guidance);
        Assert.IsNotEmpty(dependent.Guidance.Facts);
        Assert.IsNotEmpty(dependent.Guidance.CompletionChecks);
        CollectionAssert.Contains(
            dependent.AffectedFeatures,
            dependent.Category);
        CollectionAssert.Contains(
            dependent.TouchSet.ConflictKeys,
            $"category:{dependent.Category}");
    }

    [TestMethod]
    public void NormalizeForWrite_RejectsUnknownDependency()
    {
        var todo = Todo("UWMIG001");
        todo.DependsOn = ["UWMIG999"];

        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            MigrationReportStore.NormalizeForWrite(Report(todo)));

        StringAssert.Contains(exception.Message, "[unknown-dependency]");
        StringAssert.Contains(exception.Message, "UWMIG999");
    }

    [TestMethod]
    public void NormalizeForWrite_RejectsSelfDependency()
    {
        var todo = Todo("UWMIG001");
        todo.DependsOn = ["UWMIG001"];

        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            MigrationReportStore.NormalizeForWrite(Report(todo)));

        StringAssert.Contains(exception.Message, "[self-dependency]");
    }

    [TestMethod]
    public void NormalizeForWrite_RejectsDependencyCycle()
    {
        var first = Todo("UWMIG001");
        first.DependsOn = ["UWMIG002"];
        var second = Todo("UWMIG002");
        second.DependsOn = ["UWMIG001"];

        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            MigrationReportStore.NormalizeForWrite(Report(first, second)));

        StringAssert.Contains(exception.Message, "[dependency-cycle]");
    }

    [TestMethod]
    public void NormalizeForWrite_RejectsInvalidRawNodes()
    {
        var invalidReferences = Todo("UWMIG001");
        invalidReferences.DependsOn = null!;
        var invalidLocation = Todo("UWMIG002");
        invalidLocation.Locations = [null!];
        var invalidTouchSet = Todo("UWMIG003");
        invalidTouchSet.TouchSet = null!;

        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            MigrationReportStore.NormalizeForWrite(Report(
                null!,
                invalidReferences,
                invalidLocation,
                invalidTouchSet)));

        StringAssert.Contains(exception.Message, "[invalid-todo-node]");
        StringAssert.Contains(
            exception.Message,
            "[invalid-todo-dependency-reference]");
        StringAssert.Contains(exception.Message, "[invalid-location-node]");
        StringAssert.Contains(exception.Message, "[invalid-touch-set]");
    }

    [TestMethod]
    public void NormalizeForWrite_RejectsInvalidClassification()
    {
        var todo = Todo("UWMIG001");
        todo.Guidance = new MigrationRepairGuidance
        {
            ReasonCode = "test",
            Classification = new MigrationGuidanceClassification
            {
                Status = "guessed",
                Summary = "Invalid classification.",
                Candidates =
                [
                    new()
                    {
                        Type = "test",
                        Confidence = "certain",
                        Reason = "Invalid confidence."
                    }
                ]
            }
        };

        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            MigrationReportStore.NormalizeForWrite(Report(todo)));

        StringAssert.Contains(
            exception.Message,
            "[invalid-classification-status]");
        StringAssert.Contains(
            exception.Message,
            "[invalid-classification-confidence]");
    }

    [TestMethod]
    public void NormalizeForWrite_CompletesPartialLegacyGuidance()
    {
        var todo = Todo("UWMIG001");
        todo.Guidance = new MigrationRepairGuidance
        {
            ReasonCode = "legacy-reason"
        };

        MigrationReportStore.NormalizeForWrite(Report(todo));

        Assert.AreEqual("legacy-reason", todo.Guidance.ReasonCode);
        Assert.IsNotEmpty(todo.Guidance.Facts);
        Assert.AreEqual(
            "provisional",
            todo.Guidance.Classification?.Status);
        Assert.IsNotEmpty(todo.Guidance.CandidateStrategies);
        Assert.IsNotEmpty(todo.Guidance.ForbiddenShortcuts);
        Assert.IsNotEmpty(todo.Guidance.CompletionConditions);
        Assert.IsNotEmpty(todo.Guidance.CompletionChecks);
    }

    private static MigrationReport Report(params MigrationTodo[] todos) =>
        new()
        {
            Source = new MigrationProject { Root = "source" },
            Target = new MigrationProject { Root = "target" },
            Todos = [.. todos]
        };

    private static MigrationTodo Todo(string id) =>
        new()
        {
            Id = id,
            Category = "test",
            Priority = "required",
            Summary = $"Resolve {id}.",
            Reason = $"{id} was detected."
        };
}
