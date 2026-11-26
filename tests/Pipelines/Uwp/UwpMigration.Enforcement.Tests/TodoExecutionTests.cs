// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using UwpMigration.Contracts;
using UwpMigration.Enforcement;
using WinAppMigration.Cli.Pipelines.Uwp;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    [DataRow("historical")]
    [DataRow("current")]
    [DataRow("other-code")]
    [DataRow("tampered")]
    [DataRow("ambiguous")]
    public async Task SurfaceComparison_PairsHistoricalImagesWithoutChangingAcceptance(string captureCase)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var process = new FakeProcessRunner(new EnforcementProcessResult(0, ClosureTree, ""))
        {
            SaveComparisonScreenshots = true
        };
        var probe = await new UwpEnforcementRunner(process, windowOwnerResolver: TestWindowOwnerResolver.Shared)
            .InvokeUiAsync(fixture.Root, "winapp", ["inspect", "--json", "-w", "42"], [], "state-1");
        var snapshot = await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        var receiptPath = Path.GetDirectoryName(probe.ArtifactPath)!.Replace('\\', '/') + "/comparison-capture.json";
        var receipt = await fixture.LoadAsync<SurfaceComparisonArtifacts.ComparisonCapture>(receiptPath);
        var historicalRun = receipt.RunId;
        if (captureCase == "current")
        {
            await new UwpEnforcementRunner(new FakeProcessRunner(ReplaySuccess()))
                .RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
            var currentProbe = await new UwpEnforcementRunner(
                new FakeProcessRunner(new EnforcementProcessResult(0, ClosureTree, "")) { SaveComparisonScreenshots = true },
                windowOwnerResolver: TestWindowOwnerResolver.Shared)
                .InvokeUiAsync(fixture.Root, "winapp", ["inspect", "--json", "-w", "42"], [], "state-1");
            snapshot = await UwpEnforcementRunner.RecordSurfaceAsync(
                fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [currentProbe.ArtifactPath]);
        }
        else snapshot.Replay!.Id += "-later";
        if (captureCase == "other-code") receipt.Fingerprint += "-different";
        else if (captureCase == "tampered") await File.AppendAllTextAsync(fixture.Path(receipt.ScreenshotPath!), "tampered");
        else if (captureCase == "ambiguous")
        {
            await EnforcementStore.WriteAtomicAsync(fixture.Root,
                ".migration-evidence/enforcement/another/comparison-capture.json", receipt);
        }
        await EnforcementStore.WriteAtomicAsync(fixture.Root, receiptPath, receipt);
        var before = JsonSerializer.Serialize(snapshot, EnforcementStore.JsonOptions);

        await SurfaceComparisonArtifacts.WriteIndexAsync(fixture.Root, snapshot, CancellationToken.None);
        var html = await File.ReadAllTextAsync(fixture.Path(SurfaceComparisonArtifacts.IndexPath));

        Assert.AreEqual(before, JsonSerializer.Serialize(snapshot, EnforcementStore.JsonOptions));
        if (captureCase is "historical" or "current")
        {
            StringAssert.Contains(html, "<img alt=\"target screenshot\"");
            StringAssert.Contains(html, captureCase == "historical" ? historicalRun! : snapshot.Replay!.Id);
            Assert.AreEqual(captureCase == "historical", html.Contains("not a current-replay verification", StringComparison.Ordinal));
        }
        else
        {
            Assert.IsFalse(html.Contains("<img alt=\"target screenshot\"", StringComparison.Ordinal));
            StringAssert.Contains(html, captureCase == "tampered" ? "Screenshot unavailable or stale" : "Screenshot missing or ambiguous");
        }
    }

    [TestMethod]
    [DataRow("saved")]
    [DataRow("missing")]
    [DataRow("invalid")]
    public async Task SurfaceComparison_ExportsPairedEvidenceWithoutAddingAnAcceptanceGate(string screenshot)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(
            fixture.Root, new UwpEnforcementOptions());
        var process = new FakeProcessRunner(new EnforcementProcessResult(0, ClosureTree, ""))
        {
            SaveComparisonScreenshots = screenshot != "missing",
            ComparisonScreenshotContent = screenshot == "invalid" ? [] : null
        };
        var probe = await new UwpEnforcementRunner(process, windowOwnerResolver: TestWindowOwnerResolver.Shared)
            .InvokeUiAsync(fixture.Root, "winapp", ["inspect", "--json", "-w", "42"], [], "state-1");
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        var sourceTree = plan.States.Single().Source.TreeEvidence!;
        const string sourceImage = ".migration-evidence/source/gateway/comparison-source.png";
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Path(sourceImage))!);
        await File.WriteAllBytesAsync(fixture.Path(sourceImage), PngTestData.Create());
        await EnforcementStore.WriteAtomicAsync(fixture.Root,
            ".migration-evidence/source/gateway/comparison-capture.json", new
            {
                side = "source", treePath = sourceTree,
                treeSha256 = StateClosureProtocol.FileHash(fixture.Root, sourceTree),
                screenshotPath = sourceImage,
                screenshotSha256 = StateClosureProtocol.FileHash(fixture.Root, sourceImage),
                status = "saved", runId = "source-run", fingerprint = "source-fingerprint"
            });
        await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);

        var snapshot = await UwpEnforcementRunner.SynchronizeReportAsync(
            fixture.Root, TestWindowOwnerResolver.Shared);
        var html = await File.ReadAllTextAsync(fixture.Path(".migration-evidence/comparison/index.html"));

        Assert.AreEqual("passed", snapshot.Status);
        Assert.HasCount(1, process.ScreenshotCalls);
        StringAssert.Contains(html, "Before (UWP)");
        StringAssert.Contains(html, "After (WinUI)");
        StringAssert.Contains(html, "state-1");
        StringAssert.Contains(html, "UIA tree");
        StringAssert.Contains(html, "source-run");
        if (screenshot == "saved")
        {
            StringAssert.Contains(html, "comparison.png");
            await File.AppendAllTextAsync(process.ScreenshotCalls.Single()[^1], "tampered");
            var current = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
            Assert.AreEqual("passed", current.Status);
            await SurfaceComparisonArtifacts.WriteIndexAsync(fixture.Root, current, CancellationToken.None);
            html = await File.ReadAllTextAsync(fixture.Path(".migration-evidence/comparison/index.html"));
        }
        StringAssert.Contains(html, "Screenshot unavailable or stale");
    }

    [TestMethod]
    public async Task TodoExecution_RecordingImplementationPreservesLegacyApproachHistory()
    {
        using var fixture = await TodoFixtureAsync();
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, EnforcementStore.TodoProgressPath, """
            {
              "schemaVersion": "3.0",
              "todos": [{
                "todoId": "UWMIG014",
                "currentApproachKey": "preserve-template",
                "currentApproachRationale": "Retain the source item presentation.",
                "attempt": 1,
                "events": [{
                  "sequence": 1,
                  "attempt": 1,
                  "kind": "approach-selected",
                  "outcome": "selected",
                  "summary": "Selected the original presentation.",
                  "approachKey": "preserve-template",
                  "recordedAt": "2026-01-01T00:00:00Z",
                  "evidence": []
                }]
              }]
            }
            """);

        var assessment = await TodoExecutionService.RecordImplementationAsync(
            fixture.Root, "UWMIG014", "implemented", "Preserved the source template.");

        Assert.AreEqual("implemented", assessment.ImplementationStatus);
        Assert.HasCount(1, assessment.History);
        var progress = (await fixture.LoadAsync<TodoExecutionProgressDocument>(
            EnforcementStore.TodoProgressPath)).Todos.Single();
        Assert.AreEqual("preserve-template", progress.CurrentApproachKey);
        Assert.AreEqual("Retain the source item presentation.", progress.CurrentApproachRationale);
        Assert.HasCount(2, progress.Events);
        Assert.AreEqual("preserve-template", progress.Events[0].ApproachKey);
        Assert.AreEqual(TodoExecutionService.Implementation, progress.Events[1].Kind);
    }

    [TestMethod]
    public async Task TodoExecution_RetiredTodoKeepsHistoryWithoutBlockingCurrentTasks()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        var retired = ProjectTodo();
        report.Todos.Add(retired);
        await fixture.SaveReportAsync(report);
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root, retired.Id, "implemented", "Project obligation repaired.");
        report.Todos.Clear();
        var current = ProjectTodo();
        current.Id += "-CURRENT";
        report.Todos.Add(current);
        await fixture.SaveReportAsync(report);

        var assessments = await TodoExecutionService.AssessAsync(
            fixture.Root, windowOwnerResolver: TestWindowOwnerResolver.Shared);

        Assert.HasCount(1, assessments);
        Assert.AreEqual(current.Id, assessments.Single().Todo.Id);
        var history = await fixture.LoadAsync<TodoExecutionProgressDocument>(EnforcementStore.TodoProgressPath);
        Assert.AreEqual(retired.Id, history.Todos.Single().TodoId);
    }

    [TestMethod]
    public async Task TodoExecution_InputMismatchPreservesCaptureRepairFeedback()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        await File.WriteAllTextAsync(fixture.Path(plan.States.Single().Source.TreeEvidence!),
            ClosureTree.Replace("Ready", "2026-11-25T10:00:00Z", StringComparison.Ordinal));
        var report = await fixture.LoadReportAsync();
        var todo = Uwmig014();
        report.Todos.Add(todo);
        await fixture.SaveReportAsync(report);
        await TodoExecutionService.RecordImplementationAsync(fixture.Root, todo.Id, "implemented", "UI migrated.");
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(
            0, ClosureTree.Replace("Ready", "2026-11-25T10:01:00Z", StringComparison.Ordinal), ""));
        await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);

        var assessment = (await TodoExecutionService.AssessAsync(
            fixture.Root, todo.Id, TestWindowOwnerResolver.Shared)).Single();

        Assert.AreEqual("failed", assessment.VerificationStatus);
        Assert.AreEqual("review-inputs", assessment.NextOperation);
        Assert.IsTrue(assessment.Surfaces.Single().ComparisonDiagnostics.Any(item =>
            item.RepairOwner == "capture" && !item.IsNormalization));
    }

    [TestMethod]
    public async Task TodoExecution_CurrentUwpResidueBlocksAcceptanceDespitePassedReportAndTodo()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        var todo = ProjectTodo();
        todo.Status = "verified";
        todo.Guidance!.CompletionChecks.Add(new MigrationCompletionCheck
        {
            Id = "target-compatibility",
            Summary = "No incompatible UWP contract remains in current target files.",
            VerificationKind = "target-uwp-compatibility", Target = "target-source", Expected = "passed"
        });
        report.Todos.Add(todo);
        await fixture.SaveReportAsync(report);
        var probe = await CaptureClosureProbeAsync(fixture);
        await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        Assert.AreEqual("passed", (await UwpEnforcementRunner.SynchronizeReportAsync(
            fixture.Root, TestWindowOwnerResolver.Shared)).Status);
        await File.WriteAllTextAsync(fixture.Path("Compatibility.cs"), "var window = global::Microsoft.UI.Xaml.Window.Current;");

        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        var assessment = (await TodoExecutionService.AssessAsync(
            fixture.Root, todo.Id, TestWindowOwnerResolver.Shared)).Single();

        Assert.AreNotEqual("passed", snapshot.Status);
        var gate = snapshot.Gates.Single(item => item.Id == "mechanical");
        Assert.AreEqual("blocked", gate.Status);
        StringAssert.Contains(gate.Reason, "UWPC003 Compatibility.cs:1");
        Assert.AreEqual("pending", assessment.VerificationStatus);
        Assert.AreEqual("passed", (await fixture.LoadReportAsync()).MechanicalVerification.Status);
        File.Delete(fixture.Path("Compatibility.cs"));
        Assert.AreEqual("passed", (await UwpEnforcementRunner.AssessAsync(
            fixture.Root, TestWindowOwnerResolver.Shared)).Gates.Single(item => item.Id == "mechanical").Status);
    }

    [TestMethod]
    public async Task TodoExecution_ImplementationDoesNotRequireAStrategyOrProveAcceptance()
    {
        using var fixture = await TodoFixtureAsync();
        var implemented = await TodoExecutionService.RecordImplementationAsync(
            fixture.Root, "UWMIG014", "implemented", "Preserved the source item presentation.");
        Assert.AreEqual("implemented", implemented.ImplementationStatus);
        Assert.AreEqual("pending", implemented.VerificationStatus);
        Assert.AreEqual("record-surface", implemented.NextOperation);
        await File.AppendAllTextAsync(fixture.ProjectPath, Environment.NewLine);
        var current = (await TodoExecutionService.AssessAsync(fixture.Root, "UWMIG014")).Single();
        Assert.AreEqual("implemented", current.ImplementationStatus);
        Assert.AreEqual("pending", current.VerificationStatus);
    }

    [TestMethod]
    public async Task TodoExecution_CliSurfaceProofIsSharedWithoutAdditionalTodoReceipts()
    {
        using var fixture = await TodoFixtureAsync();
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root, "UWMIG014", "implemented", "Preserved source item presentation.");
        var probe = await CaptureClosureProbeAsync(fixture);
        var command = new UwpEnforceCommand(new FakeProcessRunner(), TestWindowOwnerResolver.Shared);
        var parsed = command.Parse([
            "record-surface", fixture.Root.FullName, "--surface", "state-1", "--probe", probe.ArtifactPath]);
        Assert.AreEqual(0, await parsed.InvokeAsync(parsed.InvocationConfiguration, CancellationToken.None));
        var proofPath = StateClosureProtocol.AttestationPath("state-1");
        var originalProof = await File.ReadAllTextAsync(fixture.Path(proofPath));
        var assessment = (await TodoExecutionService.AssessAsync(
            fixture.Root, "UWMIG014", TestWindowOwnerResolver.Shared)).Single();
        Assert.AreEqual("implemented", assessment.ImplementationStatus);
        Assert.AreEqual("verified", assessment.VerificationStatus);
        Assert.AreEqual("verified", assessment.Surfaces.Single().Status);
        Assert.IsEmpty(assessment.HumanValidationItems);
        await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        Assert.AreEqual(originalProof, await File.ReadAllTextAsync(fixture.Path(proofPath)));
        var progress = await fixture.LoadAsync<TodoExecutionProgressDocument>(EnforcementStore.TodoProgressPath);
        Assert.AreEqual(TodoExecutionService.Implementation, progress.Todos.Single().Events.Single().Kind);
        Assert.AreEqual("passed", (await UwpEnforcementRunner.SynchronizeReportAsync(
            fixture.Root, TestWindowOwnerResolver.Shared)).Status);
        Assert.AreEqual("verified", (await fixture.LoadReportAsync()).Todos.Single().Status);
    }

    [TestMethod]
    [DataRow("source")]
    [DataRow("target")]
    [DataRow("replay")]
    [DataRow("output")]
    public async Task TodoExecution_InvalidInputsReopenVerificationButSameBuildReplaysPreserveIt(string change)
    {
        using var fixture = await TodoFixtureAsync();
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root, "UWMIG014", "implemented", "Preserved source item presentation.");
        var probe = await CaptureClosureProbeAsync(fixture);
        await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        if (change == "target")
            await File.AppendAllTextAsync(fixture.ProjectPath, Environment.NewLine);
        if (change == "source")
        {
            var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
            await File.AppendAllTextAsync(fixture.Path(plan.States[0].Source.TreeEvidence!), " ");
        }
        if (change == "output")
            await File.AppendAllTextAsync(fixture.Path(probe.StandardOutputPath), " ");
        if (change == "replay")
            await new UwpEnforcementRunner(new FakeProcessRunner(ReplaySuccess())).RunAsync(
                fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        var assessment = (await TodoExecutionService.AssessAsync(
            fixture.Root, "UWMIG014", TestWindowOwnerResolver.Shared)).Single();
        Assert.AreEqual("implemented", assessment.ImplementationStatus);
        Assert.AreEqual(change == "replay" ? "verified" : "pending", assessment.VerificationStatus);
        Assert.IsEmpty(assessment.HumanValidationItems);
    }

    [TestMethod]
    [DataRow("mismatch", "failed")]
    [DataRow("unparseable", "human-validation-required")]
    public async Task TodoExecution_ConclusiveFailureAndAttemptedInconclusiveAreDistinct(string capture, string expected)
    {
        using var fixture = await TodoFixtureAsync();
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root, "UWMIG014", "implemented", "Preserved source item presentation.");
        var output = capture == "mismatch"
            ? ClosureTree.Replace("Ready", "Changed", StringComparison.Ordinal)
            : "No comparable UIA tree was returned.";
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(0, output, ""));
        await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        var assessment = (await TodoExecutionService.AssessAsync(
            fixture.Root, "UWMIG014", TestWindowOwnerResolver.Shared)).Single();
        Assert.AreEqual(expected, assessment.VerificationStatus);
        Assert.AreEqual(expected, assessment.Surfaces.Single().Status);
        Assert.AreEqual(capture == "unparseable" ? 1 : 0, assessment.HumanValidationItems.Count);
    }

    [TestMethod]
    public async Task TodoExecution_UntestedSurfaceIsPendingRatherThanHumanValidation()
    {
        using var fixture = await TodoFixtureAsync();
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root, "UWMIG014", "implemented", "Preserved source item presentation.");
        var assessment = (await TodoExecutionService.AssessAsync(fixture.Root, "UWMIG014")).Single();
        Assert.AreEqual("pending", assessment.VerificationStatus);
        Assert.IsEmpty(assessment.HumanValidationItems);
        var status = await TodoExecutionService.StatusAsync(
            fixture.Root, await TargetFingerprint.ComputeAsync(fixture.Root), TestWindowOwnerResolver.Shared);
        Assert.AreEqual("verification", status.Phase);
        Assert.AreEqual(1, status.Todos.PendingVerification);
        CollectionAssert.AreEqual(new[] { "state-1" }, status.FrontierStateIds);
    }

    [TestMethod]
    public async Task TodoExecution_ProjectOnlyScopeDoesNotRequireSurfaceBinding()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(ProjectTodo());
        await fixture.SaveReportAsync(report);
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var assessment = await TodoExecutionService.RecordImplementationAsync(
            fixture.Root, "UWMIG-BUILD", "implemented", "Completed the target project graph.");
        Assert.AreEqual("verified", assessment.VerificationStatus);
        Assert.IsEmpty(assessment.Surfaces);
        Assert.IsEmpty(assessment.HumanValidationItems);
    }

    [TestMethod]
    [DataRow("contained", true)]
    [DataRow("empty", true)]
    [DataRow("nested-not-required", false)]
    [DataRow("issue-not-required", false)]
    [DataRow("missing-entry", false)]
    [DataRow("failed", false)]
    [DataRow("incomplete", false)]
    [DataRow("not-run", false)]
    public async Task TodoExecution_ProjectGraphCheckAcceptsOnlyAContainedOrEmptyGraph(
        string scenario, bool expected)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        var todo = ProjectTodo();
        todo.Guidance!.CompletionChecks = [ProjectGraphCheck()];
        var graph = report.MechanicalVerification.TargetProjectGraph;
        graph.Status = scenario switch
        {
            "contained" => "passed",
            "failed" or "incomplete" or "not-run" => scenario,
            _ => "not-required"
        };
        graph.EntryProject = scenario == "missing-entry" ? null : "Target.csproj";
        if (scenario is "contained" or "nested-not-required")
            graph.NestedProjects.Add("Dependency/Dependency.csproj");
        if (scenario == "issue-not-required")
            graph.Issues.Add(new MigrationTargetProjectGraphIssue
            {
                Kind = "target-external-file-items", EntryProject = "Target.csproj",
                Reason = "The target includes a source-checkout file.",
                RequiredResolution = "Move the file into the target."
            });

        Assert.AreEqual(expected, TodoCompletionContract.ProjectClosureChecksPass(
            fixture.Root, todo, report, null, "fingerprint"));
    }

    [TestMethod]
    public async Task TodoExecution_EmptyProjectGraphDoesNotBlockStrictFinalization()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        var todo = ProjectTodo();
        todo.Id = "UWMIG003";
        todo.Guidance!.CompletionChecks.Add(ProjectGraphCheck());
        todo.Guidance.CompletionChecks.Add(new MigrationCompletionCheck
        {
            Id = "dependency-contract-review", Summary = "Review dependency behavior separately.",
            VerificationKind = "dependency-contract-review", Target = "dependencyAnalysis",
            Expected = "Consumed dependency behavior is preserved."
        });
        report.Todos.Add(todo);
        report.MechanicalVerification.TargetProjectGraph.Status = "not-required";
        report.MechanicalVerification.TargetProjectGraph.EntryProject = "Target.csproj";
        await fixture.SaveReportAsync(report);
        var probe = await CaptureClosureProbeAsync(fixture);
        await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);

        var command = new UwpEnforceCommand(new FakeProcessRunner(), TestWindowOwnerResolver.Shared);
        var parsed = command.Parse(["finalize", fixture.Root.FullName, "--strict"]);
        Assert.AreEqual(0, await parsed.InvokeAsync(parsed.InvocationConfiguration, CancellationToken.None));

        var snapshot = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        Assert.AreEqual("passed", snapshot.Status);
        Assert.AreEqual("complete", snapshot.Disposition?.Kind);
        Assert.AreEqual("UWMIG003", snapshot.HumanValidationItems.Single().TodoId);
        var assessment = (await TodoExecutionService.AssessAsync(
            fixture.Root, todo.Id, TestWindowOwnerResolver.Shared)).Single();
        Assert.AreEqual("human-validation-required", assessment.VerificationStatus);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("stale")]
    [DataRow("modified-output")]
    public async Task TodoExecution_ProjectVerificationRequiresCurrentAuthenticatedBuild(string change)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(ProjectTodo());
        await fixture.SaveReportAsync(report);
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var build = await fixture.LoadAsync<BuildAttestation>(EnforcementStore.BuildAttestationPath);
        if (change == "missing")
            File.Delete(fixture.Path(EnforcementStore.BuildAttestationPath));
        if (change == "stale")
            await File.AppendAllTextAsync(fixture.ProjectPath, Environment.NewLine);
        if (change == "modified-output")
            await File.AppendAllTextAsync(fixture.Path(build.StandardOutputPath), "tampered");
        var assessment = (await TodoExecutionService.AssessAsync(fixture.Root, "UWMIG-BUILD")).Single();
        Assert.AreEqual("pending", assessment.VerificationStatus);
        Assert.IsEmpty(assessment.Surfaces);
    }

    [TestMethod]
    public async Task TodoExecution_BusinessHandoffDoesNotBecomeAHiddenSurfaceGate()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(new MigrationTodo
        {
            Id = "UWMIG-FILE", Category = "file-picker", Priority = "required",
            Summary = "Preserve file reading and cancellation behavior.",
            Reason = "UI surface parity cannot prove file contents or cancellation semantics."
        });
        await fixture.SaveReportAsync(report);
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root, "UWMIG-FILE", "implemented", "Implemented the source file operation.");
        var probe = await CaptureClosureProbeAsync(fixture);
        await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        var completed = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("passed", completed.Status);
        Assert.AreEqual("UWMIG-FILE", completed.HumanValidationItems.Single().TodoId);
        Assert.IsNull(completed.HumanValidationItems.Single().StateId);
        var reassessed = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("UWMIG-FILE", reassessed.HumanValidationItems.Single().TodoId);
        var assessment = (await TodoExecutionService.AssessAsync(
            fixture.Root, "UWMIG-FILE", TestWindowOwnerResolver.Shared)).Single();
        Assert.AreEqual("implemented", assessment.ImplementationStatus);
        Assert.AreEqual("human-validation-required", assessment.VerificationStatus);
        Assert.IsEmpty(assessment.Surfaces);
        Assert.IsNull(assessment.HumanValidationItems.Single().StateId);
        Assert.AreEqual("Preserve file reading and cancellation behavior.",
            assessment.HumanValidationItems.Single().Behavior);
        Assert.AreNotEqual("terminal-with-evidence",
            assessment.HumanValidationItems.Single().ExpectedOutcome);
        Assert.AreEqual("pending", (await fixture.LoadReportAsync()).Todos.Single().Status);
        var status = await TodoExecutionService.StatusAsync(
            fixture.Root, completed.TargetFingerprint, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("human-handoff", status.Phase);
    }

    [TestMethod]
    public async Task TodoExecution_SourceDependencyReviewDoesNotBlockCurrentProjectChecks()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.DependencyAnalysis.Status = "review-required";
        var todo = ProjectTodo();
        todo.Guidance!.CompletionChecks.Add(new MigrationCompletionCheck
        {
            Id = "dependency-decisions-complete",
            Summary = "Every inventoried dependency has a target compatibility decision.",
            VerificationKind = "report-collection-status",
            Target = "dependencyAnalysis",
            Expected = "resolved"
        });
        report.Todos.Add(todo);
        await fixture.SaveReportAsync(report);
        var probe = await CaptureClosureProbeAsync(fixture);

        var assessment = (await TodoExecutionService.AssessAsync(
            fixture.Root, todo.Id, TestWindowOwnerResolver.Shared)).Single();
        await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        var completed = await UwpEnforcementRunner.SynchronizeReportAsync(
            fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreEqual("human-validation-required", assessment.VerificationStatus);
        Assert.HasCount(1, assessment.HumanValidationItems);
        Assert.AreEqual("passed", completed.Status);
        Assert.AreEqual("review-required", (await fixture.LoadReportAsync()).DependencyAnalysis.Status);
    }

    [TestMethod]
    [DataRow(false, "passed")]
    [DataRow(true, "passed")]
    [DataRow(true, "missing-build")]
    [DataRow(true, "mismatch")]
    public async Task TodoExecution_ActivationReviewDoesNotReplaceFixedAcceptanceChecks(
        bool legacy, string result)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.MechanicalVerification.ActivationContracts.ReviewRequiredContracts = 1;
        var todo = ProjectTodo();
        todo.Summary = "Preserve the source protocol activation handler.";
        todo.AffectedStates = ["activation:windows.protocol"];
        todo.Guidance!.CompletionConditions =
            ["The registered protocol delivers its payload to the intended destination."];
        todo.Guidance.Classification = new MigrationGuidanceClassification
        {
            Status = "confirmed",
            Summary = "Protocol delivery requires validation through the registered entry point.",
            DiscriminatingTests = ["Invoke the registered protocol and verify its payload and destination."]
        };
        todo.Guidance.CompletionChecks.Add(new MigrationCompletionCheck
        {
            Id = "activation-contract-review",
            Summary = "Review the source protocol payload and destination.",
            VerificationKind = legacy ? "report-field" : "activation-contract-review",
            Target = legacy
                ? "mechanicalVerification.activationContracts.reviewRequiredContracts"
                : "activationAnalysis",
            Expected = legacy ? "0" : todo.Guidance.CompletionConditions.Single()
        });
        report.Todos.Add(todo);
        await fixture.SaveReportAsync(report);
        var probe = await CaptureClosureProbeAsync(fixture,
            result == "mismatch"
                ? new EnforcementProcessResult(0,
                    ClosureTree.Replace("Ready", "Changed", StringComparison.Ordinal), "")
                : null);
        await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        if (result == "missing-build")
            File.Delete(fixture.Path(EnforcementStore.BuildAttestationPath));

        var assessment = (await TodoExecutionService.AssessAsync(
            fixture.Root, todo.Id, TestWindowOwnerResolver.Shared)).Single();

        Assert.AreEqual(result == "missing-build" ? "pending" : "human-validation-required",
            assessment.VerificationStatus);
        Assert.IsEmpty(assessment.Surfaces);
        var handoff = assessment.HumanValidationItems.Single();
        Assert.IsNull(handoff.StateId);
        StringAssert.Contains(handoff.ExpectedOutcome, "payload");
        CollectionAssert.AreEqual(todo.Guidance.Classification.DiscriminatingTests, handoff.Actions);
        if (result == "passed")
        {
            var completed = await UwpEnforcementRunner.SynchronizeReportAsync(
                fixture.Root, TestWindowOwnerResolver.Shared);
            Assert.AreEqual("passed", completed.Status);
            Assert.AreEqual(todo.Id, completed.HumanValidationItems.Single().TodoId);
            Assert.AreEqual("passed", (await UwpEnforcementRunner.AssessAsync(
                fixture.Root, TestWindowOwnerResolver.Shared)).Status);
        }
        else
        {
            await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
            var blocked = await UwpEnforcementRunner.AssessAsync(
                fixture.Root, TestWindowOwnerResolver.Shared);
            Assert.AreNotEqual("passed", blocked.Status);
            Assert.AreNotEqual("passed", blocked.Gates.Single(gate =>
                gate.Id == (result == "missing-build" ? "fresh-build" : "state-closure")).Status);
        }
        var current = await fixture.LoadReportAsync();
        Assert.AreEqual(1, current.MechanicalVerification.ActivationContracts.ReviewRequiredContracts);
        Assert.AreEqual("pending", current.Todos.Single().Status);
    }

    [TestMethod]
    public async Task TodoExecution_TerminalTodoLabelDoesNotReplaceCurrentEvidence()
    {
        using var fixture = await TodoFixtureAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos[0].Status = "verified";
        await fixture.SaveReportAsync(report);
        var assessment = (await TodoExecutionService.AssessAsync(fixture.Root, "UWMIG014")).Single();
        Assert.AreEqual("implemented", assessment.ImplementationStatus);
        Assert.AreEqual("pending", assessment.VerificationStatus);
    }

    [TestMethod]
    public async Task TodoExecution_OnlyExplicitCompletionMappingsDefineVerificationScope()
    {
        using var fixture = await TodoFixtureAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos[0].AffectedStates = ["another-surface"];
        await fixture.SaveReportAsync(report);
        var assessment = (await TodoExecutionService.AssessAsync(fixture.Root, "UWMIG014")).Single();
        Assert.AreEqual("state-1", assessment.Surfaces.Single().StateId);
        var json = JsonSerializer.SerializeToNode(assessment, EnforcementStore.JsonOptions)!;
        Assert.IsNotNull(json["verificationStatus"]);
        Assert.IsNotNull(json["surfaces"]![0]!["checkIds"]);
        Assert.IsNull(json["developmentStatus"]);
        Assert.IsNull(json["readyForCanonical"]);
    }

    [TestMethod]
    [DataRow("UWMIG001", "preserved-uwp-resource-contract-requires-merge", "app-resource-replay-passed")]
    [DataRow("UWMIG004", "preserved-uwp-startup-contract-requires-port", "startup-replay-passed")]
    public async Task TodoExecution_LegacySemanticObligationsRemainReadableAndRequireHumanValidation(
        string todoId, string reasonCode, string checkId)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        var todo = Uwmig014();
        todo.Id = todoId;
        todo.Guidance!.ReasonCode = reasonCode;
        todo.Guidance.CompletionChecks =
        [
            new()
            {
                Id = checkId, Summary = "Bind the actual startup surface.",
                VerificationKind = "surface-binding", Target = todoId, Expected = "passed",
                AssertionIds = [SurfaceCheckIds.TargetReachable, SurfaceCheckIds.StructureMatch]
            }
        ];
        report.Todos.Add(todo);
        await fixture.SaveReportAsync(report);
        var source = (await fixture.LoadAsync<StatePlanDocument>(ClosurePlan)).States.Single().Source;
        var sourceBefore = JsonSerializer.Serialize(source, EnforcementStore.JsonOptions);
        var sourceTreeHash = StateClosureProtocol.FileHash(fixture.Root, source.TreeEvidence!);

        var pending = (await TodoExecutionService.AssessAsync(fixture.Root, todoId)).Single();
        Assert.AreEqual("bind-surface", pending.NextOperation);
        Assert.HasCount(1, pending.HumanValidationItems);
        await TodoExecutionService.BindStateAsync(fixture.Root, todoId, "state-1", checkId,
            "The actual startup surface.", [SurfaceCheckIds.TargetReachable, SurfaceCheckIds.StructureMatch]);
        await TodoExecutionService.RecordImplementationAsync(fixture.Root, todoId, "implemented", "Ported the preserved source.");
        var probe = await CaptureClosureProbeAsync(fixture);
        await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);

        var snapshot = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        var assessment = (await TodoExecutionService.AssessAsync(
            fixture.Root, todoId, TestWindowOwnerResolver.Shared)).Single();

        Assert.AreEqual("implemented", assessment.ImplementationStatus);
        Assert.AreEqual("verified", assessment.Surfaces.Single().Status);
        Assert.AreEqual("human-validation-required", assessment.VerificationStatus);
        Assert.AreEqual(todoId, snapshot.HumanValidationItems.Single().TodoId);
        var current = (await fixture.LoadReportAsync()).Todos.Single();
        Assert.AreEqual("pending", current.Status);
        Assert.HasCount(1, current.Guidance!.CompletionChecks);
        Assert.AreEqual("surface-check", current.Guidance.CompletionChecks.Single().VerificationKind);
        Assert.AreEqual(sourceBefore, JsonSerializer.Serialize(
            (await fixture.LoadAsync<StatePlanDocument>(ClosurePlan)).States.Single().Source, EnforcementStore.JsonOptions));
        Assert.AreEqual(sourceTreeHash, StateClosureProtocol.FileHash(fixture.Root, source.TreeEvidence!));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task TodoExecution_StartupBindingResolvesActualInventoryWithoutPhantomChecks(
        bool legacy, bool reuseId)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        var todo = ProjectTodo();
        todo.Id = "UWMIG001";
        todo.Origin = "mechanical";
        todo.AffectedStates = legacy ? ["app-launch"] : [];
        todo.Guidance!.ReasonCode = "preserved-uwp-resource-contract-requires-merge";
        todo.Guidance.CompletionChecks.Add(new MigrationCompletionCheck
        {
            Id = "source-resource-contract-review", Summary = "Resource precedence and lookup require separate semantic evidence.",
            VerificationKind = "todo-resolution-evidence", Target = todo.Id, Expected = "terminal-with-evidence"
        });
        todo.Guidance.CompletionChecks.Add(new MigrationCompletionCheck
        {
            Id = "app-resource-replay-passed", Summary = "Bind the actual startup surface.",
            VerificationKind = legacy ? "surface-check" : "surface-binding",
            Target = legacy ? "app-launch" : todo.Id, Expected = "passed",
            AssertionIds = [SurfaceCheckIds.TargetReachable, SurfaceCheckIds.StructureMatch]
        });
        report.Todos.Add(todo);
        await fixture.SaveReportAsync(report);
        var pending = (await TodoExecutionService.AssessAsync(fixture.Root, todo.Id)).Single();
        Assert.AreEqual("pending", pending.VerificationStatus);
        Assert.AreEqual("bind-surface", pending.NextOperation);
        Assert.IsTrue(pending.HumanValidationItems.Any(item => item.CheckId == "source-resource-contract-review"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            TodoExecutionService.BindStateAsync(fixture.Root, todo.Id, "state-1",
                "app-resource-replay-passed", "Cannot weaken parity to reachability.",
                [SurfaceCheckIds.TargetReachable]));

        var bound = await TodoExecutionService.BindStateAsync(fixture.Root, todo.Id, "state-1",
            reuseId ? "app-resource-replay-passed" : "actual-startup", "The actual startup surface.",
            [SurfaceCheckIds.TargetReachable, SurfaceCheckIds.StructureMatch]);

        Assert.HasCount(3, bound.Guidance!.CompletionChecks);
        Assert.IsFalse(bound.AffectedStates.Contains("app-launch", StringComparer.Ordinal));
        var concrete = bound.Guidance.CompletionChecks.Single(check => check.VerificationKind == "surface-check");
        Assert.AreEqual("state-1", concrete.Target);
        Assert.IsFalse(bound.Guidance.CompletionChecks.Any(check => check.VerificationKind == "surface-binding"));
        var probe = await CaptureClosureProbeAsync(fixture);
        await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        var completed = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("passed", completed.Status);
    }

    [TestMethod]
    public async Task TodoExecution_BindingCannotRetargetAnAlreadyConcreteCheck()
    {
        using var fixture = await TodoFixtureAsync();
        var original = await fixture.LoadReportAsync();
        var id = original.Todos.Single().Guidance!.CompletionChecks.Single().Id;
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        plan.States.Add(new MigrationState
        {
            Id = "other", Kind = "page", SourceDefinitions = ["Other.xaml"],
            Verification = new StateVerificationContract { TreeProbeIndex = 0 },
            Source = new StateObservation { Status = "not-run" },
            Target = new StateObservation { Status = "not-run" },
            Comparison = new StateComparison { Status = "not-run" }
        });
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            TodoExecutionService.BindStateAsync(fixture.Root, "UWMIG014", "other", id,
                "Cannot silently move an existing obligation.", [SurfaceCheckIds.TargetReachable]));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            TodoExecutionService.BindStateAsync(fixture.Root, "UWMIG014", "state-1", id,
                "Cannot silently weaken an existing obligation.", [SurfaceCheckIds.TargetReachable]));
        var current = await fixture.LoadReportAsync();
        Assert.AreEqual("state-1", current.Todos.Single().Guidance!.CompletionChecks.Single().Target);
    }

    [TestMethod]
    public async Task TodoExecution_SynchronizationUsesExactScopeWithoutWeakeningSurfaceClosure()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        var reachable = Uwmig014();
        reachable.Id = "REACHABLE";
        reachable.Guidance!.CompletionChecks.Single().Id = "reachable-only";
        reachable.Guidance.CompletionChecks.Single().AssertionIds = [SurfaceCheckIds.TargetReachable];
        report.Todos.Add(reachable);
        report.Todos.Add(Uwmig014());
        await fixture.SaveReportAsync(report);
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(
            0, ClosureTree.Replace("Ready", "Changed", StringComparison.Ordinal), ""));
        await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);

        var assessments = await TodoExecutionService.AssessAsync(
            fixture.Root, windowOwnerResolver: TestWindowOwnerResolver.Shared);
        Assert.AreEqual("verified", assessments.Single(item => item.Todo.Id == reachable.Id).VerificationStatus);
        Assert.AreEqual("failed", assessments.Single(item => item.Todo.Id == "UWMIG014").VerificationStatus);
        var snapshot = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        var synchronized = await fixture.LoadReportAsync();
        Assert.AreEqual("verified", synchronized.Todos.Single(todo => todo.Id == reachable.Id).Status);
        Assert.AreEqual("pending", synchronized.Todos.Single(todo => todo.Id == "UWMIG014").Status);
        Assert.AreNotEqual("passed", snapshot.Gates.Single(gate => gate.Id == "state-closure").Status);

        var status = await UwpEnforcementRunner.StatusAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual(snapshot.TargetFingerprint, status.Closure.TargetFingerprint);
        Assert.AreEqual(1, status.Todos.Todos.Verified);
        Assert.AreEqual(1, status.Todos.Todos.Failed);
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        await File.AppendAllTextAsync(fixture.Path(plan.States.Single().Target.TreeEvidence!), "modified");
        var current = await UwpEnforcementRunner.StatusAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual(0, current.Todos.Todos.Verified);
        Assert.AreEqual(2, current.Todos.Todos.PendingVerification);
    }

    [TestMethod]
    public async Task TodoExecution_BindingPreservesProjectChecksAndExactSurfaceScope()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        var todo = ProjectTodo();
        report.Todos.Add(todo);
        await fixture.SaveReportAsync(report);
        var bound = await TodoExecutionService.BindStateAsync(
            fixture.Root, todo.Id, "state-1", "build-surface",
            "This surface exposes the migrated dependency's UI.", [SurfaceCheckIds.TargetReachable]);
        Assert.HasCount(2, bound.Guidance!.CompletionChecks);
        Assert.AreEqual("build-attestation", bound.Guidance.CompletionChecks[0].VerificationKind);
        CollectionAssert.AreEqual(new[] { SurfaceCheckIds.TargetReachable },
            bound.Guidance.CompletionChecks[1].AssertionIds);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => TodoExecutionService.BindStateAsync(
            fixture.Root, todo.Id, "state-1", "invalid-surface", "Cannot prove file reading.", ["file-read-success"]));
        Assert.HasCount(2, (await fixture.LoadReportAsync()).Todos.Single().Guidance!.CompletionChecks);
    }

    [TestMethod]
    public async Task TodoExecution_FailureHistoryDoesNotAuthorizeOrPreventAnotherImplementation()
    {
        using var fixture = await TodoFixtureAsync();
        var failed = await TodoExecutionService.RecordImplementationAsync(
            fixture.Root, "UWMIG014", "failed", "The source selection contract remains incomplete.",
            "selection-missing", "Preserve explicit selection handling.", ["selection-diagnostic.json"]);
        Assert.AreEqual("pending", failed.ImplementationStatus);
        Assert.AreEqual("selection-missing", failed.History.Single().FailureSignature);
        Assert.AreEqual("Preserve explicit selection handling.", failed.History.Single().RetryCondition);
        CollectionAssert.AreEqual(new[] { "selection-diagnostic.json" }, failed.History.Single().Evidence);
        var repaired = await TodoExecutionService.RecordImplementationAsync(
            fixture.Root, "UWMIG014", "implemented", "Preserved the selection contract.");
        Assert.AreEqual("implemented", repaired.ImplementationStatus);
        Assert.AreEqual("pending", repaired.VerificationStatus);
        Assert.AreEqual("selection-missing", repaired.History[0].FailureSignature);
        Assert.AreEqual("passed", repaired.History[1].Outcome);
    }

    [TestMethod]
    [DataRow("old-schema")]
    [DataRow("missing-schema")]
    [DataRow("old-event")]
    public async Task TodoExecution_RetiredProgressIsRejectedWithoutInferenceOrReplacement(string mutation)
    {
        using var fixture = await TodoFixtureAsync();
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root, "UWMIG014", "implemented", "Preserved source item presentation.");
        var progress = await fixture.LoadAsync<TodoExecutionProgressDocument>(EnforcementStore.TodoProgressPath);
        var node = JsonSerializer.SerializeToNode(progress, EnforcementStore.JsonOptions)!;
        if (mutation == "old-schema") node["schemaVersion"] = "2.4";
        if (mutation == "missing-schema") node.AsObject().Remove("schemaVersion");
        if (mutation == "old-event") node["todos"]![0]!["events"]![0]!["kind"] = "development-surface-verification";
        var original = node.ToJsonString();
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, EnforcementStore.TodoProgressPath, original);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            TodoExecutionService.AssessAsync(fixture.Root, "UWMIG014"));
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path(EnforcementStore.TodoProgressPath)));
    }

    [TestMethod]
    public async Task TodoExecution_DiscoveredObligationRetainsCategoryScopeAndEvidence()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var todo = await TodoExecutionService.DiscoverAsync(
            fixture.Root, "DISC001", "dispatcher", "required", "Preserve the source UI dispatch obligation.",
            "The migrated callback still needs UI-thread dispatch.", "implementation", "dispatcher-obligation",
            ["dispatcher-diagnostic.json"], ["Page.xaml.cs"], touchPaths: ["Page.xaml.cs"]);
        Assert.AreEqual("dispatcher", todo.Category);
        Assert.AreEqual("discovered", todo.Origin);
        Assert.AreEqual("dispatcher-obligation", todo.Discovery!.Signature);
        CollectionAssert.AreEqual(new[] { "Page.xaml.cs" }, todo.TouchSet.Paths);
        Assert.AreEqual("DISC001", (await fixture.LoadReportAsync()).Todos.Single().Id);
    }

    private static async Task<RunnerFixture> TodoFixtureAsync()
    {
        var fixture = await RunnerFixture.CreateAsync();
        try
        {
            var report = await fixture.LoadReportAsync();
            report.Todos.Add(Uwmig014());
            await fixture.SaveReportAsync(report);
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    private static MigrationCompletionCheck ProjectGraphCheck() => new()
    {
        Id = "target-project-graph-contained",
        Summary = "The target project graph has no ownership or portability issues.",
        VerificationKind = "report-field",
        Target = "mechanicalVerification.targetProjectGraph.status",
        Expected = "passed"
    };

    private static MigrationTodo ProjectTodo() => new()
    {
        Id = "UWMIG-BUILD", Category = "project-dependencies", Priority = "required",
        Summary = "Complete the target project graph.", Reason = "The target must build with its migrated dependencies.",
        Guidance = new MigrationRepairGuidance
        {
            ReasonCode = "project-closure",
            CompletionChecks =
            [
                new()
                {
                    Id = "target-build-passed", Summary = "The current target build passes.",
                    VerificationKind = "build-attestation", Target = "target-build", Expected = "passed"
                }
            ]
        }
    };

    private static MigrationTodo Uwmig014() => new()
    {
        Id = "UWMIG014", Category = "navigation-view-item-template", Priority = "required",
        Summary = "Preserve NavigationView item-template fallback behavior.",
        Reason = "The source uses native items, a separator, and custom items.",
        Locations = [new MigrationLocation { Path = "OuterNav/OuterNavPage.xaml" }],
        AffectedStates = ["state-1"],
        TouchSet = new MigrationTodoTouchSet { Paths = ["todo-relevant.txt"] },
        Guidance = new MigrationRepairGuidance
        {
            ReasonCode = "navigation-view-item-template",
            CandidateStrategies =
            [
                new() { Id = "preserve-native-items", Summary = "Preserve native items and template custom items." }
            ],
            CompletionConditions = ["Native, separator, custom, and selected items preserve source behavior."],
            CompletionChecks =
            [
                new()
                {
                    Id = "uwmig014-main", Summary = "The mapped surface preserves source structure.",
                    VerificationKind = "surface-check", Target = "state-1", Expected = "passed",
                    AssertionIds = [SurfaceCheckIds.TargetReachable, SurfaceCheckIds.StructureMatch]
                }
            ]
        }
    };
}
