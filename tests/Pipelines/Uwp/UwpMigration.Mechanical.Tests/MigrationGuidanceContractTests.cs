// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
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
                                VerificationKind = "surface-check",
                                Target = "test-state",
                                Expected = "passed",
                                AssertionIds = ["target-reachable", "uia-structure-match"]
                            }
                        ]
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

        Assert.AreEqual("1.7", roundTripped.SchemaVersion);
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
            "surface-check",
            guidance.CompletionChecks.Single().VerificationKind);
        Assert.AreEqual(
            "UWMIG999",
            roundTripped.Todos.Single().Id);
    }

    [TestMethod]
    [DataRow("state-set")]
    [DataRow("missing-checks")]
    [DataRow("missing-check-ids")]
    public void TodoContract_RejectsRetiredOrIncompleteCompletionProtocol(string mutation)
    {
        var todo = CurrentSurfaceTodo();
        if (mutation == "state-set")
        {
            todo.Guidance!.CompletionChecks[0].VerificationKind = "state-set";
            todo.Guidance.CompletionChecks[0].Target = "affectedStates";
        }
        if (mutation == "missing-checks")
            todo.Guidance!.CompletionChecks.Clear();
        if (mutation == "missing-check-ids")
            todo.Guidance!.CompletionChecks[0].AssertionIds.Clear();

        Assert.ThrowsExactly<InvalidDataException>(() =>
            MigrationTodoValidator.ValidatePersisted([todo]));
    }

    [TestMethod]
    [DataRow("affectedStates")]
    [DataRow("assertionIds")]
    public void TodoContract_RejectsRetiredJsonNames(string property)
    {
        var report = new MigrationReport
        {
            Source = new MigrationProject { Root = "source" },
            Target = new MigrationProject { Root = "target" },
            Todos = [CurrentSurfaceTodo()]
        };
        var node = JsonNode.Parse(JsonSerializer.Serialize(
            report, MigrateJsonContext.Default.MigrationReport))!;
        if (property == "affectedStates")
            node["todos"]![0]![property] = new JsonArray("main");
        else
            node["todos"]![0]!["guidance"]!["completionChecks"]![0]![property] =
                new JsonArray("target-reachable");
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize(
            node.ToJsonString(), MigrateJsonContext.Default.MigrationReport));
    }

    private static MigrationTodo CurrentSurfaceTodo() => new()
    {
        Id = "UWMIG-CURRENT",
        Category = "test",
        Priority = "required",
        Summary = "Preserve the main surface.",
        Reason = "The main surface is inventoried.",
        AffectedStates = ["main"],
        Guidance = new MigrationRepairGuidance
        {
            ReasonCode = "test",
            CompletionChecks =
            [
                new()
                {
                    Id = "main-parity",
                    Summary = "The main surface preserves its tree.",
                    VerificationKind = "surface-check",
                    Target = "main",
                    Expected = "passed",
                    AssertionIds = ["target-reachable", "uia-structure-match"]
                }
            ]
        }
    };

    [TestMethod]
    public async Task ReportStoreRejectsSchema16()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "schemaVersion": "1.6",
                  "source": { "root": "source" },
                  "target": { "root": "target" }
                }
                """);

            var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => MigrationReportStore.LoadAsync(
                    path,
                    CancellationToken.None));

            StringAssert.Contains(
                error.Message,
                "schemaVersion '1.6' is not supported");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
