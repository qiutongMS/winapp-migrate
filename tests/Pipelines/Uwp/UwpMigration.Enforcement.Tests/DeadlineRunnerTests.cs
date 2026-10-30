// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    private static readonly DateTimeOffset DeadlineNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(0)]
    [DataRow(145)]
    [DataRow(151)]
    public async Task ElapsedTime_DoesNotCancelProcessOrBlockWorkflow(int previousMinutes)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var clock = new ManualTimeProvider(DeadlineNow);
        await SeedElapsedTimeAsync(fixture, previousMinutes);
        var invocation = 0;
        var process = new DelegateProcessRunner(token =>
        {
            Assert.IsFalse(token.IsCancellationRequested);
            clock.Advance(TimeSpan.FromMinutes(200));
            invocation++;
            return Task.FromResult(invocation == 1 ? BuildSuccess() : ReplaySuccess());
        });

        var snapshot = await new UwpEnforcementRunner(process, clock).RunAsync(
            fixture.Root, new UwpEnforcementOptions());

        Assert.AreEqual(2, process.CallCount);
        Assert.AreEqual("within-budget", snapshot.Budget?.Decision);
        Assert.AreEqual("continue", snapshot.Disposition?.Kind);
        Assert.IsFalse(snapshot.Disposition!.Terminal);
        Assert.IsNull(snapshot.Budget!.SoftDeadline);
        Assert.IsNull(snapshot.Budget.HardDeadline);
        Assert.IsNull(snapshot.Budget.TotalHardDeadline);
        Assert.IsGreaterThanOrEqualTo(previousMinutes + 400, snapshot.Budget.TotalElapsedMinutes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InfrastructureFailurePersistsActualElapsedTime(bool returnsFailure)
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

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            runner.RunAsync(fixture.Root, new UwpEnforcementOptions()));

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
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CallerCancellationStillCancelsProcess(bool honorsCancellation)
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
        Assert.IsFalse(File.Exists(fixture.Path(EnforcementStore.BuildAttestationPath)));
        Assert.AreEqual(1, snapshot.Workflow!.Units.Single().Operations.Builds);
        Assert.IsNull(snapshot.Workflow.Units.Single().Epochs.Single().ExitedAt);
        Assert.AreEqual(1, process.CallCount);
    }

    [TestMethod]
    [DataRow("total-budget-exhausted", "total-hard-limit")]
    [DataRow("blocked-required", "unit-hard-limit")]
    [DataRow("blocked-required", "operation-ceiling")]
    public async Task LegacySchema20AuthorizationTerminalResumesAndInvokesWinApp(
        string kind, string reasonCode)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await SeedElapsedTimeAsync(fixture, 151);
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        progress.SchemaVersion = "2.0";
        var disposition = new RunDisposition
        {
            Kind = kind,
            ReasonCode = reasonCode,
            Terminal = true,
            Retryable = false,
            Finalization = "blocked"
        };
        progress.Disposition = disposition;
        var unit = progress.Units.Single();
        unit.BlockedAt = DeadlineNow;
        unit.BlockedReason = "Retired time authorization.";
        WorkflowController.CloseActiveEpochs(progress, DeadlineNow, $"terminal:{kind}");
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, progress);
        var state = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        state.GeneratedAt = DeadlineNow;
        state.Status = "blocked";
        state.Workflow = progress;
        state.Disposition = disposition;
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.StatePath, state);
        var process = SuccessfulLaunch();

        var resumed = await new UwpEnforcementRunner(process, new ManualTimeProvider(DeadlineNow))
            .RunAsync(fixture.Root, new UwpEnforcementOptions());

        Assert.AreEqual(2, process.CallCount);
        Assert.AreEqual("continue", resumed.Disposition?.Kind);
        Assert.IsFalse(resumed.Disposition!.Terminal);
        Assert.AreEqual("3.0", resumed.Workflow?.SchemaVersion);
        Assert.AreEqual("within-budget", resumed.Budget?.Decision);
        Assert.IsTrue(resumed.Workflow!.Units.All(account => account.BlockedAt is null));
    }

    private static async Task SeedElapsedTimeAsync(RunnerFixture fixture, int previousMinutes)
    {
        if (!File.Exists(fixture.Path(EnforcementStore.ProgressPath)))
            await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
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

    private sealed class DelegateProcessRunner(Func<CancellationToken, Task<EnforcementProcessResult>> invoke)
        : IEnforcementProcessRunner, IWindowOwnerResolver
    {
        public int CallCount { get; private set; }

        public bool TryGetProcessId(long windowHandle, out int processId)
        {
            processId = 4242;
            return windowHandle > 0;
        }

        public bool TryGetProcessIdentity(int processId, out ProcessIdentity identity)
        {
            identity = new ProcessIdentity(processId, TestWindowOwnerResolver.ProcessStartedAt);
            return processId > 0;
        }

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

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            var onCreated = OnTimerCreated;
            OnTimerCreated = null;
            onCreated?.Invoke();
            return timer;
        }

        private sealed class ManualTimer(
            ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
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
