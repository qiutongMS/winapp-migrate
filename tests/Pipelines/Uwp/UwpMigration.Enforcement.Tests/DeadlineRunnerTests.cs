// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    public async Task ElapsedTime_DoesNotCancelLateBuildOrReplay()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var clock = new ManualTimeProvider(DeadlineNow);
        var invocation = 0;
        var process = new DelegateProcessRunner(token =>
        {
            Assert.IsFalse(token.IsCancellationRequested);
            clock.Advance(TimeSpan.FromMinutes(6));
            invocation++;
            return Task.FromResult(invocation == 1 ? BuildSuccess() : ReplaySuccess());
        });

        var snapshot = await new UwpEnforcementRunner(process, clock).RunAsync(
            fixture.Root, new UwpEnforcementOptions());

        Assert.AreEqual(2, process.CallCount);
        Assert.IsTrue(File.Exists(fixture.Path(EnforcementStore.BuildAttestationPath)));
        Assert.IsTrue(File.Exists(fixture.Path(EnforcementStore.ReplayAttestationPath)));
        Assert.AreEqual(12, snapshot.Telemetry!.ElapsedMinutes);
        Assert.IsFalse(snapshot.Disposition!.Terminal);
    }

    private static readonly DateTimeOffset DeadlineNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

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
        Assert.AreEqual(5, snapshot.Telemetry!.ElapsedMinutes);
        Assert.IsNull(WorkflowValidation.ProgressError(snapshot.Workflow, failedAt));
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
        Assert.AreEqual(WorkflowOperation.Build, snapshot.Workflow!.Operations.Single().Kind);
        Assert.AreEqual(1, process.CallCount);
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
        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
