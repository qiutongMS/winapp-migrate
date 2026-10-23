// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

[TestClass]
public sealed partial class EnforcementEvaluatorTests
{
    [TestMethod]
    public void Evaluate_MissingStatePlanFailsClosed()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input = input with { StatePlan = null };

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("blocked", snapshot.Status);
        Assert.AreEqual("state-plan", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public void Evaluate_DuplicateStateIdsFailClosed()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input.StatePlan!.States.Add(input.StatePlan.States[0]);

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("state-plan", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public void Evaluate_MechanicalFailureIsFirstFrontier()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput() with
        {
            MechanicalReady = false,
            MechanicalReason = "Mechanical verification failed."
        };

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("mechanical", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public void Evaluate_StaleBuildRequiresRebuild()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input.Build!.TargetFingerprint = "stale";

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("build", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public void Evaluate_ReplayForDifferentBuildRequiresReplay()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input.Replay!.BuildAttestationId = "different-build";

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("replay", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public void Evaluate_RuntimeFailureRequiresDiagnosis()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input.Attempts.Attempts =
        [
            new()
            {
                Kind = "runtime",
                TargetFingerprint = input.TargetFingerprint,
                Signature = "runtime:0x800f1000",
                RecordedAt = DateTimeOffset.UtcNow,
                StateId = "state-1",
                Evidence = ["evidence.txt"]
            }
        ];

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("diagnosis-required", snapshot.Frontier?.Kind);
        Assert.AreEqual("state-1", snapshot.Frontier?.Id);
        Assert.AreEqual(
            "runtime:0x800f1000",
            snapshot.Frontier?.Signature);
    }

    [TestMethod]
    public void Evaluate_FreshCompleteDiagnosisClearsDiagnosisFrontier()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        var recordedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        File.SetLastWriteTimeUtc(
            fixture.Path("evidence.txt"),
            DateTimeOffset.UtcNow.UtcDateTime);
        input.Attempts.Attempts =
        [
            new()
            {
                Kind = "runtime",
                TargetFingerprint = input.TargetFingerprint,
                Signature = "runtime:0x800f1000",
                RecordedAt = recordedAt,
                Evidence = ["evidence.txt"],
                Diagnosis = new FailureDiagnosis
                {
                    RecordedAt = DateTimeOffset.UtcNow,
                    SourceLocations = ["OuterNavPage.xaml.cs:100"],
                    TargetLocations = ["OuterNavPage.xaml.cs:90"],
                    SourceInvariant = "Preserve the selection workaround.",
                    Hypothesis = "Removing the guard causes the crash.",
                    DiscriminatingTest = "Restore only the guard.",
                    Evidence = ["evidence.txt"]
                }
            }
        ];

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("change-required", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public void Evaluate_StaleDiagnosisEvidenceRequiresDiagnosis()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        var recordedAt = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(
            fixture.Path("evidence.txt"),
            recordedAt.AddSeconds(-1).UtcDateTime);
        input.Attempts.Attempts =
        [
            new()
            {
                Kind = "runtime",
                TargetFingerprint = input.TargetFingerprint,
                Signature = "runtime:crash",
                RecordedAt = recordedAt,
                Diagnosis = new FailureDiagnosis
                {
                    RecordedAt = recordedAt.AddSeconds(1),
                    SourceLocations = ["source.cs:1"],
                    TargetLocations = ["target.cs:1"],
                    SourceInvariant = "Invariant",
                    Hypothesis = "Hypothesis",
                    DiscriminatingTest = "Test",
                    Evidence = ["evidence.txt"]
                }
            }
        ];

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("diagnosis-required", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public void Evaluate_InvalidComparisonStatusListsAllowedValues()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input.StatePlan!.States[0].Comparison.Status = "passed";

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("state", snapshot.Frontier?.Kind);
        StringAssert.Contains(
            snapshot.Frontier!.Reason,
            "use verified, blocked, unverified, or failed");
    }

    [TestMethod]
    public void Evaluate_StaleEvidenceBlocksStateClosure()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        var evidence = fixture.Path("evidence.txt");
        File.SetLastWriteTimeUtc(
            evidence,
            input.Replay!.StartedAt.AddSeconds(-1).UtcDateTime);

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("state", snapshot.Frontier?.Kind);
        StringAssert.Contains(snapshot.Frontier!.Reason, "predates");
    }

    [TestMethod]
    public void Evaluate_OutOfRootEvidenceBlocksStateClosure()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input.StatePlan!.States[0].Target.Evidence = ["../outside.txt"];

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("state", snapshot.Frontier?.Kind);
        StringAssert.Contains(snapshot.Frontier!.Reason, "escapes");
    }

    [TestMethod]
    public void Evaluate_MissingPassedStateEvidenceBlocksClosure()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input.StatePlan!.States[0].Target.Evidence = ["missing.json"];

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("state", snapshot.Frontier?.Kind);
        StringAssert.Contains(snapshot.Frontier!.Reason, "does not exist");
    }

    [TestMethod]
    public void Evaluate_ActionableBlockedFindingFailsClosed()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input = input with { FindingLedger = new SemanticFindingLedger
        {
            Findings =
            [
                new()
                {
                    Id = "finding-1",
                    Category = "behavior",
                    SourceInvariant = "Activation remains correct.",
                    Status = "blocked",
                    CompletionCondition = "Activation is validated.",
                    AppOwnedLocations = ["App.xaml.cs:10"],
                    NextActions = ["Inspect activation."]
                }
            ]
        } };

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("finding", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    [DataRow("open")]
    [DataRow("failed")]
    public void Evaluate_UnresolvedFindingFailsClosed(string status)
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput() with
        {
            FindingLedger = new SemanticFindingLedger
            {
                Findings =
                [
                    new()
                    {
                        Id = "finding-1",
                        Category = "behavior",
                        SourceInvariant = "Activation remains correct.",
                        Status = status,
                        CompletionCondition = "Activation is validated."
                    }
                ]
            }
        };

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("finding", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public void Evaluate_ResolvedFindingWithoutEvidenceFailsClosed()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input = input with { FindingLedger = new SemanticFindingLedger
        {
            Findings =
            [
                new()
                {
                    Id = "finding-1",
                    Category = "behavior",
                    SourceInvariant = "Activation remains correct.",
                    Status = "resolved",
                    CompletionCondition = "Activation is validated."
                }
            ]
        } };

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("finding", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public void Evaluate_UnsupportedArtifactSchemaFailsClosed()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input.Attempts.SchemaVersion = "2.0";

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("evidence", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public void Evaluate_ReportMismatchBlocksClosure()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input = input with { ReportValidation = input.ReportValidation with
        {
            ParityStatus = "partial"
        } };

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("report-sync", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public void Evaluate_TwoSameSignatureAttemptsExhaustBudget()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input = input with { Build = null, Replay = null };
        input.Attempts.Attempts =
        [
            Attempt(input.TargetFingerprint, "same"),
            Attempt(input.TargetFingerprint, "same")
        ];

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("change-required", snapshot.Frontier?.Kind);
        Assert.AreEqual(0, snapshot.Frontier!.AttemptsRemaining);
    }

    [TestMethod]
    public void Evaluate_TargetChangeResetsRetryBudget()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input = input with { Build = null, Replay = null };
        input.Attempts.Attempts =
        [
            Attempt("old-fingerprint", "same"),
            Attempt("old-fingerprint", "same")
        ];

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("build", snapshot.Frontier?.Kind);
        Assert.AreEqual(2, snapshot.Frontier!.AttemptsRemaining);
    }

    [TestMethod]
    public void Evaluate_LaterSuccessfulBuildSupersedesFailureBudget()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input.Attempts.Attempts =
        [
            Attempt(input.TargetFingerprint, "same"),
            Attempt(input.TargetFingerprint, "same")
        ];
        foreach (var attempt in input.Attempts.Attempts)
        {
            attempt.RecordedAt = input.Build!.CompletedAt.AddSeconds(-1);
        }

        var snapshot = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("passed", snapshot.Status);
    }

    [TestMethod]
    public void Evaluate_FullyClosedEvidencePasses()
    {
        using var fixture = new EvaluatorFixture();

        var snapshot = EnforcementEvaluator.Evaluate(fixture.ClosedInput());

        Assert.AreEqual("passed", snapshot.Status);
        Assert.IsNull(snapshot.Frontier);
    }

    [TestMethod]
    public void TargetFingerprint_IgnoresReportAndEvidenceChanges()
    {
        using var fixture = new EvaluatorFixture();
        var before = fixture.Fingerprint();

        File.WriteAllText(fixture.Path("migration-report.json"), "changed");
        Directory.CreateDirectory(fixture.Path(".migration-evidence"));
        File.WriteAllText(
            fixture.Path(".migration-evidence/state-plan.json"),
            "changed");

        Assert.AreEqual(before, fixture.Fingerprint());
    }

    [TestMethod]
    public void EvidenceAndFingerprint_RejectDirectoryLinks()
    {
        var root = TestDirectories.Create("enforcement-link-root-");
        var external = TestDirectories.Create(
            "enforcement-link-external-");
        var link = System.IO.Path.Combine(root.FullName, "linked");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, external.FullName);
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
                Assert.Inconclusive(
                    $"Symbolic links are unavailable: {exception.Message}");
            }

            Assert.IsFalse(EnforcementStore.TryResolveContainedPath(
                root,
                "linked/evidence.json",
                out _,
                out var pathError));
            StringAssert.Contains(pathError, "reparse point");
            Assert.ThrowsExactly<InvalidDataException>(() =>
                TargetFingerprint.Compute(root));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
            root.Delete(recursive: true);
            external.Delete(recursive: true);
        }
    }

    private static EnforcementAttempt Attempt(
        string fingerprint,
        string signature) =>
        new()
        {
            Kind = "build",
            TargetFingerprint = fingerprint,
            Signature = signature,
            RecordedAt = DateTimeOffset.UtcNow
        };

    private sealed class EvaluatorFixture : IDisposable
    {
        private readonly DirectoryInfo _root;

        internal EvaluatorFixture()
        {
            _root = TestDirectories.Create("enforcement-evaluator-");
            File.WriteAllText(Path("app.txt"), "target");
            File.WriteAllText(Path("evidence.txt"), "verified");
            File.WriteAllText(Path("build-stdout.json"), "{}");
            File.WriteAllText(Path("build-stderr.log"), "");
            File.WriteAllText(Path("replay-stdout.json"), "{}");
            File.WriteAllText(Path("replay-stderr.log"), "");
        }

        internal EnforcementEvaluationInput ClosedInput()
        {
            var replayStarted = DateTimeOffset.UtcNow.AddMinutes(-1);
            File.SetLastWriteTimeUtc(
                Path("evidence.txt"),
                DateTimeOffset.UtcNow.UtcDateTime);
            var plan = new StatePlanDocument
            {
                States =
                [
                    new()
                    {
                        Id = "state-1",
                        Actions = ["Open the main state."],
                        ExpectedOutcome = "Main state remains usable.",
                        Source = new StateObservation
                        {
                            Status = "captured",
                            Evidence = ["evidence.txt"]
                        },
                        Target = new StateObservation
                        {
                            Status = "passed",
                            Evidence = ["evidence.txt"]
                        },
                        Comparison = new StateComparison
                        {
                            Status = "verified"
                        }
                    }
                ]
            };
            var fingerprint = TargetFingerprint.Compute(_root);
            return new EnforcementEvaluationInput
            {
                TargetRoot = _root,
                TargetFingerprint = fingerprint,
                MechanicalReady = true,
                MechanicalReason = "passed",
                AnalyzerReferencePresent = true,
                AnalyzerPrivateAssets = true,
                StatePlan = plan,
                Build = new BuildAttestation
                {
                    Id = "build-1",
                    TargetFingerprint = fingerprint,
                    StartedAt = replayStarted.AddMinutes(-1),
                    CompletedAt = replayStarted.AddSeconds(-1),
                    Command = ["winapp"],
                    ExitCode = 0,
                    Success = true,
                    StructuredOutput = true,
                    AnalyzerPackage =
                        "Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer",
                    AnalyzerPrivateAssets = true,
                    StandardOutputPath = "build-stdout.json",
                    StandardErrorPath = "build-stderr.log"
                },
                Replay = new ReplayAttestation
                {
                    Id = "replay-1",
                    RunId = "run-1",
                    TargetFingerprint = fingerprint,
                    BuildAttestationId = "build-1",
                    StartedAt = replayStarted,
                    CompletedAt = replayStarted.AddSeconds(1),
                    Command = ["winapp"],
                    ExitCode = 0,
                    Success = true,
                    ProcessId = 123,
                    StandardOutputPath = "replay-stdout.json",
                    StandardErrorPath = "replay-stderr.log"
                },
                Attempts = new AttemptLedger(),
                ReportValidation =
                    EnforcementEvaluator.SummarizeStatePlan(plan)
            };
        }

        internal string Path(string relative) =>
            System.IO.Path.Combine(_root.FullName, relative);

        internal string Fingerprint() => TargetFingerprint.Compute(_root);

        public void Dispose() => TestDirectories.Delete(_root);
    }
}
