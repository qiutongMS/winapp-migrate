// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using UwpMigration.Contracts;

namespace UwpMigration.Mechanical.Tests;

[TestClass]
public class MigrationGuidanceContractTests
{
    [TestMethod]
    public void Guidance_RoundTripsThroughSourceGeneratedJson()
    {
        var report = new MigrationReport
        {
            Source = new MigrationProject { Root = "source" },
            Target = new MigrationProject { Root = "target" },
            Todos =
            [
                new()
                {
                    Id = "UWMIG999",
                    Category = "test",
                    Priority = "required",
                    Summary = "Repair a deterministic condition.",
                    Reason = "A deterministic fact requires semantic repair.",
                    Locations = [new() { Path = "View.xaml", Line = 12 }],
                    Guidance = new MigrationRepairGuidance
                    {
                        ReasonCode = "test-reason",
                        Facts =
                        [
                            new()
                            {
                                Id = "test-fact",
                                Summary = "The target contains the test condition.",
                                Locations = [new() { Path = "Selector.cs", Line = 8 }]
                            }
                        ],
                        MechanicallyAppliedTransforms =
                        [
                            new()
                            {
                                Id = "TEST-TRANSFORM",
                                Summary = "Applied the safe portion."
                            }
                        ],
                        CandidateStrategies =
                        [
                            new()
                            {
                                Id = "test-strategy",
                                Summary = "Use the explicit target contract.",
                                Actions = ["Apply the target contract."],
                                Preconditions = ["The owner is known."],
                                Risks = ["The wrong owner changes behavior."]
                            }
                        ],
                        ForbiddenShortcuts = ["Do not guess the owner."],
                        CompletionConditions = ["The behavior is verified."]
                    }
                }
            ]
        };

        var json = JsonSerializer.Serialize(
            report,
            MigrateJsonContext.Default.MigrationReport);
        var roundTripped = JsonSerializer.Deserialize(
            json,
            MigrateJsonContext.Default.MigrationReport)!;

        Assert.AreEqual("1.5", roundTripped.SchemaVersion);
        var guidance = roundTripped.Todos.Single().Guidance!;
        Assert.AreEqual("test-reason", guidance.ReasonCode);
        Assert.AreEqual("test-fact", guidance.Facts.Single().Id);
        Assert.AreEqual(8, guidance.Facts.Single().Locations.Single().Line);
        Assert.AreEqual(
            "TEST-TRANSFORM",
            guidance.MechanicallyAppliedTransforms.Single().Id);
        Assert.AreEqual(
            "test-strategy",
            guidance.CandidateStrategies.Single().Id);
        CollectionAssert.AreEqual(
            new[] { "Do not guess the owner." },
            guidance.ForbiddenShortcuts);
        CollectionAssert.AreEqual(
            new[] { "The behavior is verified." },
            guidance.CompletionConditions);
    }

    [TestMethod]
    public void Schema14Todo_DeserializesWithoutGuidance()
    {
        const string json = """
            {
              "schemaVersion": "1.4",
              "source": { "root": "source" },
              "target": { "root": "target" },
              "todos": [
                {
                  "id": "UWMIG005",
                  "category": "dispatcher",
                  "priority": "required",
                  "status": "accepted",
                  "summary": "Preserved summary",
                  "reason": "Preserved reason",
                  "locations": [{ "path": "Page.xaml.cs", "line": 21 }]
                }
              ]
            }
            """;

        var report = JsonSerializer.Deserialize(
            json,
            MigrateJsonContext.Default.MigrationReport)!;

        Assert.AreEqual("1.4", report.SchemaVersion);
        var todo = report.Todos.Single();
        Assert.AreEqual("accepted", todo.Status);
        Assert.AreEqual("Preserved reason", todo.Reason);
        Assert.AreEqual(21, todo.Locations.Single().Line);
        Assert.IsNull(todo.Guidance);
    }
}
