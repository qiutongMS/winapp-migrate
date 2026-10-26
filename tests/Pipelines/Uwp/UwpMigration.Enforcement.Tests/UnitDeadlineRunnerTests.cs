// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task UnitDeadline_RequiredStageCancelsAndRejectsLateSuccess(bool semantic, bool honorsCancellation)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var clock = new ManualTimeProvider(DeadlineNow);
        await PrepareUnitDeadlineAsync(fixture, clock, semantic);
        var process = new DelegateProcessRunner(token =>
        {
            clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
            Assert.IsFalse(token.IsCancellationRequested);
            clock.Advance(TimeSpan.FromTicks(1));
            Assert.IsTrue(token.IsCancellationRequested);
            if (honorsCancellation) token.ThrowIfCancellationRequested();
            return Task.FromResult(BuildSuccess());
        });
        var runner = new UwpEnforcementRunner(process, clock);
        Func<Task> invoke = semantic
            ? () => runner.InvokeUiAsync(fixture.Root, "winapp", ["list"])
            : () => runner.RunAsync(fixture.Root, new UwpEnforcementOptions());

        await Assert.ThrowsExactlyAsync<InvalidDataException>(invoke);

        var snapshot = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        AssertUnitExpiry(snapshot, false, DeadlineNow.AddMinutes(5), 5);
        Assert.AreEqual(semantic ? WorkflowStage.SemanticConvergence : WorkflowStage.MechanicalStabilization, snapshot.Stage);
        Assert.AreEqual(1, process.CallCount);
        if (!semantic) Assert.IsFalse(File.Exists(fixture.Path(EnforcementStore.BuildAttestationPath)));
        Assert.IsNull(WorkflowValidation.ProgressError(snapshot.Workflow,
            await fixture.LoadAsync<EnforcementPolicy>(EnforcementStore.PolicyPath), clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromMinutes(10));
        var observed = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreEqual("blocked-required", observed.Disposition?.Kind);
        Assert.AreEqual(DeadlineNow.AddMinutes(5), observed.Workflow!.LastObservedAt);
        Assert.AreEqual(1, process.CallCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnitDeadline_OptionalSemanticExpiryDefersWithoutTerminalizing(bool honorsCancellation)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var clock = new ManualTimeProvider(DeadlineNow);
        await PrepareUnitDeadlineAsync(fixture, clock, true, true);
        var process = new DelegateProcessRunner(token =>
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            Assert.IsTrue(token.IsCancellationRequested);
            if (honorsCancellation) token.ThrowIfCancellationRequested();
            return Task.FromResult(new EnforcementProcessResult(0, "UI completed too late.", ""));
        });
        var runner = new UwpEnforcementRunner(process, clock);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(fixture.Root, "winapp", ["list"]));

        var snapshot = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        AssertUnitExpiry(snapshot, true, DeadlineNow.AddMinutes(5), 5);
        Assert.AreEqual("state-1", snapshot.Workflow!.Debt.Single().SliceId);
        Assert.IsFalse(snapshot.Workflow.Slices.Single(slice => slice.SliceId == "state-1").Resolved);
        Assert.AreEqual(1, snapshot.Budget!.Operations.UiActions);
        Assert.IsNull(WorkflowValidation.ProgressError(snapshot.Workflow,
            await fixture.LoadAsync<EnforcementPolicy>(EnforcementStore.PolicyPath), clock.GetUtcNow()));
        var independent = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        Assert.AreEqual("independent", independent.ActiveSlice);
        Assert.AreEqual("continue", independent.Disposition?.Kind);
        Assert.IsFalse(independent.Disposition!.Terminal);
        Assert.AreEqual(5, independent.Workflow!.Units.Single(unit => unit.SliceId == "state-1")
            .ActiveMinutes(clock.GetUtcNow()));
        Assert.AreEqual(1, process.CallCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnitDeadline_SimultaneousGlobalExpiryWinsIncludingOptionalAndCallerCancellation(bool optional)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var clock = new ManualTimeProvider(DeadlineNow);
        await PrepareUnitDeadlineAsync(fixture, clock, true, optional);
        await SeedDeadlineAsync(fixture, 145);
        using var callerCancellation = new CancellationTokenSource();
        var process = new DelegateProcessRunner(token =>
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            callerCancellation.Cancel();
            Assert.IsTrue(token.IsCancellationRequested);
            return Task.FromResult(new EnforcementProcessResult(0, "UI completed too late.", ""));
        });

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new UwpEnforcementRunner(process, clock).InvokeUiAsync(
                fixture.Root, "winapp", ["list"], callerCancellation.Token));

        var snapshot = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        AssertDeadline(snapshot, DeadlineNow.AddMinutes(5), 5);
        Assert.IsEmpty(snapshot.Workflow!.Debt);
        Assert.AreEqual(1, process.CallCount);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task UnitDeadline_AlreadyExpiredAtInvocationDoesNotStartProcess(bool semantic, bool optional)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var clock = new ManualTimeProvider(DeadlineNow);
        await PrepareUnitDeadlineAsync(fixture, clock, semantic, optional);
        clock.OnTimerCreated = () => clock.Advance(TimeSpan.FromMinutes(5));
        var process = new DelegateProcessRunner(_ => Task.FromResult(BuildSuccess()));
        var runner = new UwpEnforcementRunner(process, clock);
        Func<Task> invoke = semantic
            ? () => runner.InvokeUiAsync(fixture.Root, "winapp", ["list"])
            : () => runner.RunAsync(fixture.Root, new UwpEnforcementOptions());

        await Assert.ThrowsExactlyAsync<InvalidDataException>(invoke);

        Assert.AreEqual(0, process.CallCount);
        AssertUnitExpiry(await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath),
            optional, DeadlineNow.AddMinutes(5), 5);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task UnitDeadline_ReentryChargesPriorActiveDurationButNotInactiveTime(int extraActiveTicks)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var clock = new ManualTimeProvider(DeadlineNow);
        await PrepareUnitDeadlineAsync(fixture, clock, true);
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        var unit = progress.Units.Single(account => account.SliceId == "state-1");
        var priorActive = TimeSpan.FromMinutes(2) + TimeSpan.FromTicks(extraActiveTicks);
        var remaining = TimeSpan.FromMinutes(5) - priorActive;
        var expiredAt = DeadlineNow.AddMinutes(22) + remaining;
        unit.Epochs[0] = unit.Epochs[0] with
        {
            ExitedAt = DeadlineNow + priorActive, ExitReason = "account-switched"
        };
        unit.Epochs.Add(new WorkflowActivationEpoch
        {
            EnteredAt = DeadlineNow.AddMinutes(22), TargetFingerprint = progress.LastTargetFingerprint
        });
        progress.LastObservedAt = DeadlineNow.AddMinutes(22);
        clock.Advance(TimeSpan.FromMinutes(22));
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, progress);
        var process = new DelegateProcessRunner(token =>
        {
            clock.Advance(remaining - TimeSpan.FromTicks(1));
            Assert.IsFalse(token.IsCancellationRequested);
            clock.Advance(TimeSpan.FromTicks(1));
            Assert.IsTrue(token.IsCancellationRequested);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new EnforcementProcessResult(0, "", ""));
        });

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new UwpEnforcementRunner(process, clock).InvokeUiAsync(fixture.Root, "winapp", ["list"]));

        var snapshot = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        AssertUnitExpiry(snapshot, false, expiredAt, (expiredAt - DeadlineNow).TotalMinutes);
        Assert.HasCount(2, snapshot.Workflow!.Units.Single(account => account.SliceId == "state-1").Epochs);
        Assert.AreEqual(1, process.CallCount);
    }

    private static async Task PrepareUnitDeadlineAsync(
        RunnerFixture fixture, ManualTimeProvider clock, bool semantic, bool optional = false)
    {
        var policy = EnforcementPolicy.Balanced();
        policy.MechanicalStabilization.SoftMinutes = policy.MechanicalStabilization.HardMinutes = 5;
        policy.SemanticConvergence.SoftMinutes = policy.SemanticConvergence.HardMinutes = 5;
        await UwpEnforcementRunner.ConfigurePolicyAsync(fixture.Root, policy);
        if (optional)
        {
            var plan = await fixture.LoadAsync<StatePlanDocument>(".migration-evidence/state-plan.json");
            plan.States[0].Criticality = StateCriticality.Optional;
            plan.States.Add(new MigrationState
            {
                Id = "independent", Actions = ["Observe"], ExpectedOutcome = "Independent behavior is preserved.",
                Source = new StateObservation { Status = "captured", Evidence = [".migration-evidence/source.txt"] },
                Target = new StateObservation { Status = "unverified" },
                Comparison = new StateComparison { Status = "unverified" }
            });
            await EnforcementStore.WriteAtomicAsync(fixture.Root, ".migration-evidence/state-plan.json", plan);
        }
        if (semantic)
            await new UwpEnforcementRunner(SuccessfulLaunch(), clock).RunAsync(fixture.Root, new UwpEnforcementOptions());
    }

    private static void AssertUnitExpiry(
        EnforcementSnapshot snapshot, bool optional, DateTimeOffset observedAt, double totalMinutes)
    {
        Assert.AreEqual(optional ? "continue-with-debt" : "blocked-required", snapshot.Disposition?.Kind);
        Assert.AreEqual(optional ? "optional-debt" : "unit-hard-limit", snapshot.Disposition?.ReasonCode);
        Assert.AreEqual(!optional, snapshot.Disposition!.Terminal);
        Assert.AreEqual(optional, snapshot.Disposition.Retryable);
        Assert.AreEqual(optional ? "debt" : "blocked", snapshot.Disposition.Finalization);
        Assert.IsEmpty(snapshot.Disposition.AllowedOperations);
        Assert.AreEqual(optional ? "continue-with-debt" : "blocked", snapshot.Status);
        Assert.AreEqual(observedAt, snapshot.GeneratedAt);
        Assert.AreEqual(observedAt, snapshot.Workflow!.LastObservedAt);
        Assert.AreEqual(snapshot.Disposition.Kind, snapshot.Budget?.Decision);
        Assert.AreEqual("unit-hard-limit", snapshot.Budget!.ReasonCode);
        Assert.AreEqual(5, snapshot.Budget.ElapsedMinutes);
        Assert.AreEqual(totalMinutes, snapshot.Budget.TotalElapsedMinutes);
        Assert.AreEqual("blocked", snapshot.Gates.Single(gate => gate.Id == "stage-budget").Status);
        var account = snapshot.Workflow.Units.Single(unit =>
            unit.Stage == snapshot.Budget.Stage && unit.SliceId == snapshot.Budget.SliceId);
        Assert.AreEqual(observedAt, account.Epochs.Last().ExitedAt);
        Assert.AreEqual(optional ? "optional-deferred" : "terminal:blocked-required", account.Epochs.Last().ExitReason);
        Assert.AreEqual(optional ? (DateTimeOffset?)null : observedAt, account.BlockedAt);
    }
}
