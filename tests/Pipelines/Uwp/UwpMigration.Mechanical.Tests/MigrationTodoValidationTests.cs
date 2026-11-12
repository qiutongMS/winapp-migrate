// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Contracts;

namespace UwpMigration.Mechanical.Tests;

[TestClass]
public sealed class MigrationTodoValidationTests
{
    [TestMethod]
    public void NormalizeForWrite_CompletesMechanicalTodoContract()
    {
        var todo = Todo("UWMIG001");
        var report = Report(todo);

        MigrationReportStore.NormalizeForWrite(report);

        Assert.AreEqual("mechanical", todo.Origin);
        Assert.IsNull(todo.Discovery);
        Assert.IsNotNull(todo.Guidance);
        Assert.IsNotEmpty(todo.Guidance.Facts);
        Assert.IsNotEmpty(todo.Guidance.CompletionChecks);
        CollectionAssert.Contains(
            todo.AffectedFeatures,
            todo.Category);
        CollectionAssert.Contains(
            todo.TouchSet.ConflictKeys,
            $"category:{todo.Category}");
    }

    [TestMethod]
    public void NormalizeForWrite_AcceptsDiscoveredTodoWithEvidence()
    {
        var todo = Todo("UWDISC001");
        todo.Origin = "discovered";
        todo.Discovery = new MigrationTodoDiscovery
        {
            Phase = "runtime",
            Signature = "startup-null-reference",
            Evidence = ["runtime/startup.json"]
        };

        MigrationReportStore.NormalizeForWrite(Report(todo));

        Assert.AreEqual("discovered", todo.Origin);
        Assert.AreEqual("runtime", todo.Discovery.Phase);
    }

    [TestMethod]
    public void NormalizeForWrite_RejectsUnknownOrigin()
    {
        var todo = Todo("UWMIG001");
        todo.Origin = "generated";

        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            MigrationReportStore.NormalizeForWrite(Report(todo)));

        StringAssert.Contains(exception.Message, "[invalid-todo-origin]");
    }

    [TestMethod]
    public void NormalizeForWrite_RejectsIncompleteDiscovery()
    {
        var todo = Todo("UWDISC001");
        todo.Origin = "discovered";
        todo.Discovery = new MigrationTodoDiscovery
        {
            Phase = "runtime",
            Signature = "",
            Evidence = []
        };

        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            MigrationReportStore.NormalizeForWrite(Report(todo)));

        StringAssert.Contains(exception.Message, "[invalid-todo-discovery]");
    }

    [TestMethod]
    public void NormalizeForWrite_RejectsInvalidRawNodes()
    {
        var invalidLocation = Todo("UWMIG002");
        invalidLocation.Locations = [null!];
        var invalidTouchSet = Todo("UWMIG003");
        invalidTouchSet.TouchSet = null!;

        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            MigrationReportStore.NormalizeForWrite(Report(
                null!,
                invalidLocation,
                invalidTouchSet)));

        StringAssert.Contains(exception.Message, "[invalid-todo-node]");
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
    public void NormalizeForWrite_CompletesCurrentMechanicalGuidance()
    {
        var todo = Todo("UWMIG001");
        todo.Guidance = new MigrationRepairGuidance
        {
            ReasonCode = "mechanical-reason"
        };

        MigrationReportStore.NormalizeForWrite(Report(todo));

        Assert.AreEqual("mechanical-reason", todo.Guidance.ReasonCode);
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
