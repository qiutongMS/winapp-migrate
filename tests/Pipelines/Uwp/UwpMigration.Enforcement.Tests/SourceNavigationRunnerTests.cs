// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task SourceNavigation_VerifiesCurrentStartingStateWithoutRequiringParentParity(bool wrongStart, bool replayBetween)
    {
        const string sourceTree = """{"windows":[{"elements":[{"type":"Window","automationId":"main","name":"Source"}]}]}""";
        const string targetTree = """{"windows":[{"elements":[{"type":"Window","automationId":"main","name":"Migrated"}]}]}""";
        const string storeTree = """{"windows":[{"elements":[{"type":"Window","automationId":"main","name":"Store"}]}]}""";
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        await File.WriteAllTextAsync(fixture.Path("StorePage.xaml"),
            """<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />""");
        var store = new MigrationState
        {
            Id = "store",
            SourceDefinitions = ["StorePage.xaml"],
            Source = new StateObservation { Status = "not-run" },
            Target = new StateObservation { Status = "not-run" },
            Comparison = new StateComparison { Status = "not-run" }
        };
        plan.States.Add(store);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        await ObserveSourceAsync(fixture, plan, tree: sourceTree);
        await SourceNavigationContracts.EstablishAsync(fixture.Root, plan.States[0], "fixture-source",
            plan.States[0].Source.Evidence, DateTimeOffset.UtcNow, default);
        const string sourceStorePath = ".migration-evidence/states/store/source-ui.json";
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, sourceStorePath, storeTree);
        store.Source = new StateObservation { Status = "captured", Evidence = [sourceStorePath], TreeEvidence = sourceStorePath };
        store.Verification = new StateVerificationContract
        {
            StartSurfaceId = "state-1",
            RequiredActions = [new("open-store", 1, ["invoke", "Shop"])],
            TreeProbeIndex = 2
        };
        await SourceNavigationContracts.EstablishAsync(fixture.Root, store, "fixture-source",
            store.Source.Evidence, DateTimeOffset.UtcNow, default);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await fixture.RunTargetAsync();
        var initial = await ContractProbeAsync(fixture, ["inspect", "--json", "-w", "42"], targetTree);
        var parent = await UwpEnforcementRunner.RecordSurfaceAsync(fixture.Root,
            TestWindowOwnerResolver.Shared, "state-1", [initial.ArtifactPath]);
        Assert.AreEqual(SurfaceVerificationResult.Mismatch, parent.StateVerifications.Single(item => item.Id == "state-1").Result);
        if (replayBetween)
            await new UwpEnforcementRunner(new FakeProcessRunner(ReplaySuccess()))
                .RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        var process = new FakeProcessRunner(new(0, wrongStart ? storeTree : targetTree, ""),
            new(0, """{"success":true}""", ""), new(0, storeTree, ""));
        var runner = new UwpEnforcementRunner(process);
        if (wrongStart)
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                runner.NavigateAsync(fixture.Root, "store", "winapp", 42));
            StringAssert.Contains(error.Message, "start surface");
            Assert.AreEqual(1, process.CallCount);
        }
        else
        {
            var probes = await runner.NavigateAsync(fixture.Root, "store", "winapp", 42);
            Assert.HasCount(3, probes);
            var result = await UwpEnforcementRunner.RecordSurfaceAsync(fixture.Root,
                TestWindowOwnerResolver.Shared, "store", probes.Select(probe => probe.ArtifactPath).ToArray());
            Assert.AreEqual(SurfaceVerificationResult.Verified, result.StateVerifications.Single(item => item.Id == "store").Result);
            Assert.AreEqual("blocked", result.Status);
            if (replayBetween)
            {
                var replayed = await new UwpEnforcementRunner(new FakeProcessRunner(ReplaySuccess()))
                    .RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
                Assert.AreEqual(SurfaceVerificationResult.Verified,
                    replayed.StateVerifications.Single(item => item.Id == "store").Result);
                Assert.AreEqual(SurfaceVerificationResult.Mismatch,
                    replayed.StateVerifications.Single(item => item.Id == "state-1").Result);
            }
        }
    }

    [TestMethod]
    public async Task SourceNavigation_InventoryAllowsDraftRouteChangesButNotObligationChanges()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        plan.States[0].Verification = null;
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        var contract = await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        plan.States[0].Verification = new StateVerificationContract
        {
            RequiredActions = [new("buy-compact", 1, ["send-keys", "Shift+Enter", "--target", "BuyButton", "--via", "send-input"])],
            TreeProbeIndex = 2
        };
        Assert.AreEqual(contract.RequiredContractHash, RequiredStateContracts.Require(fixture.Root, plan.States[0]));
        plan.States[0].Disposition = "excluded";
        Assert.ThrowsExactly<InvalidDataException>(() => RequiredStateContracts.Require(fixture.Root, plan.States[0]));
    }

    [TestMethod]
    public async Task SourceNavigation_StopsAtFailedActionWithoutExecutingLaterSteps()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        await ObserveSourceAsync(fixture, plan);
        plan.States[0].Verification = new StateVerificationContract
        {
            RequiredActions = [new("open-category", 1, ["invoke", "Create your own"]), new("choose-product", 2, ["invoke", "Floor Lamps"])],
            TreeProbeIndex = 3
        };
        await SourceNavigationContracts.EstablishAsync(fixture.Root, plan.States[0], "fixture-source",
            plan.States[0].Source.Evidence, DateTimeOffset.UtcNow, default);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await fixture.RunTargetAsync();
        var process = new FakeProcessRunner(new(0, ClosureTree, ""), new(1, "", "disabled"), new(0, "", ""));
        var probes = await new UwpEnforcementRunner(process).NavigateAsync(fixture.Root, "state-1", "winapp", 42);
        Assert.HasCount(2, probes);
        Assert.AreEqual(1, probes[^1].ExitCode);
        Assert.AreEqual(2, process.CallCount);
        Assert.IsFalse(File.Exists(fixture.Path(StateClosureProtocol.AttestationPath("state-1"))));
    }

    [TestMethod]
    public async Task SourceNavigation_BoundBaselineCannotBeEditedOrRecreatedAfterDeletion()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        await ObserveSourceAsync(fixture, plan);
        await SourceNavigationContracts.EstablishAsync(fixture.Root, plan.States[0], "fixture-source",
            plan.States[0].Source.Evidence, DateTimeOffset.UtcNow, default);
        await File.AppendAllTextAsync(fixture.Path(plan.States[0].Source.TreeEvidence!), " ");
        Assert.ThrowsExactly<InvalidDataException>(() => SourceNavigationContracts.Require(fixture.Root, plan.States[0]));
        File.Delete(fixture.Path(SourceNavigationContracts.ContractPath("state-1")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            SourceNavigationContracts.EstablishAsync(fixture.Root, plan.States[0], "fixture-source",
                plan.States[0].Source.Evidence, DateTimeOffset.UtcNow, default));
    }
}
