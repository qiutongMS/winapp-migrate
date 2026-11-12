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
        Assert.AreEqual(12, snapshot.Telemetry!.TotalElapsedMinutes);
        Assert.IsFalse(snapshot.Disposition!.Terminal);
    }

}
