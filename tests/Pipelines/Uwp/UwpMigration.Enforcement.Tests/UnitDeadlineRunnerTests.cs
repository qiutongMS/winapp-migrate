// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    public async Task UnitElapsedTime_DoesNotCancelLateBuildOrReplay()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var clock = new ManualTimeProvider(DeadlineNow);
        var policy = EnforcementPolicy.Balanced();
        policy.MechanicalStabilization.SoftMinutes = policy.MechanicalStabilization.HardMinutes = 5;
        policy.TargetBootstrap.SoftMinutes = policy.TargetBootstrap.HardMinutes = 5;
        await UwpEnforcementRunner.ConfigurePolicyAsync(fixture.Root, policy);
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
        Assert.AreEqual("within-budget", snapshot.Budget?.Decision);
        Assert.IsFalse(snapshot.Disposition!.Terminal);
    }

    [TestMethod]
    public async Task UnitElapsedTime_DoesNotDeferOptionalSemanticWork()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var clock = new ManualTimeProvider(DeadlineNow);
        var policy = EnforcementPolicy.Balanced();
        policy.SemanticConvergence.SoftMinutes = policy.SemanticConvergence.HardMinutes = 5;
        await UwpEnforcementRunner.ConfigurePolicyAsync(fixture.Root, policy);
        var plan = await fixture.LoadAsync<StatePlanDocument>(".migration-evidence/state-plan.json");
        plan.States[0].Criticality = StateCriticality.Optional;
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root, ".migration-evidence/state-plan.json", plan);
        await new UwpEnforcementRunner(SuccessfulLaunch(), clock).RunAsync(
            fixture.Root, new UwpEnforcementOptions());
        clock.Advance(TimeSpan.FromMinutes(6));
        var process = new DelegateProcessRunner(token =>
        {
            Assert.IsFalse(token.IsCancellationRequested);
            return Task.FromResult(new EnforcementProcessResult(0, "UI completed.", ""));
        });
        await new UwpEnforcementRunner(process, clock).InvokeUiAsync(
            fixture.Root, "winapp", ["list"]);

        var snapshot = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        Assert.AreEqual(1, process.CallCount);
        Assert.IsEmpty(snapshot.Workflow!.Debt);
        Assert.AreEqual("within-budget", snapshot.Budget?.Decision);
        Assert.IsTrue(snapshot.Workflow.Units.All(unit => unit.BlockedAt is null));
    }
}
