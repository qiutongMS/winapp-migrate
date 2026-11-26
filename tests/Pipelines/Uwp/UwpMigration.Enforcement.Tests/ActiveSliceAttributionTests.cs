// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class EnforcementEvaluatorTests
{
    [TestMethod]
    [DataRow("none")]
    [DataRow("verified-prior")]
    [DataRow("unresolved-current")]
    public void Attribution_IdlessRecoveryIsRejectedWithoutInferringHistory(string fallback)
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        var next = State("category-selection");
        next.Slice = "category-slice";
        input.StatePlan!.States.Add(next);
        var progress = fallback == "none" ? null : new WorkflowProgress
        {
            ActiveStateId = fallback == "verified-prior" ? "state-1" : "category-selection",
            ActiveSliceId = fallback == "verified-prior" ? "state-1" : "category-slice"
        };
        var failed = new ReplayAttestation
        {
            Id = "legacy-failure", RunId = "legacy-run", BuildAttestationId = "build-1",
            TargetFingerprint = input.TargetFingerprint, Success = false, ExitCode = 1, CompletedAt = input.GeneratedAt,
            StandardOutputPath = "replay-stdout.json", StandardErrorPath = "replay-stderr.log"
        };
        var attempts = new AttemptLedger();

        Assert.ThrowsExactly<InvalidDataException>(() =>
            RuntimeFailureRecovery.Recover(attempts, failed, null, input with { Progress = progress }));
        Assert.IsEmpty(attempts.Attempts);
    }

    [TestMethod]
    [DataRow("missing-state", "state-1")]
    [DataRow("state-1", "wrong-slice")]
    [DataRow("state-1", null)]
    public void Attribution_UnknownOrInconsistentLegacyTuplesAreRejected(string state, string? slice)
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        var failure = new DiagnosticRunAttestation
        {
            Id = "legacy", TargetFingerprint = input.TargetFingerprint, StateId = state, SliceId = slice,
            ExitCode = 1, CompletedAt = input.GeneratedAt,
            StandardOutputPath = "replay-stdout.json", StandardErrorPath = "replay-stderr.log"
        };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            RuntimeFailureRecovery.Recover(new AttemptLedger(), null, failure, input));
    }

    [TestMethod]
    public void Attribution_FrontierAdvisesWithoutRejectingAnotherDeclaredState()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input.StatePlan!.States.Add(State("later-state"));
        input.Attempts.Attempts.Add(new EnforcementAttempt
            {
                Kind = "runtime", Signature = "mapped-regression", TargetFingerprint = input.TargetFingerprint,
                StateId = "state-1", SliceId = "state-1", RecordedAt = input.GeneratedAt, Evidence = ["evidence.txt"]
            });

        var snapshot = EnforcementEvaluator.Evaluate(input);
        var binding = StateAttribution.Authorize(input, "state-1");
        var laterBinding = StateAttribution.Authorize(input, "later-state");

        Assert.AreEqual("later-state", snapshot.ActiveSlice);
        Assert.AreEqual("state-1", binding.SliceId);
        Assert.AreEqual("state-1", binding.StateId);
        Assert.AreEqual(new StateSliceBinding("later-state", "later-state"), laterBinding);
    }

    [TestMethod]
    public void Attribution_MultipleUnresolvedStatesRemainSliceOnly()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        var first = State("branch-a"); first.Slice = "branch-slice";
        var second = State("branch-b"); second.Slice = first.Slice;
        input.StatePlan!.States.AddRange([first, second]);

        Assert.AreEqual(new StateSliceBinding(null, first.Slice), StateAttribution.Authorize(input));
        Assert.AreEqual(new StateSliceBinding(second.Id, first.Slice), StateAttribution.Authorize(input, second.Id));
    }

    [TestMethod]
    [DataRow("null-target")]
    [DataRow("duplicate-id")]
    [DataRow("unknown-dependency")]
    public void Attribution_MalformedPlansBlockWithoutCrashingRecovery(string malformed)
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        var state = input.StatePlan!.States[0];
        if (malformed == "null-target") state.Target = null!;
        if (malformed == "duplicate-id") input.StatePlan.States.Add(state);
        if (malformed == "unknown-dependency") state.Dependencies.Add("unknown");
        input = input with { Progress = new WorkflowProgress
        {
            ActiveStateId = state.Id, ActiveSliceId = state.Id
        } };

        if (malformed == "null-target")
            Assert.IsFalse(StateClosureProtocol.HasHistoricalReachability(input.TargetRoot, state));
        Assert.ThrowsExactly<InvalidDataException>(() => StateAttribution.Authorize(input));
        var snapshot = EnforcementEvaluator.Evaluate(input with { Progress = null });
        Assert.AreEqual("blocked", snapshot.Gates.Single(gate => gate.Id == "state-plan").Status);
    }

    [TestMethod]
    public void Attribution_EqualSignaturesDoNotCollapseDistinctOperationTuples()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input.StatePlan!.States.Add(State("later-state"));
        var (progress, _) = EnforcementStore.TryLoadAsync<WorkflowProgress>(input.TargetRoot, EnforcementStore.ProgressPath)
            .GetAwaiter().GetResult();
        var earlier = progress!.Operations[^1];
        var diagnostic = new DiagnosticRunAttestation
        {
            Id = "later-diagnostic", OperationId = "later-operation", Signature = "same-signature",
            TargetFingerprint = input.TargetFingerprint, StateId = "later-state", SliceId = "later-state",
            StartedAt = input.GeneratedAt.AddSeconds(-1), CompletedAt = input.GeneratedAt, ExitCode = 1,
            StandardOutputPath = "replay-stdout.json", StandardErrorPath = "replay-stderr.log"
        };
        StateClosureSeal.Sign(diagnostic);
        EnforcementStore.WriteAtomicAsync(input.TargetRoot, EnforcementStore.DiagnosticAttestationPath, diagnostic).GetAwaiter().GetResult();
        progress.Operations.Add(new WorkflowOperationRecord
        {
            Id = diagnostic.OperationId, Kind = WorkflowOperation.DiagnosticRun, Stage = WorkflowStage.SemanticConvergence,
            StateId = diagnostic.StateId, IntendedSliceId = diagnostic.SliceId,
            TargetFingerprint = input.TargetFingerprint, RecordedAt = diagnostic.StartedAt,
            ArtifactId = diagnostic.Id, ArtifactSha256 = StateClosureProtocol.FileHash(input.TargetRoot, EnforcementStore.DiagnosticAttestationPath)
        });
        ContractTestAuthority.SealOperations(progress, input.TargetRoot);
        EnforcementStore.WriteAtomicAsync(input.TargetRoot, EnforcementStore.ProgressPath, progress).GetAwaiter().GetResult();
        input.Attempts.Attempts.Add(new EnforcementAttempt
        {
            Kind = "runtime", Signature = diagnostic.Signature, OperationId = earlier.Id,
            StateId = earlier.StateId, SliceId = earlier.IntendedSliceId,
            TargetFingerprint = input.TargetFingerprint, RecordedAt = earlier.RecordedAt, Evidence = ["evidence.txt"]
        });

        Assert.IsTrue(RuntimeFailureRecovery.Recover(input.Attempts, null, diagnostic, input));
        Assert.IsFalse(RuntimeFailureRecovery.Recover(input.Attempts, null, diagnostic, input));
        Assert.HasCount(2, input.Attempts.Attempts);
        Assert.AreEqual("later-state", input.Attempts.Attempts[1].StateId);
        Assert.AreEqual(diagnostic.OperationId, input.Attempts.Attempts[1].OperationId);
    }
}

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    public async Task Attribution_CorrectiveBuildRetainsHistoricalFailureProvenance()
    {
        using var fixture = await AttributionFixtureAsync();
        const string evidence = ".migration-evidence/corrective-replay.txt";
        await File.WriteAllTextAsync(fixture.Path(evidence), "A controlled replay of the current selection.");
        var failed = await new UwpEnforcementRunner(new FakeProcessRunner(new EnforcementProcessResult(1, "", "selection launch failed")))
            .RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        var attempt = (await fixture.LoadAsync<AttemptLedger>(EnforcementStore.AttemptLedgerPath)).Attempts.Single();
        await File.AppendAllTextAsync(fixture.ProjectPath, Environment.NewLine);
        var process = SuccessfulLaunch();

        var corrected = await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());

        Assert.AreEqual(2, process.CallCount);
        Assert.IsTrue(corrected.Replay!.Success);
        Assert.AreNotEqual(failed.Replay!.BuildAttestationId, corrected.Replay.BuildAttestationId);
        Assert.AreEqual("category-selection", corrected.Replay.StateId);
        Assert.AreEqual("passed", corrected.Gates.Single(gate => gate.Id == "enforcement-artifacts").Status);
        var history = (await fixture.LoadAsync<AttemptLedger>(EnforcementStore.AttemptLedgerPath)).Attempts;
        Assert.AreEqual(JsonSerializer.Serialize(attempt, EnforcementStore.JsonOptions),
            JsonSerializer.Serialize(history.Single(), EnforcementStore.JsonOptions));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Attribution_CategoryReplayCapturesItsIntentBeforeCompletion(bool failure)
    {
        using var fixture = await AttributionFixtureAsync();
        const string evidence = ".migration-evidence/category-replay-intent.txt";
        await File.WriteAllTextAsync(fixture.Path(evidence), "A controlled replay of the current unresolved selection.");
        var result = failure ? new EnforcementProcessResult(1, """{"Error":"selection launch failed"}""", "") : ReplaySuccess();
        var runner = new UwpEnforcementRunner(new FakeProcessRunner(result));

        var replayed = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions
        { ForceReplay = true });

        Assert.AreEqual("category-selection", replayed.Replay!.StateId);
        Assert.AreEqual("category-slice", replayed.Replay.SliceId);
        var history = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        var operation = history.Operations.Single(item => item.Id == replayed.Replay.OperationId);
        Assert.AreEqual(replayed.Replay.StateId, operation.StateId);
        Assert.AreEqual(replayed.Replay.SliceId, operation.IntendedSliceId);
        if (failure)
        {
            Assert.AreEqual("replay", replayed.Frontier!.Kind);
            Assert.AreEqual("category-slice", replayed.Frontier.SliceId);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Attribution_CategoryOperationIntentSurvivesProgressChangeAndLedgerRecovery(bool changeDuringOperation)
    {
        using var fixture = await AttributionFixtureAsync();
        var before = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("category-slice", before.ActiveSlice);
        Assert.AreEqual("category-selection", before.Workflow!.ActiveStateId);
        Action? mutate = changeDuringOperation ? () => SetStaleActiveTupleAsync(fixture).GetAwaiter().GetResult() : null;
        var runner = new UwpEnforcementRunner(new AttributionDiagnosticRunner(mutate),
            windowOwnerResolver: TestWindowOwnerResolver.Shared);

        var diagnostic = await runner.DiagnoseAsync(fixture.Root, new UwpEnforcementOptions());

        Assert.AreEqual("category-selection", diagnostic.StateId);
        Assert.AreEqual("category-slice", diagnostic.SliceId);
        Assert.IsNotNull(diagnostic.OperationId);
        Assert.IsTrue(StateClosureSeal.Verify(diagnostic));
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        var operation = progress.Operations.Single(item => item.Id == diagnostic.OperationId);
        Assert.AreEqual("category-selection", operation.StateId);
        Assert.AreEqual("category-slice", operation.IntendedSliceId);
        File.Delete(fixture.Path(EnforcementStore.AttemptLedgerPath));
        await SetStaleActiveTupleAsync(fixture);

        var recovered = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        var failure = (await fixture.LoadAsync<AttemptLedger>(EnforcementStore.AttemptLedgerPath)).Attempts.Single();
        Assert.AreEqual("category-selection", failure.StateId);
        Assert.AreEqual("category-slice", failure.SliceId);
        Assert.AreEqual(diagnostic.OperationId, failure.OperationId);
        Assert.AreEqual("category-selection", recovered.Frontier!.Id);
        Assert.AreEqual("category-slice", recovered.Frontier.SliceId);
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        Assert.IsTrue(StateClosureProtocol.HasHistoricalReachability(fixture.Root, plan.States[0]));
    }

    [TestMethod]
    public async Task Attribution_DeclaredStateDoesNotDependOnAdvisoryActiveTuple()
    {
        using var fixture = await AttributionFixtureAsync();
        await SetStaleActiveTupleAsync(fixture);
        var process = new FakeProcessRunner(new EnforcementProcessResult(0, ClosureTree, ""));
        var runner = new UwpEnforcementRunner(process, windowOwnerResolver: TestWindowOwnerResolver.Shared);

        await runner.InvokeUiAsync(fixture.Root,
            "winapp", ["inspect", "-w", "42", "--json"], [], "initial-state");
        Assert.AreEqual(1, process.CallCount);
    }

    private static async Task<RunnerFixture> AttributionFixtureAsync()
    {
        var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        try
        {
            var plan = await NewRequiredPlanAsync(fixture);
            var first = plan.States[0];
            first.Id = "initial-state"; first.Slice = "initial-slice";
            var next = JsonSerializer.Deserialize<MigrationState>(JsonSerializer.Serialize(first, EnforcementStore.JsonOptions),
                EnforcementStore.JsonOptions)!;
            next.Id = "category-selection"; next.Slice = "category-slice"; next.Dependencies = ["initial-slice"];
            next.SourceDefinitions = ["CategoryPage.xaml"];
            await File.WriteAllTextAsync(
                fixture.Path("CategoryPage.xaml"),
                """
                <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />
                """);
            const string categoryTree =
                ".migration-evidence/states/state-1/category-ui.json";
            plan.States.Add(next);
            await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
            ContractTestAuthority.Prepare(fixture.Root, plan);
            await ObserveSourceAsync(fixture, plan);
            await File.WriteAllTextAsync(
                fixture.Path(categoryTree),
                ClosureTree);
            next.Source.Status = "captured";
            next.Source.TreeEvidence = categoryTree;
            next.Source.Evidence = [categoryTree];
            await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
            await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
            var probe = await new UwpEnforcementRunner(new FakeProcessRunner(new EnforcementProcessResult(0, ClosureTree, "")),
                windowOwnerResolver: TestWindowOwnerResolver.Shared).InvokeUiAsync(fixture.Root,
                    "winapp", ["inspect", "-w", "42", "--json"], [], "initial-state");
            await UwpEnforcementRunner.RecordSurfaceAsync(fixture.Root, TestWindowOwnerResolver.Shared, "initial-state", [probe.ArtifactPath]);
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    private static async Task SetStaleActiveTupleAsync(RunnerFixture fixture)
    {
        var progress = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(fixture.Root.FullName, EnforcementStore.ProgressPath)))!;
        progress["activeSliceId"] = "initial-slice";
        progress["activeStateId"] = "initial-state";
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, progress);
    }

    private sealed class AttributionDiagnosticRunner(Action? during) : IEnforcementProcessRunner
    {
        public Task<EnforcementProcessResult> RunAsync(string executable, DirectoryInfo workingDirectory,
            IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            during?.Invoke();
            return Task.FromResult(new EnforcementProcessResult(1, "", "runtime-selection-error"));
        }
    }
}
