// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinAppMigration.Cli.Pipelines.Uwp;
using WinAppMigration.Enforcement;
using UwpMigration.Contracts;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    private const string ClosureTree =
        """{"windows":[{"elements":[{"type":"Window","automationId":"main","name":"Ready"}]}]}""";
    private const string ClosurePlan =
        ".migration-evidence/state-plan.json";

    [TestMethod]
    public async Task SurfaceClosure_CliDerivesAndFinalizesCanonicalTrees()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            BuildSuccess(),
            ReplaySuccess(),
            new EnforcementProcessResult(0, ClosureTree, ""));
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(
            fixture.Root,
            new UwpEnforcementOptions());
        var command =
            new UwpEnforceCommand(
                process,
                new TestWindowOwnerResolver(4242));
        var probe = await runner.InvokeUiAsync(
            fixture.Root,
            "winapp",
            ["inspect", "--json", "-w", "42"],
            [],
            "state-1");
        var recorded = command.Parse(
            [
                "record-surface",
                fixture.Root.FullName,
                "--surface",
                "state-1",
                "--probe",
                probe.ArtifactPath
            ]);
        Assert.AreEqual(
            0,
            await recorded.InvokeAsync(
                recorded.InvocationConfiguration,
                CancellationToken.None));
        var recordedSnapshot =
            await UwpEnforcementRunner.AssessAsync(
                fixture.Root,
                TestWindowOwnerResolver.Shared);
        Assert.AreEqual(
            "passed",
            recordedSnapshot.Gates.Single(gate =>
                gate.Id == "state-closure").Status);
        var completed =
            await UwpEnforcementRunner.SynchronizeReportAsync(
                fixture.Root,
                TestWindowOwnerResolver.Shared);
        Assert.AreEqual("complete", completed.Disposition?.Kind,
            JsonSerializer.Serialize(completed.Gates, EnforcementStore.JsonOptions));
        Assert.AreEqual(
            ClosureTree,
            await File.ReadAllTextAsync(
                fixture.Path(
                    ".migration-evidence/target/state-1-ui.json")));
        Assert.IsFalse(
            File.Exists(
                fixture.Path(
                    ".migration-evidence/target/state-1.png")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SurfaceClosure_FinalizesAuthenticatedFailureWithoutDeclaringParity(bool unsupportedHost)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var tree = unsupportedHost
            ? """
              {"windows":[{"elements":[{"type":"Window","automationId":"main","name":"Ready","className":"WinUIDesktopWin32WindowClass","children":[
                {"type":"Pane","className":"Microsoft.UI.Content.DesktopChildSiteBridge","children":[
                  {"type":"Pane","className":"InputSiteWindowClass","children":[{"type":"Text","name":"Ready"}]}]}
              ]}]}]}
              """
            : ClosureTree.Replace("Ready", "Changed", StringComparison.Ordinal);
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(0, tree, ""));
        await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);

        var synchronized = await UwpEnforcementRunner.SynchronizeReportAsync(
            fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreEqual("blocked", synchronized.Status);
        Assert.AreEqual(unsupportedHost ? "tool-blocked" : "continue", synchronized.Disposition?.Kind);
        Assert.AreEqual("passed", synchronized.Gates.Single(gate => gate.Id == "report-sync").Status);
        Assert.AreEqual("blocked", synchronized.Gates.Single(gate => gate.Id == "state-closure").Status);
        Assert.AreEqual("blocked", synchronized.Gates.Single(gate => gate.Id == "report-closure").Status);
        Assert.AreEqual(
            unsupportedHost ? SurfaceVerificationResult.HumanValidationRequired : SurfaceVerificationResult.Mismatch,
            synchronized.StateVerifications.Single().Result);
        var report = await fixture.LoadReportAsync();
        Assert.AreEqual("captured", report.Validation.SourceBaseline.Status);
        Assert.AreEqual("passed", report.Validation.TargetReplay.Status);
        Assert.AreEqual(unsupportedHost ? "unverified" : "failed", report.Validation.ParityStatus);
        Assert.AreEqual(tree, await File.ReadAllTextAsync(fixture.Path(".migration-evidence/target/state-1-ui.json")));

        var strict = new UwpEnforceCommand(new FakeProcessRunner(), TestWindowOwnerResolver.Shared)
            .Parse(["finalize", fixture.Root.FullName, "--strict"]);
        Assert.AreNotEqual(0, await strict.InvokeAsync(strict.InvocationConfiguration, CancellationToken.None));
    }

    [TestMethod]
    public async Task SurfaceClosure_CampaignPreservesIndependentSurfacesAcrossAuthenticatedReplays()
    {
        using var fixture = await CreateCampaignFixtureAsync(2);
        await fixture.RunTargetAsync();
        var first = await CaptureCampaignSurfaceAsync(fixture, "state-1", ClosureTree);
        Assert.AreEqual(SurfaceVerificationResult.Verified,
            first.StateVerifications.Single(item => item.Id == "state-1").Result);
        var firstReplay = first.Replay!.Id;
        var resolver = new TestWindowOwnerResolver(5252,
            startedAt: TestWindowOwnerResolver.ProcessStartedAt.AddDays(1));
        var replayRunner = new UwpEnforcementRunner(new FakeProcessRunner(
            ReplaySuccess() with { Output = ReplaySuccess().Output.Replace("4242", "5252", StringComparison.Ordinal) }),
            windowOwnerResolver: resolver);
        var relaunched = await replayRunner.RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreNotEqual(firstReplay, relaunched.Replay!.Id);
        Assert.AreEqual(SurfaceVerificationResult.Verified,
            relaunched.StateVerifications.Single(item => item.Id == "state-1").Result);
        var second = await CaptureCampaignSurfaceAsync(fixture, "state-2", ClosureTree, resolver);
        Assert.IsTrue(second.StateVerifications.All(item => item.Result == SurfaceVerificationResult.Verified));
        Assert.IsTrue(second.Telemetry!.CoverageProgress);
        Assert.IsTrue(second.Telemetry.ParityProgress);
        Assert.IsTrue(second.Telemetry.ActivityObserved);
        var observedAgain = await UwpEnforcementRunner.AssessAsync(fixture.Root, resolver);
        Assert.IsFalse(observedAgain.Telemetry!.MeasuredProgress);
        Assert.IsFalse(observedAgain.Telemetry.ActivityObserved);
        var completed = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, resolver);
        Assert.AreEqual("complete", completed.Disposition?.Kind,
            JsonSerializer.Serialize(completed.Gates, EnforcementStore.JsonOptions));
        Assert.IsTrue(completed.Gates.All(gate => gate.Status == "passed"));
    }

    [TestMethod]
    public async Task SurfaceClosure_CampaignFinalizesVerifiedStaleUnsupportedAndMissingResultsHonestly()
    {
        using var fixture = await CreateCampaignFixtureAsync(4);
        await fixture.RunTargetAsync();
        await CaptureCampaignSurfaceAsync(fixture, "state-1", ClosureTree);
        await CaptureCampaignSurfaceAsync(fixture, "state-3", ClosureTree);
        var legacyPath = StateClosureProtocol.AttestationPath("state-3");
        var legacy = await fixture.LoadAsync<StateClosureAttestation>(legacyPath);
        legacy.SchemaVersion = "2.0";
        legacy.ComparisonRules = null;
        StateClosureSeal.Sign(legacy);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, legacyPath, legacy);
        await new UwpEnforcementRunner(new FakeProcessRunner(ReplaySuccess()))
            .RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        const string unsupported = """
            {"windows":[{"elements":[{"type":"Window","automationId":"main","name":"Ready",
            "className":"WinUIDesktopWin32WindowClass","children":[{"type":"Pane","className":"UnknownNativeHost",
            "children":[{"type":"Text","name":"Ready"}]}]}]}]}
            """;
        var recorded = await CaptureCampaignSurfaceAsync(fixture, "state-2", unsupported);
        Assert.AreEqual(SurfaceVerificationResult.HumanValidationRequired,
            recorded.StateVerifications.Single(item => item.Id == "state-2").Result);
        var synchronized = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("passed", synchronized.Gates.Single(gate => gate.Id == "report-sync").Status);
        Assert.AreEqual("needs-work", synchronized.Completion!.Outcome);
        CollectionAssert.AreEquivalent(new[] { "state-3", "state-4" }, synchronized.Completion.ActionableSurfaceIds.ToArray());
        CollectionAssert.AreEqual(new[] { "state-2" }, synchronized.Completion.ToolBlockedSurfaceIds.ToArray());
        var report = await fixture.LoadReportAsync();
        Assert.AreEqual("partial", report.Validation.ParityStatus);
        CollectionAssert.AreEqual(new[] { "passed", "passed", "unverified", "not-run" },
            report.Validation.TargetReplay.States.Select(state => state.Status).ToArray());
        Assert.IsFalse(File.Exists(fixture.Path(".migration-evidence/target/state-3-ui.json")));
        var strict = new UwpEnforceCommand(new FakeProcessRunner(), TestWindowOwnerResolver.Shared)
            .Parse(["finalize", fixture.Root.FullName, "--strict"]);
        Assert.AreNotEqual(0, await strict.InvokeAsync(strict.InvocationConfiguration, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("archive")]
    [DataRow("raw-output")]
    [DataRow("source")]
    [DataRow("rules")]
    [DataRow("inputs")]
    [DataRow("target")]
    public async Task SurfaceClosure_CampaignRejectsChangedOrUntrustedEvidence(string change)
    {
        using var fixture = await CreateCampaignFixtureAsync(2);
        await fixture.RunTargetAsync();
        var recorded = await CaptureCampaignSurfaceAsync(fixture, "state-1", ClosureTree);
        var path = StateClosureProtocol.AttestationPath("state-1");
        var proof = await fixture.LoadAsync<StateClosureAttestation>(path);
        if (change == "archive")
            await File.AppendAllTextAsync(fixture.Path(CanonicalAttestationTrust.ReplayArchivePath(recorded.Replay!.Id)), " ");
        else if (change == "raw-output")
            await File.AppendAllTextAsync(fixture.Path(proof.TargetTreeEvidence!), " ");
        else if (change == "source")
            await File.AppendAllTextAsync(fixture.Path(proof.SourceEvidenceHashes[0].Path), " ");
        else if (change == "rules")
        {
            proof.ComparisonRules = "another-comparison-build";
            StateClosureSeal.Sign(proof);
            await EnforcementStore.WriteAtomicAsync(fixture.Root, path, proof);
        }
        else if (change == "inputs")
        {
            await new UwpEnforcementRunner(new FakeProcessRunner(BuildSuccess(), ReplaySuccess()))
                .RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true, Architecture = "x86" });
        }
        else
            await File.AppendAllTextAsync(fixture.Path("MainPage.xaml"), "\n");
        var assessed = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("unverified", assessed.StateVerifications.Single(item => item.Id == "state-1").Level);
        Assert.AreNotEqual("passed", assessed.Gates.Single(gate => gate.Id == "state-closure").Status);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SurfaceClosure_NestedUnsupportedComparisonHasConsistentHandoffsAndRetryPolicy(bool applicationDifference)
    {
        var (source, target) = SurfaceTreeComparisonTests.NavigationViewTrees();
        target["windows"]![0]!["elements"]![0]!["children"]![0]!["children"]![0]!["children"]![0]!["children"]![1]!["automationId"] =
            "UnrecognizedScroll";
        if (applicationDifference)
            target["windows"]![0]!["elements"]![0]!["name"] = "Changed application name";
        using var fixture = await CreateCampaignFixtureAsync(1, source.ToJsonString());
        await fixture.RunTargetAsync();
        var recorded = await CaptureCampaignSurfaceAsync(fixture, "state-1", target.ToJsonString());
        Assert.AreEqual(applicationDifference ? SurfaceVerificationResult.Mismatch : SurfaceVerificationResult.HumanValidationRequired,
            recorded.StateVerifications.Single().Result);
        Assert.IsTrue(recorded.HumanValidationItems.Any(item => item.StateId == "state-1"));
        var finalized = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual(applicationDifference ? "continue" : "tool-blocked", finalized.Disposition?.Kind);
        Assert.IsFalse(finalized.Disposition!.Terminal);
        Assert.AreEqual(applicationDifference, finalized.Disposition.Retryable);
        if (!applicationDifference)
        {
            var process = new FakeProcessRunner();
            await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
            Assert.AreEqual(0, process.CallCount);
            await File.AppendAllTextAsync(fixture.Path("MainPage.xaml"), "\n");
            var reopened = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
            Assert.AreEqual("continue", reopened.Disposition?.Kind);
            Assert.IsTrue(reopened.Disposition!.AllowedOperations.Contains(WorkflowOperation.Build));
        }
    }

    private static async Task<RunnerFixture> CreateCampaignFixtureAsync(int count, string? sourceTree = null)
    {
        var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        for (var index = 2; index <= count; index++)
        {
            var definition = $"Page{index}.xaml";
            await File.WriteAllTextAsync(fixture.Path(definition),
                """<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />""");
            plan.States.Add(new MigrationState
            {
                Id = $"state-{index}",
                SourceDefinitions = [definition],
                Verification = new StateVerificationContract { TreeProbeIndex = 0 },
                Source = new StateObservation { Status = "not-run" },
                Target = new StateObservation { Status = "not-run" },
                Comparison = new StateComparison { Status = "not-run" }
            });
        }
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        foreach (var state in plan.States)
        {
            var source = $".migration-evidence/states/{state.Id}/source-ui.json";
            await EnforcementStore.WriteTextAtomicAsync(fixture.Root, source, sourceTree ?? ClosureTree);
            state.Source = new StateObservation { Status = "captured", Evidence = [source], TreeEvidence = source };
            await SourceNavigationContracts.EstablishAsync(fixture.Root, state, "fixture-source",
                state.Source.Evidence, DateTimeOffset.UtcNow, default);
        }
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        return fixture;
    }

    private static async Task<EnforcementSnapshot> CaptureCampaignSurfaceAsync(
        RunnerFixture fixture, string stateId, string tree, IWindowOwnerResolver? resolver = null)
    {
        resolver ??= TestWindowOwnerResolver.Shared;
        var probe = await new UwpEnforcementRunner(new FakeProcessRunner(new EnforcementProcessResult(0, tree, "")),
            windowOwnerResolver: resolver).InvokeUiAsync(
                fixture.Root, "winapp", ["inspect", "--json", "-w", "42"], [], stateId);
        return await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, resolver, stateId, [probe.ArtifactPath]);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task SurfaceClosure_LockContentionReturnsARetryableStructuredError()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await using var held = await EnforcementStore.AcquireLockAsync(fixture.Root);
        var command = new UwpEnforceCommand(new FakeProcessRunner(), TestWindowOwnerResolver.Shared)
            .Parse(["assess", fixture.Root.FullName]);
        var original = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            Assert.AreEqual(1, await command.InvokeAsync(command.InvocationConfiguration, CancellationToken.None));
        }
        finally { Console.SetOut(original); }
        using var json = JsonDocument.Parse(writer.ToString());
        Assert.AreEqual("enforcement-busy", json.RootElement.GetProperty("errorCode").GetString());
        Assert.IsTrue(json.RootElement.GetProperty("retryable").GetBoolean());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SurfaceClosure_WindowsProbePathsRetainCanonicalEvidence(bool dotPrefix)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var probe = await CaptureClosureProbeAsync(fixture);
        var windowsPath = (dotPrefix ? ".\\" : "") + probe.ArtifactPath.Replace('/', '\\');
        await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [windowsPath]);
        var proof = await fixture.LoadAsync<StateClosureAttestation>(
            StateClosureProtocol.AttestationPath("state-1"));
        Assert.AreEqual(probe.ArtifactPath, proof.Probes.Single());
        Assert.AreEqual(probe.StandardOutputPath, proof.TargetTreeEvidence);
        Assert.IsTrue(StateClosureSeal.Verify(proof));
        Assert.AreEqual("passed",
            (await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared)).Status);
    }

    [TestMethod]
    public async Task SurfaceClosure_ProbePathAliasesCannotBypassDuplicateOrContainmentChecks()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var probe = await CaptureClosureProbeAsync(fixture);
        foreach (var paths in new[]
        {
            new[] { probe.ArtifactPath, probe.ArtifactPath.Replace('/', '\\') },
            new[] { "..\\" + probe.ArtifactPath.Replace('/', '\\') },
            new[] { fixture.Path(probe.ArtifactPath) }
        })
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                UwpEnforcementRunner.RecordSurfaceAsync(
                    fixture.Root, TestWindowOwnerResolver.Shared, "state-1", paths));
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("replay")]
    [DataRow("state")]
    [DataRow("modified-output")]
    [DataRow("duplicate")]
    public async Task SurfaceClosure_RejectsUntrustedOrStaleProbes(
        string invalid)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var probe = await CaptureClosureProbeAsync(fixture);
        var path = probe.ArtifactPath;
        if (invalid == "fingerprint")
            probe.TargetFingerprint = "wrong";
        if (invalid == "replay")
            probe.ReplayAttestationId = "wrong";
        if (invalid == "state")
            probe.StateId = "another-surface";
        if (invalid == "modified-output")
        {
            await File.WriteAllTextAsync(
                fixture.Path(probe.StandardOutputPath),
                ClosureTree + " ");
        }
        if (invalid is "fingerprint" or "replay" or "state")
        {
            await EnforcementStore.WriteAtomicAsync(
                fixture.Root,
                path,
                probe);
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordSurfaceAsync(
                fixture.Root,
                TestWindowOwnerResolver.Shared,
                "state-1",
                invalid == "duplicate"
                    ? [path, path]
                    : [path]));
    }

    [TestMethod]
    [DataRow("structured-failure")]
    [DataRow("text")]
    [DataRow("empty-tree")]
    [DataRow("duplicate-json-keys")]
    public async Task SurfaceClosure_UnsupportedTreeEvidenceIsInconclusive(
        string invalid)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var output = invalid switch
        {
            "structured-failure" =>
                """{"success":false,"windows":[]}""",
            "text" => "passed verified true",
            "empty-tree" => """{"windows":[]}""",
            "duplicate-json-keys" =>
                """{"elements":[],"elements":[{"type":"Window"}]}""",
            _ => ClosureTree
        };
        var probe = await CaptureClosureProbeAsync(
            fixture,
            new EnforcementProcessResult(0, output, ""));

        if (invalid == "structured-failure")
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                UwpEnforcementRunner.RecordSurfaceAsync(
                    fixture.Root,
                    TestWindowOwnerResolver.Shared,
                    "state-1",
                    [probe.ArtifactPath]));
            return;
        }
        var snapshot =
            await UwpEnforcementRunner.RecordSurfaceAsync(
                fixture.Root,
                TestWindowOwnerResolver.Shared,
                "state-1",
                [probe.ArtifactPath]);
        Assert.AreEqual(
            StateVerificationLevel.EvidenceValid,
            snapshot.StateVerifications.Single().Level);
        Assert.AreEqual(
            SurfaceVerificationResult.HumanValidationRequired,
            snapshot.StateVerifications.Single().Result);
        Assert.AreEqual("state-1", snapshot.HumanValidationItems.Single().StateId);
    }

    [TestMethod]
    public async Task SurfaceClosure_NotAttemptedAndConfirmedMismatchRemainDistinct()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var pending = await UwpEnforcementRunner.AssessAsync(
            fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual(SurfaceVerificationResult.Pending, pending.StateVerifications.Single().Result);
        var probe = await CaptureClosureProbeAsync(fixture,
            new EnforcementProcessResult(0,
                """{"windows":[{"elements":[{"type":"Window","automationId":"main","name":"Changed"}]}]}""", ""));
        var failed = await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        Assert.AreEqual(SurfaceVerificationResult.Mismatch, failed.StateVerifications.Single().Result);
        Assert.AreEqual("blocked", failed.Gates.Single(gate => gate.Id == "state-closure").Status);
        Assert.AreEqual("application", failed.Frontier!.RepairOwner);
        Assert.AreEqual("name", failed.Frontier.ComparisonDiagnostics!.Single().Property);
        StringAssert.Contains(failed.Frontier.SuggestedAction!, "restore the target");
        StringAssert.Contains(failed.Frontier.Reason, "Changed");
        Assert.AreEqual(failed.StateVerifications.Single().ComparisonDiagnostics!.Single(),
            failed.Frontier.ComparisonDiagnostics!.Single());

        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        var proofPath = plan.States.Single().Target.Attestation!;
        var proof = await fixture.LoadAsync<StateClosureAttestation>(proofPath);
        proof.ComparisonDiagnostics![0] = proof.ComparisonDiagnostics[0] with { RepairOwner = "tool" };
        await EnforcementStore.WriteAtomicAsync(fixture.Root, proofPath, proof);
        var tampered = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual(SurfaceVerificationResult.Pending, tampered.StateVerifications.Single().Result);
        Assert.IsNull(tampered.Frontier?.ComparisonDiagnostics);
    }

    [TestMethod]
    [DataRow("temporal-text", "capture", SurfaceVerificationResult.Mismatch)]
    [DataRow("unsupported-host", "tool", SurfaceVerificationResult.HumanValidationRequired)]
    public async Task SurfaceClosure_RoutesInputReviewAndToolHandoffs(
        string problem, string owner, string expectedResult)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var target = ClosureTree.Replace("Ready", "11/12/2026 4:21 PM", StringComparison.Ordinal);
        if (problem == "temporal-text")
        {
            var sourcePlan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
            await File.WriteAllTextAsync(fixture.Path(sourcePlan.States.Single().Source.TreeEvidence!),
                ClosureTree.Replace("Ready", "11/12/2026 3:54 PM", StringComparison.Ordinal));
        }
        else
        {
            target = """
                {"windows":[{"elements":[{"type":"Window","name":"Ready","className":"WinUIDesktopWin32WindowClass","children":[
                  {"type":"Pane","className":"Microsoft.UI.Content.DesktopChildSiteBridge","children":[
                    {"type":"Pane","className":"InputSiteWindowClass","children":[{"type":"Text","name":"Ready"}]}]}
                ]}]}]}
                """;
        }
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(0, target, ""));

        var snapshot = await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);

        Assert.AreEqual(expectedResult, snapshot.StateVerifications.Single().Result);
        Assert.AreEqual(owner, snapshot.Frontier!.RepairOwner);
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        Assert.AreEqual(plan.States.Single().Source.TreeEvidence,
            snapshot.Frontier.ComparisonDiagnostics!.Single().SourceEvidence);
        var handoff = snapshot.HumanValidationItems.Single(item => item.StateId == "state-1");
        if (problem == "unsupported-host")
        {
            Assert.IsTrue(handoff.Actions.Any(action => action.Contains("Request support", StringComparison.Ordinal)));
        }
        else
        {
            CollectionAssert.AreEqual(new[] { "state-1" }, snapshot.Completion!.InputReviewSurfaceIds.ToArray());
            Assert.IsTrue(handoff.Actions.Any(action => action.Contains("comparable inputs", StringComparison.Ordinal)));
            StringAssert.Contains(snapshot.Frontier.SuggestedAction!, "No text was ignored");
        }
        var html = await File.ReadAllTextAsync(fixture.Path(".migration-evidence/comparison/index.html"));
        StringAssert.Contains(html, "Comparison diagnostics");
        StringAssert.Contains(html, snapshot.Frontier.ComparisonDiagnostics!.Single().Kind);
    }

    [TestMethod]
    public async Task SurfacePlan_ObservationEnumsAreValidatedBeforeSummary()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var plan =
            await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        plan.States[0].Comparison.Status = "agent-passed";
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);

        var snapshot =
            await UwpEnforcementRunner.AssessAsync(
                fixture.Root,
                TestWindowOwnerResolver.Shared);

        Assert.AreEqual(
            "blocked",
            snapshot.Gates.Single(
                gate => gate.Id == "state-plan").Status);
    }

    private static async Task<UiDevelopmentProbe>
        CaptureClosureProbeAsync(
            RunnerFixture fixture,
            EnforcementProcessResult? result = null,
            string? stateId = "state-1",
            IReadOnlyList<string>? arguments = null)
    {
        var plan =
            await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        ContractTestAuthority.Prepare(fixture.Root, plan);
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(
            fixture.Root,
            new UwpEnforcementOptions
            {
                ForceReplay = true
            });
        return await new UwpEnforcementRunner(
            new FakeProcessRunner(
                result ??
                new EnforcementProcessResult(0, ClosureTree, "")),
            windowOwnerResolver:
                new TestWindowOwnerResolver(4242))
            .InvokeUiAsync(
                fixture.Root,
                "winapp",
                arguments ??
                    ["inspect", "--json", "-w", "42"],
                [],
                stateId);
    }
}
