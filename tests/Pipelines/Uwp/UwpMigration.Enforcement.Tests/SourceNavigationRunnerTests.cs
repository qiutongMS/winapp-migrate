// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;
using WinAppMigration.Cli.Pipelines.Uwp;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    [DataRow(false, false, true)]
    [DataRow(true, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, true, true)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    public async Task SourceNavigation_VerifiesCurrentStartingStateWithoutRequiringParentParity(
        bool wrongStart, bool replayBetween, bool captureSource)
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
        plan.States[0].Verification = new StateVerificationContract { TreeProbeIndex = 0 };
        if (captureSource)
        {
            await ObserveSourceAsync(fixture, plan, tree: sourceTree);
            await SourceNavigationContracts.EstablishAsync(fixture.Root, plan.States[0], "fixture-source",
                plan.States[0].Source.Evidence, DateTimeOffset.UtcNow, default);
        }
        const string sourceStorePath = ".migration-evidence/states/store/source-ui.json";
        if (captureSource)
        {
            await EnforcementStore.WriteTextAtomicAsync(fixture.Root, sourceStorePath, storeTree);
            store.Source = new StateObservation { Status = "captured", Evidence = [sourceStorePath], TreeEvidence = sourceStorePath };
        }
        store.Verification = new StateVerificationContract
        {
            StartSurfaceId = "state-1",
            RequiredActions = [new("open-store", 1, ["invoke", "Shop"])],
            TreeProbeIndex = 2
        };
        if (captureSource)
            await SourceNavigationContracts.EstablishAsync(fixture.Root, store, "fixture-source",
                store.Source.Evidence, DateTimeOffset.UtcNow, default);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await fixture.RunTargetAsync();
        var initial = await ContractProbeAsync(fixture, ["inspect", "--json", "-w", "42"], targetTree);
        var parent = await UwpEnforcementRunner.RecordSurfaceAsync(fixture.Root,
            TestWindowOwnerResolver.Shared, "state-1", [initial.ArtifactPath]);
        Assert.AreEqual(captureSource ? SurfaceVerificationResult.Mismatch : SurfaceVerificationResult.HumanValidationRequired,
            parent.StateVerifications.Single(item => item.Id == "state-1").Result);
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
            result = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
            Assert.AreEqual(captureSource ? SurfaceVerificationResult.Verified : SurfaceVerificationResult.HumanValidationRequired,
                result.StateVerifications.Single(item => item.Id == "store").Result);
            Assert.AreEqual("passed", result.Status);
            Assert.AreEqual("complete", result.Completion!.Outcome);
            Assert.IsEmpty(result.Completion.ActionableSurfaceIds);
            if (replayBetween)
            {
                var completedProcess = new FakeProcessRunner(ReplaySuccess());
                var completed = await new UwpEnforcementRunner(completedProcess)
                    .RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
                Assert.AreEqual(0, completedProcess.CallCount);
                Assert.AreEqual("complete", completed.Disposition!.Kind);
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task SourceNavigation_StopsAtFailedActionWithoutExecutingLaterSteps(bool captureSource)
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        if (captureSource)
            await ObserveSourceAsync(fixture, plan);
        plan.States[0].Verification = new StateVerificationContract
        {
            RequiredActions = [new("open-category", 1, ["invoke", "Create your own"]), new("choose-product", 2, ["invoke", "Floor Lamps"])],
            TreeProbeIndex = 3
        };
        if (captureSource)
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
    public async Task SourceNavigation_ReachabilityClosesWithoutSourceOrCompleteComparisonTree()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        plan.States[0].Verification = new StateVerificationContract { TreeProbeIndex = 0 };
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        await fixture.RunTargetAsync();
        var probe = await ContractProbeAsync(fixture, ["inspect", "--json", "-w", "42"],
            """{"windows":[{"elements":[{"type":"Window","name":"Ready","hasMoreChildren":true}]}]}""");
        var recorded = await UwpEnforcementRunner.RecordSurfaceAsync(fixture.Root,
            TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        var finalize = new UwpEnforceCommand(new FakeProcessRunner(), TestWindowOwnerResolver.Shared)
            .Parse(["finalize", fixture.Root.FullName, "--strict"]);
        Assert.AreEqual(0, await finalize.InvokeAsync(finalize.InvocationConfiguration, CancellationToken.None));
        recorded = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("passed", recorded.Status);
        Assert.AreEqual("ui-surface-reachability", recorded.VerificationScope);
        Assert.IsTrue(recorded.StateVerifications.Single().IsReachable);
        Assert.AreEqual(SurfaceVerificationResult.HumanValidationRequired, recorded.StateVerifications.Single().Result);
        Assert.IsTrue(recorded.HumanValidationItems.Any(item => item.StateId == "state-1"));
        var current = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        Assert.AreEqual("not-run", current.States[0].Source.Status);
        Assert.AreEqual("passed", current.States[0].Target.Status);
        Assert.AreEqual("unverified", current.States[0].Comparison.Status);
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
