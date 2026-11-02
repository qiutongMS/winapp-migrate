// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class EnforcementEvaluatorTests
{
    [TestMethod]
    [DataRow("none", null, null)]
    [DataRow("verified-prior", null, null)]
    [DataRow("unresolved-current", "category-selection", "category-slice")]
    public void Attribution_IdlessRecoveryNeverInfersTheFirstPlannedState(string fallback, string? state, string? slice)
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        var next = State("category-selection");
        next.Slice = "category-slice";
        input.StatePlan!.States.Add(next);
        var progress = fallback == "none" ? null : new WorkflowProgress
        {
            PolicyFingerprint = "fixture", LastTargetFingerprint = input.TargetFingerprint,
            ActiveStateId = fallback == "verified-prior" ? "state-1" : "category-selection",
            ActiveSliceId = fallback == "verified-prior" ? "state-1" : "category-slice"
        };
        var failed = new ReplayAttestation
        {
            Id = "legacy-failure", RunId = "legacy-run", BuildAttestationId = "build-1",
            TargetFingerprint = input.TargetFingerprint, Success = false, CompletedAt = input.GeneratedAt,
            StandardOutputPath = "replay-stdout.json", StandardErrorPath = "replay-stderr.log"
        };
        var attempts = new AttemptLedger();

        Assert.IsTrue(RuntimeFailureRecovery.Recover(attempts, failed, null, input with { Progress = progress }));

        Assert.AreEqual(state, attempts.Attempts.Single().StateId);
        Assert.AreEqual(slice, attempts.Attempts.Single().SliceId);
        var evaluated = EnforcementEvaluator.Evaluate(input with
        {
            Attempts = attempts, Progress = null, ReportClosureReady = false, ReportClosureReason = "Pending category work."
        });
        Assert.IsTrue(evaluated.Workflow!.Slices.Single(item => item.SliceId == "state-1").Resolved,
            "Unscoped later failures cannot reopen an unrelated parity-verified state.");
        if (state is null)
        {
            Assert.IsNull(evaluated.Frontier!.Id);
            Assert.IsNull(evaluated.Frontier.SliceId);
            Assert.IsEmpty(evaluated.Frontier.StateIds);
        }
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
    public void Attribution_AuthorizationSkipsDeferredOptionalWork()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        var optional = State("optional-state");
        optional.Criticality = StateCriticality.Optional;
        input.StatePlan!.States.Add(optional);
        input.StatePlan.States.Add(State("required-state"));
        var progress = EnforcementEvaluator.Evaluate(input).Workflow!;
        progress.Debt.Add(new WorkflowDebt { SliceId = optional.Id, Reason = "Explicitly deferred optional work." });
        input = input with { Progress = progress };

        var snapshot = EnforcementEvaluator.Evaluate(input);
        var binding = StateAttribution.Authorize(input, "required-state");

        Assert.AreEqual(snapshot.ActiveSlice, binding.SliceId);
        Assert.AreEqual("required-state", binding.StateId);
        Assert.ThrowsExactly<InvalidDataException>(() => StateAttribution.Authorize(input, optional.Id));
    }

    [TestMethod]
    [DataRow("finding")]
    [DataRow("failure")]
    public void Attribution_AuthorizationIncludesMappedReopenedStates(string cause)
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input.StatePlan!.States.Add(State("later-state"));
        if (cause == "finding")
            input = input with { FindingLedger = new SemanticFindingLedger
            {
                Findings = [new SemanticFinding
                {
                    Id = "regression", Category = "behavior", Status = "open", StateIds = ["state-1"],
                    SourceInvariant = "Preserve the established observation.", CompletionCondition = "Reprove its contract."
                }]
            } };
        else
            input.Attempts.Attempts.Add(new EnforcementAttempt
            {
                Kind = "runtime", Signature = "mapped-regression", TargetFingerprint = input.TargetFingerprint,
                StateId = "state-1", SliceId = "state-1", RecordedAt = input.GeneratedAt, Evidence = ["evidence.txt"]
            });

        var snapshot = EnforcementEvaluator.Evaluate(input);
        var binding = StateAttribution.Authorize(input, "state-1");

        Assert.AreEqual(snapshot.ActiveSlice, binding.SliceId);
        Assert.AreEqual("state-1", binding.StateId);
        Assert.ThrowsExactly<InvalidDataException>(() => StateAttribution.Authorize(input, "later-state"));
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
            PolicyFingerprint = "fixture", LastTargetFingerprint = input.TargetFingerprint,
            ActiveStateId = state.Id, ActiveSliceId = state.Id
        } };

        if (malformed == "null-target")
            Assert.IsFalse(StateClosureProtocol.HasHistoricalParity(input.TargetRoot, state));
        Assert.ThrowsExactly<InvalidDataException>(() => StateAttribution.Authorize(input));
        Assert.ThrowsExactly<InvalidDataException>(() => StateAttribution.PersistedFallback(input));
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
        Assert.HasCount(2, FailureProtocol.ActiveRuntimeFailures(input).ToArray());
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
            .RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true, DiscriminatingEvidence = [evidence] });
        var attempt = (await fixture.LoadAsync<AttemptLedger>(EnforcementStore.AttemptLedgerPath)).Attempts.Single();
        const string diagnosis = ".migration-evidence/corrective-diagnosis.txt";
        await File.WriteAllTextAsync(fixture.Path(diagnosis), "A source/target comparison isolates the selection callback.");
        await UwpEnforcementRunner.RecordDiagnosisAsync(fixture.Root, TestWindowOwnerResolver.Shared, attempt.Signature,
            attempt.StateId, ["Source.cs:1"], ["Target.cs:1"], "Preserve selection", "The callback is missing",
            "Restore that callback and replay", [diagnosis]);
        await File.AppendAllTextAsync(fixture.ProjectPath, Environment.NewLine);
        var process = SuccessfulLaunch();

        var corrected = await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());

        Assert.AreEqual(2, process.CallCount);
        Assert.IsTrue(corrected.Replay!.Success);
        Assert.AreNotEqual(failed.Replay!.BuildAttestationId, corrected.Replay.BuildAttestationId);
        Assert.AreEqual("category-selection", corrected.Replay.StateId);
        Assert.AreEqual("passed", corrected.Gates.Single(gate => gate.Id == "enforcement-artifacts").Status);
        Assert.IsTrue(corrected.Workflow!.Slices.Single(slice => slice.SliceId == "initial-slice").Resolved);
        var preserved = (await fixture.LoadAsync<AttemptLedger>(EnforcementStore.AttemptLedgerPath)).Attempts.Single();
        Assert.AreEqual(attempt.OperationId, preserved.OperationId);
        Assert.AreEqual("category-selection", preserved.Diagnosis!.StateId);
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
        { ForceReplay = true, DiscriminatingEvidence = [evidence] });

        Assert.AreEqual("category-selection", replayed.Replay!.StateId);
        Assert.AreEqual("category-slice", replayed.Replay.SliceId);
        var history = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        var operation = history.Operations.Single(item => item.Id == replayed.Replay.OperationId);
        Assert.AreEqual(replayed.Replay.StateId, operation.StateId);
        Assert.AreEqual(replayed.Replay.SliceId, operation.IntendedSliceId);
        Assert.IsTrue(history.Slices.Single(item => item.SliceId == "initial-slice").Resolved);
        if (failure)
        {
            Assert.AreEqual("category-selection", replayed.Frontier!.Id);
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
        Assert.IsTrue(recovered.Workflow!.Slices.Single(item => item.SliceId == "initial-slice").Resolved);
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        Assert.IsTrue(StateClosureProtocol.HasHistoricalParity(fixture.Root, plan.States[0]));
        const string note = ".migration-evidence/category-diagnosis.txt";
        await File.WriteAllTextAsync(fixture.Path(note), "A source/target comparison of the current selection handler.");

        var diagnosed = await UwpEnforcementRunner.RecordDiagnosisAsync(fixture.Root, TestWindowOwnerResolver.Shared,
            failure.Signature, "category-selection", ["Source.xaml.cs:10"], ["Target.xaml.cs:10"],
            "Preserve the selected category.", "The current category callback fails.", "Replay only that category transition.", [note]);

        var persisted = (await fixture.LoadAsync<AttemptLedger>(EnforcementStore.AttemptLedgerPath)).Attempts.Single();
        Assert.AreEqual(persisted.StateId, persisted.Diagnosis!.StateId);
        Assert.AreEqual(persisted.SliceId, persisted.Diagnosis.SliceId);
        Assert.AreEqual("category-selection", diagnosed.Frontier!.Id);
        Assert.AreEqual("category-slice", diagnosed.Frontier.SliceId);
        Assert.IsTrue(diagnosed.Workflow!.Slices.Single(item => item.SliceId == "initial-slice").Resolved);
    }

    [TestMethod]
    public async Task Attribution_CallerStateCannotUseAStaleVerifiedActiveTuple()
    {
        using var fixture = await AttributionFixtureAsync();
        await SetStaleActiveTupleAsync(fixture);
        var process = new FakeProcessRunner(new EnforcementProcessResult(0, ClosureTree, ""));
        var runner = new UwpEnforcementRunner(process, windowOwnerResolver: TestWindowOwnerResolver.Shared);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => runner.InvokeUiAsync(fixture.Root,
            "winapp", ["inspect", "-w", "42", "--json"], [], "initial-state"));
        Assert.AreEqual(0, process.CallCount);
        const string evidence = ".migration-evidence/observed-category.txt";
        await File.WriteAllTextAsync(fixture.Path(evidence), "A later unresolved selection failure.");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UwpEnforcementRunner.RecordFailureAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "runtime", "later-selection", "initial-state", [evidence]));
    }

    [TestMethod]
    public async Task Attribution_ProbeEvidenceTupleCannotBeReboundByCallerState()
    {
        using var fixture = await AttributionFixtureAsync();
        var probe = await new UwpEnforcementRunner(new FakeProcessRunner(new EnforcementProcessResult(1, "", "selection failure")),
            windowOwnerResolver: TestWindowOwnerResolver.Shared).InvokeUiAsync(fixture.Root,
                "winapp", ["inspect", "-w", "42", "--json"], [], "category-selection");
        Assert.AreEqual("category-slice", probe.SliceId);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UwpEnforcementRunner.RecordFailureAsync(fixture.Root,
            TestWindowOwnerResolver.Shared, "runtime", "selection-failure", "initial-state", [probe.StandardErrorPath]));

        var recorded = await UwpEnforcementRunner.RecordFailureAsync(fixture.Root, TestWindowOwnerResolver.Shared,
            "runtime", "selection-failure", "category-selection", [probe.StandardErrorPath]);
        Assert.AreEqual("category-selection", recorded.Frontier!.Id);
        Assert.AreEqual("category-slice", recorded.Frontier.SliceId);
        var failure = (await fixture.LoadAsync<AttemptLedger>(EnforcementStore.AttemptLedgerPath)).Attempts.Single();
        Assert.AreEqual(probe.OperationId, failure.OperationId);
    }

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("inconsistent")]
    public async Task Attribution_InvalidBoundFailureCannotBeRepairedByDiagnosis(string malformed)
    {
        using var fixture = await AttributionFixtureAsync();
        const string evidence = ".migration-evidence/invalid-failure.txt";
        await File.WriteAllTextAsync(fixture.Path(evidence), "An invalid tuple is not a diagnosis input.");
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.AttemptLedgerPath, new AttemptLedger
        {
            Attempts = [new EnforcementAttempt
            {
                Kind = "runtime", Signature = "invalid", TargetFingerprint = await TargetFingerprint.ComputeAsync(fixture.Root),
                StateId = malformed == "unknown" ? "unknown-state" : "category-selection",
                SliceId = "initial-slice", RecordedAt = DateTimeOffset.UtcNow, Evidence = [evidence]
            }]
        });

        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("blocked", snapshot.Gates.Single(item => item.Id == "enforcement-artifacts").Status);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UwpEnforcementRunner.RecordDiagnosisAsync(fixture.Root,
            TestWindowOwnerResolver.Shared, "invalid", "category-selection", ["Source:1"], ["Target:1"],
            "Invariant", "Hypothesis", "Test", [evidence]));
        Assert.IsNull((await fixture.LoadAsync<AttemptLedger>(EnforcementStore.AttemptLedgerPath)).Attempts.Single().Diagnosis);
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
            next.ExpectedOutcome = "Current category selection remains observable.";
            next.Assertions[0].SourceEvidence = ".migration-evidence/states/state-1/category-ui.json";
            plan.States.Add(next);
            await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
            await UwpEnforcementRunner.SealContractAsync(fixture.Root);
            await ObserveSourceAsync(fixture, plan);
            await File.WriteAllTextAsync(fixture.Path(next.Assertions[0].SourceEvidence), ClosureTree);
            next.Source.Status = "captured"; next.Source.Evidence = [next.Assertions[0].SourceEvidence];
            await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
            await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
            var probe = await new UwpEnforcementRunner(new FakeProcessRunner(new EnforcementProcessResult(0, ClosureTree, "")),
                windowOwnerResolver: TestWindowOwnerResolver.Shared).InvokeUiAsync(fixture.Root,
                    "winapp", ["inspect", "-w", "42", "--json"], [], "initial-state");
            await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "initial-state", [probe.ArtifactPath]);
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    private static async Task SetStaleActiveTupleAsync(RunnerFixture fixture)
    {
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        var now = DateTimeOffset.UtcNow;
        WorkflowController.CloseActiveEpochs(progress, now, "fixture-active-change");
        progress.ActiveSliceId = "initial-slice"; progress.ActiveStateId = "initial-state";
        WorkflowController.Activate(progress, WorkflowController.CurrentUnit(progress, "initial-slice"), now, progress.LastTargetFingerprint);
        progress.LastObservedAt = now;
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
