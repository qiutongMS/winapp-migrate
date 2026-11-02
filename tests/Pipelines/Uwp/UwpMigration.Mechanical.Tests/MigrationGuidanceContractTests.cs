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
                    WorkSlices =
                    [
                        new()
                        {
                            Id = "UWMIG999:prepare",
                            Summary = "Prepare the repair.",
                            Stage = "preparation"
                        },
                        new()
                        {
                            Id = "UWMIG999:integrate",
                            Summary = "Integrate the repair.",
                            Stage = "integration",
                            Dependencies =
                            [
                                new()
                                {
                                    WorkItemId = "UWMIG999:prepare",
                                    Kind = "integration"
                                }
                            ]
                        }
                    ],
                    Guidance = new MigrationRepairGuidance
                    {
                        ReasonCode = "test-reason",
                        Facts =
                        [
                            new()
                            {
                                Id = "test-fact",
                                Summary = "The target contains the test condition.",
                                Authority = "authoritative-detection",
                                Locations = [new() { Path = "Selector.cs", Line = 8 }]
                            }
                        ],
                        Classification = new MigrationGuidanceClassification
                        {
                            Status = "ambiguous",
                            Summary = "Two project-specific contracts are plausible.",
                            Candidates =
                            [
                                new()
                                {
                                    Type = "owner-a",
                                    Confidence = "medium",
                                    Reason = "The first owner is reachable."
                                }
                            ],
                            DiscriminatingTests = ["Probe the owning window."]
                        },
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
                        CompletionConditions = ["The behavior is verified."],
                        CompletionChecks =
                        [
                            new()
                            {
                                Id = "test-check",
                                Summary = "The state assertion passes.",
                                VerificationKind = "state-assertion",
                                Target = "test-state",
                                Expected = "passed"
                            }
                        ]
                    }
                }
            ],
            TodoGraph = new MigrationTodoGraph
            {
                Status = "valid",
                WorkItems =
                [
                    new()
                    {
                        Id = "UWMIG999:prepare",
                        TodoId = "UWMIG999",
                        Stage = "preparation",
                        Summary = "Prepare the repair."
                    }
                ],
                Edges =
                [
                    new()
                    {
                        PrerequisiteId = "UWMIG999:prepare",
                        DependentId = "UWMIG999:integrate",
                        Kind = "integration"
                    }
                ],
                ExecutionWaves =
                [
                    new()
                    {
                        Index = 0,
                        Stage = "preparation",
                        WorkItemIds = ["UWMIG999:prepare"]
                    }
                ]
            }
        };

        var json = JsonSerializer.Serialize(
            report,
            MigrateJsonContext.Default.MigrationReport);
        var roundTripped = JsonSerializer.Deserialize(
            json,
            MigrateJsonContext.Default.MigrationReport)!;

        Assert.AreEqual("1.6", roundTripped.SchemaVersion);
        var guidance = roundTripped.Todos.Single().Guidance!;
        Assert.AreEqual("test-reason", guidance.ReasonCode);
        Assert.AreEqual("test-fact", guidance.Facts.Single().Id);
        Assert.AreEqual(
            "authoritative-detection",
            guidance.Facts.Single().Authority);
        Assert.AreEqual(8, guidance.Facts.Single().Locations.Single().Line);
        Assert.AreEqual("ambiguous", guidance.Classification!.Status);
        Assert.AreEqual(
            "medium",
            guidance.Classification.Candidates.Single().Confidence);
        Assert.IsFalse(guidance.CandidateStrategiesExhaustive);
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
        Assert.AreEqual(
            "state-assertion",
            guidance.CompletionChecks.Single().VerificationKind);
        var todo = roundTripped.Todos.Single();
        Assert.AreEqual(
            "integration",
            todo.WorkSlices[1].Dependencies.Single().Kind);
        Assert.AreEqual(
            "preparation",
            roundTripped.TodoGraph.ExecutionWaves.Single().Stage);
        Assert.AreEqual(
            "UWMIG999:prepare",
            roundTripped.TodoGraph.ExecutionWaves.Single()
                .WorkItemIds.Single());
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
