// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProcessIdentity_SamePidReplacementBeforeOrDuringCaptureIsRejected(bool during)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var owner = new MutableIdentityResolver();
        var process = new IdentityProcessRunner();
        var runner = new UwpEnforcementRunner(process, windowOwnerResolver: owner);
        var replayed = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        Assert.AreEqual(owner.StartedAt, replayed.Replay!.ProcessStartedAt);
        if (during)
            process.OnUi = () => owner.StartedAt = owner.StartedAt.AddHours(1);
        else
            owner.StartedAt = owner.StartedAt.AddHours(1);

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(fixture.Root, "winapp", ["inspect", "-w", "42", "--json"], [], "state-1"));

        StringAssert.Contains(error.Message, "reused-PID");
        Assert.AreEqual(during ? 3 : 2, process.Calls);
        Assert.IsFalse(File.Exists(fixture.Path(EnforcementStore.UiProbePath)));
    }

    [TestMethod]
    public async Task ProcessIdentity_ReplayCannotBeAttestedWithoutProcessCreationIdentity()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var owner = new MutableIdentityResolver { Available = false };
        var runner = new UwpEnforcementRunner(new IdentityProcessRunner(), windowOwnerResolver: owner);

        var snapshot = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());

        Assert.IsFalse(snapshot.Replay!.Success);
        Assert.IsNull(snapshot.Replay.ProcessStartedAt);
        StringAssert.Contains(snapshot.Replay.Error!, "stable process identity");
        Assert.AreEqual("blocked", snapshot.Gates.Single(gate => gate.Id == "canonical-replay").Status);
    }

    [TestMethod]
    public async Task ProcessIdentity_StableOwnerIsSealedAndAccepted()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var probe = await CaptureClosureProbeAsync(fixture);
        var replay = await fixture.LoadAsync<ReplayAttestation>(EnforcementStore.ReplayAttestationPath);

        Assert.AreEqual(replay.ProcessStartedAt, probe.WindowProcessStartedAt);
        Assert.IsNotNull(probe.WindowProcessStartedAt);
        Assert.IsTrue(StateClosureSeal.Verify(probe));
        var recorded = await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        Assert.AreEqual("passed", recorded.Gates.Single(gate => gate.Id == "state-closure").Status);
        probe.WindowProcessStartedAt = probe.WindowProcessStartedAt.Value.AddHours(1);
        Assert.IsFalse(StateClosureSeal.Verify(probe));
    }

    [TestMethod]
    [DataRow("missing-probe-start")]
    [DataRow("different-probe-start")]
    [DataRow("missing-replay-start")]
    [DataRow("different-replay-start")]
    public async Task ProcessIdentity_DerivationCannotReuseAStablePidForAnotherInstance(string mismatch)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var probe = await CaptureClosureProbeAsync(fixture);
        if (mismatch.EndsWith("probe-start", StringComparison.Ordinal))
        {
            probe.WindowProcessStartedAt = mismatch.StartsWith("missing", StringComparison.Ordinal) ?
                null : probe.WindowProcessStartedAt!.Value.AddHours(1);
            StateClosureSeal.Sign(probe);
            await PersistTrackedProbeAsync(fixture, probe);
        }
        else
        {
            var replay = await fixture.LoadAsync<ReplayAttestation>(EnforcementStore.ReplayAttestationPath);
            replay.ProcessStartedAt = mismatch.StartsWith("missing", StringComparison.Ordinal) ?
                null : replay.ProcessStartedAt!.Value.AddHours(1);
            await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.ReplayAttestationPath, replay);
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));
    }

    private sealed class MutableIdentityResolver : IWindowOwnerResolver
    {
        internal DateTimeOffset StartedAt { get; set; } = TestWindowOwnerResolver.ProcessStartedAt;
        internal bool Available { get; set; } = true;
        public bool TryGetProcessId(long windowHandle, out int processId)
        {
            processId = 4242;
            return windowHandle == TestWindowOwnerResolver.WindowHandle;
        }
        public bool TryGetProcessIdentity(int processId, out ProcessIdentity identity)
        {
            identity = new ProcessIdentity(processId, StartedAt);
            return Available;
        }
    }

    private sealed class IdentityProcessRunner : IEnforcementProcessRunner
    {
        internal int Calls { get; private set; }
        internal Action? OnUi { get; set; }
        public Task<EnforcementProcessResult> RunAsync(string executable, DirectoryInfo workingDirectory,
            IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (arguments[0] == "ui")
            {
                OnUi?.Invoke();
                return Task.FromResult(new EnforcementProcessResult(0, ClosureTree, ""));
            }
            return Task.FromResult(Calls == 1 ? BuildSuccess() : ReplaySuccess());
        }
    }
}
