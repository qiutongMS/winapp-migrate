// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    private static readonly DateTimeOffset DeadlineNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(WorkflowOperation.Build)]
    [DataRow(WorkflowOperation.Replay)]
    [DataRow(WorkflowOperation.DiagnosticRun)]
    [DataRow(WorkflowOperation.UiAction)]
    public async Task Deadline_ExpiryAfterAssessmentPreventsEveryProcessInvocation(WorkflowOperation operation)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var clock = new ManualTimeProvider(DeadlineNow);
        if (operation != WorkflowOperation.Build)
            await new UwpEnforcementRunner(SuccessfulLaunch(), clock).RunAsync(fixture.Root, new UwpEnforcementOptions());
        await SeedDeadlineAsync(fixture, 145);
        clock.OnTimerCreated = () => clock.Advance(TimeSpan.FromMinutes(5));
        var process = new DelegateProcessRunner(_ => Task.FromResult(BuildSuccess()));
        var runner = new UwpEnforcementRunner(process, clock);
        const string evidence = ".migration-evidence/deadline.txt";
        await File.WriteAllTextAsync(fixture.Path(evidence), "A discriminating observation before deadline.");
        Func<Task> invoke = operation switch
        {
            WorkflowOperation.Build or WorkflowOperation.Replay => () => runner.RunAsync(fixture.Root,
                new UwpEnforcementOptions { ForceReplay = true, DiscriminatingEvidence = [evidence] }),
            WorkflowOperation.DiagnosticRun => () => runner.DiagnoseAsync(fixture.Root,
                new UwpEnforcementOptions { DiscriminatingEvidence = [evidence] }),
            _ => () => runner.InvokeUiAsync(fixture.Root, "winapp", ["list"], [evidence])
        };

        await Assert.ThrowsExactlyAsync<InvalidDataException>(invoke);

        Assert.AreEqual(0, process.CallCount);
        var snapshot = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        AssertDeadline(snapshot, DeadlineNow.AddMinutes(5), 5);
        Assert.AreEqual(operation, snapshot.Workflow!.Operations.Last().Kind);
    }

    [TestMethod]
    [DataRow(0, true)]
    [DataRow(145, true)]
    [DataRow(145, false)]
    public async Task Deadline_RemainingBudgetCancelsProcessAndDurablyTerminalizes(
        int previousMinutes, bool honorsCancellation)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var clock = new ManualTimeProvider(DeadlineNow);
        await SeedDeadlineAsync(fixture, previousMinutes);
        var remaining = TimeSpan.FromMinutes(150 - previousMinutes);
        using var callerCancellation = new CancellationTokenSource();
        var process = new DelegateProcessRunner(token =>
        {
            Assert.IsTrue(token.CanBeCanceled);
            Assert.AreNotEqual(callerCancellation.Token, token);
            clock.Advance(remaining - TimeSpan.FromSeconds(1));
            Assert.IsFalse(token.IsCancellationRequested);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.IsTrue(token.IsCancellationRequested);
            Assert.IsFalse(callerCancellation.IsCancellationRequested);
            if (honorsCancellation) token.ThrowIfCancellationRequested();
            return Task.FromResult(BuildSuccess());
        });
        var runner = new UwpEnforcementRunner(process, clock);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.RunAsync(fixture.Root, new UwpEnforcementOptions(), callerCancellation.Token));

        var snapshot = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        AssertDeadline(snapshot, DeadlineNow + remaining, remaining.TotalMinutes);
        Assert.AreEqual(1, snapshot.Workflow!.Units.Single().Operations.Builds);
        Assert.IsFalse(File.Exists(fixture.Path(EnforcementStore.BuildAttestationPath)));
        Assert.IsNull(WorkflowValidation.ProgressError(snapshot.Workflow,
            await fixture.LoadAsync<EnforcementPolicy>(EnforcementStore.PolicyPath), clock.GetUtcNow()));
        var observed = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreEqual("total-budget-exhausted", observed.Disposition?.Kind);
        Assert.AreEqual(1, process.CallCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Deadline_InfrastructureFailurePersistsActualElapsedTime(bool returnsFailure)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var clock = new ManualTimeProvider(DeadlineNow);
        var process = new DelegateProcessRunner(_ =>
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            if (!returnsFailure) throw new IOException("Process transport failed.");
            return Task.FromResult(new EnforcementProcessResult(-1, "", "Process transport failed.")
            {
                InfrastructureFailure = true
            });
        });
        var runner = new UwpEnforcementRunner(process, clock);

        await Assert.ThrowsExactlyAsync<IOException>(() => runner.RunAsync(fixture.Root, new UwpEnforcementOptions()));

        var snapshot = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        var failedAt = DeadlineNow.AddMinutes(5);
        Assert.AreEqual("infrastructure-failure", snapshot.Disposition?.Kind);
        Assert.AreEqual(failedAt, snapshot.GeneratedAt);
        Assert.AreEqual(failedAt, snapshot.Workflow!.LastObservedAt);
        Assert.AreEqual(failedAt, snapshot.Workflow.Units.Single().Epochs.Single().ExitedAt);
        Assert.AreEqual("terminal:infrastructure-failure", snapshot.Workflow.Units.Single().Epochs.Single().ExitReason);
        Assert.AreEqual(5, snapshot.Budget!.ElapsedMinutes);
        Assert.AreEqual(5, snapshot.Budget.TotalElapsedMinutes);
        Assert.AreEqual(1, snapshot.Budget.Operations.Builds);
        Assert.AreEqual(1, snapshot.Budget.TotalOperations.Builds);
        Assert.IsNull(snapshot.Budget.HardDeadline);
        Assert.IsNull(WorkflowValidation.ProgressError(snapshot.Workflow,
            await fixture.LoadAsync<EnforcementPolicy>(EnforcementStore.PolicyPath), failedAt));
        clock.Advance(TimeSpan.FromMinutes(10));
        var observed = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        Assert.AreEqual("infrastructure-failure", observed.Disposition?.Kind);
        Assert.AreEqual(failedAt, observed.Workflow!.LastObservedAt);
        Assert.AreEqual(5, observed.Workflow.Units.Single().ActiveMinutes(clock.GetUtcNow()));
        Assert.AreEqual(1, process.CallCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Deadline_CallerCancellationBeforeDeadlineIsNotGlobalTimeout(bool honorsCancellation)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var clock = new ManualTimeProvider(DeadlineNow);
        using var callerCancellation = new CancellationTokenSource();
        var process = new DelegateProcessRunner(token =>
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            callerCancellation.Cancel();
            if (honorsCancellation) token.ThrowIfCancellationRequested();
            return Task.FromResult(BuildSuccess());
        });
        var runner = new UwpEnforcementRunner(process, clock);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runner.RunAsync(fixture.Root, new UwpEnforcementOptions(), callerCancellation.Token));

        var snapshot = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        Assert.IsFalse(snapshot.Disposition!.Terminal);
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        Assert.IsFalse(progress.Disposition!.Terminal);
        Assert.IsFalse(File.Exists(fixture.Path(EnforcementStore.BuildAttestationPath)));
        Assert.IsFalse(File.Exists(fixture.Path(EnforcementStore.ReplayAttestationPath)));
        Assert.AreEqual(1, snapshot.Workflow!.Units.Single().Operations.Builds);
        Assert.IsNull(snapshot.Workflow.Units.Single().Epochs.Single().ExitedAt);
        Assert.AreEqual(1, process.CallCount);
    }

    [TestMethod]
    public async Task Deadline_ExpiryTakesPrecedenceOverConcurrentInfrastructureFailure()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var clock = new ManualTimeProvider(DeadlineNow);
        await SeedDeadlineAsync(fixture, 145);
        var process = new DelegateProcessRunner(_ =>
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            throw new IOException("Process transport failed at the deadline.");
        });

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new UwpEnforcementRunner(process, clock).RunAsync(fixture.Root, new UwpEnforcementOptions()));

        AssertDeadline(await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath),
            DeadlineNow.AddMinutes(5), 5);
        Assert.AreEqual(1, process.CallCount);
    }

    private static async Task SeedDeadlineAsync(RunnerFixture fixture, int previousMinutes)
    {
        if (!File.Exists(fixture.Path(EnforcementStore.ProgressPath)))
            await UwpEnforcementRunner.AssessAsync(fixture.Root);
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        progress.StartedAt = DeadlineNow.AddMinutes(-previousMinutes);
        progress.LastObservedAt = DeadlineNow;
        foreach (var unit in progress.Units)
        {
            unit.StartedAt = DeadlineNow;
            unit.Epochs = unit.Epochs.Select(epoch => epoch with
            {
                EnteredAt = DeadlineNow,
                ExitedAt = epoch.ExitedAt is null ? null : DeadlineNow
            }).ToList();
        }
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, progress);
    }

    private static void AssertDeadline(EnforcementSnapshot snapshot, DateTimeOffset observedAt, double activeMinutes)
    {
        Assert.AreEqual("total-budget-exhausted", snapshot.Disposition?.Kind);
        Assert.AreEqual("total-hard-limit", snapshot.Disposition?.ReasonCode);
        Assert.IsTrue(snapshot.Disposition!.Terminal);
        Assert.IsFalse(snapshot.Disposition.Retryable);
        Assert.IsEmpty(snapshot.Disposition.AllowedOperations);
        Assert.AreEqual("blocked", snapshot.Status);
        Assert.AreEqual(observedAt, snapshot.GeneratedAt);
        Assert.AreEqual(observedAt, snapshot.Workflow!.LastObservedAt);
        Assert.IsTrue(snapshot.Workflow.Units.SelectMany(unit => unit.Epochs).All(epoch => epoch.ExitedAt is not null));
        Assert.AreEqual("total-budget-exhausted", snapshot.Budget?.Decision);
        Assert.AreEqual("blocked", snapshot.Gates.Single(gate => gate.Id == "stage-budget").Status);
        Assert.AreEqual(activeMinutes, snapshot.Budget!.ElapsedMinutes);
        Assert.AreEqual(150, snapshot.Budget.TotalElapsedMinutes);
    }

    private sealed class DelegateProcessRunner(Func<CancellationToken, Task<EnforcementProcessResult>> invoke)
        : IEnforcementProcessRunner
    {
        public int CallCount { get; private set; }

        public Task<EnforcementProcessResult> RunAsync(
            string executable, DirectoryInfo workingDirectory, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return invoke(cancellationToken);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _now = initialTime;
        private readonly List<ManualTimer> _timers = [];
        public Action? OnTimerCreated { get; set; }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            foreach (var timer in _timers.ToArray())
                if (timer.DueAt <= _now) timer.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            var onCreated = OnTimerCreated;
            OnTimerCreated = null;
            onCreated?.Invoke();
            return timer;
        }

        private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan _period;
            private bool _disposed;
            public DateTimeOffset? DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _period = period;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                return true;
            }

            public void Fire()
            {
                if (_disposed) return;
                DueAt = _period == Timeout.InfiniteTimeSpan ? null : clock._now + _period;
                callback(state);
            }

            public void Dispose()
            {
                _disposed = true;
                clock._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
