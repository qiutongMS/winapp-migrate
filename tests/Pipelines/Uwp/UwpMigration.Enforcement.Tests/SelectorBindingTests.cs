// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    public async Task SelectorBinding_RebindKeepsSemanticContractAndInvalidatesEarlierProbes()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        plan.States[0].Verification = new StateVerificationContract
        {
            RequiredActions =
            [
                new("invoke-target", 0, ["invoke", "Original"])
            ],
            TreeProbeIndex = 1
        };
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);
        var contract =
            await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        await ObserveSourceAsync(fixture, plan);
        await fixture.RunTargetAsync();
        var runner = new UwpEnforcementRunner(
            new FakeProcessRunner(
                new EnforcementProcessResult(0, ClosureTree, ""),
                new EnforcementProcessResult(0, ClosureTree, ""),
                new EnforcementProcessResult(0, ClosureTree, "")),
            windowOwnerResolver: TestWindowOwnerResolver.Shared);
        var stale = await runner.InvokeUiAsync(
            fixture.Root,
            "winapp",
            ["invoke", "Original", "-w", "42", "--json"],
            [],
            "state-1");

        var amendments =
            await UwpEnforcementRunner.RebindSelectorAsync(
                fixture.Root,
                "state-1",
                "invoke-target",
                ["invoke", "Replacement", "--type", "Button"],
                "The original name is no longer unique.");

        Assert.AreEqual(
            contract.RequiredContractHash,
            amendments.RequiredContractHash);
        Assert.AreEqual(1, amendments.Generation);
        Assert.IsNull(stale.SelectorBindingHash);
        var state = (await fixture.LoadAsync<StatePlanDocument>(
            ClosurePlan)).States.Single();
        Assert.AreEqual("unverified", state.Target.Status);
        Assert.AreEqual("unverified", state.Comparison.Status);
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => UwpEnforcementRunner.RecordSurfaceAsync(
                fixture.Root,
                TestWindowOwnerResolver.Shared,
                "state-1",
                [stale.ArtifactPath]));
        StringAssert.Contains(error.Message, "selector binding");

        var replacement = await runner.InvokeUiAsync(
            fixture.Root,
            "winapp",
            [
                "invoke",
                "Replacement",
                "--type",
                "Button",
                "-w",
                "42",
                "--json"
            ],
            [],
            "state-1");
        var inspection = await runner.InvokeUiAsync(
            fixture.Root,
            "winapp",
            ["inspect", "-w", "42", "--json"],
            [],
            "state-1");
        var recorded = await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root,
            TestWindowOwnerResolver.Shared,
            "state-1",
            [replacement.ArtifactPath, inspection.ArtifactPath]);

        Assert.IsNotNull(replacement.SelectorBindingHash);
        Assert.AreEqual(
            "passed",
            recorded.Gates.Single(
                gate => gate.Id == "state-closure").Status);
    }

    [TestMethod]
    public async Task SelectorBinding_RejectsSemanticCommandChanges()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        plan.States[0].Verification = new StateVerificationContract
        {
            RequiredActions =
            [
                new RequiredStateAction(
                    "open-surface",
                    0,
                    ["invoke", "Original"])
            ],
            TreeProbeIndex = 1
        };
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => UwpEnforcementRunner.RebindSelectorAsync(
                fixture.Root,
                "state-1",
                "open-surface",
                ["set-value", "Replacement", "changed"],
                "Attempt to change behavior."));

        StringAssert.Contains(error.Message, "semantic order");
        Assert.IsFalse(File.Exists(
            fixture.Path(SelectorBindingAmendments.Path)));
    }

    [TestMethod]
    public async Task SelectorBinding_OlderSignedGenerationCannotReplaceCurrentAnchor()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        plan.States[0].Verification = new StateVerificationContract
        {
            RequiredActions =
            [
                new RequiredStateAction(
                    "open-surface",
                    0,
                    ["invoke", "Original"])
            ],
            TreeProbeIndex = 1
        };
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        await UwpEnforcementRunner.RebindSelectorAsync(
            fixture.Root,
            "state-1",
            "open-surface",
            ["invoke", "Replacement"],
            "First selector repair.");
        var older = await File.ReadAllTextAsync(
            fixture.Path(SelectorBindingAmendments.Path));
        await UwpEnforcementRunner.RebindSelectorAsync(
            fixture.Root,
            "state-1",
            "open-surface",
            ["invoke", "Replacement-2"],
            "Second selector repair.");
        await File.WriteAllTextAsync(
            fixture.Path(SelectorBindingAmendments.Path),
            older);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            SelectorBindingAmendments.StateHash(
                fixture.Root,
                plan.States[0]));
    }

    [TestMethod]
    public async Task SelectorBinding_DeletingCurrentDocumentCannotResetAnchor()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        plan.States[0].Verification = new StateVerificationContract
        {
            RequiredActions =
            [
                new RequiredStateAction(
                    "open-surface",
                    0,
                    ["inspect", "Original"])
            ],
            TreeProbeIndex = 1
        };
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        await UwpEnforcementRunner.RebindSelectorAsync(
            fixture.Root,
            "state-1",
            "open-surface",
            ["inspect", "Replacement"],
            "Initial selector repair.");
        File.Delete(fixture.Path(SelectorBindingAmendments.Path));

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => UwpEnforcementRunner.RebindSelectorAsync(
                fixture.Root,
                "state-1",
                "open-surface",
                ["inspect", "Replacement-2"],
                "Attempt to reset the amendment history."));

        StringAssert.Contains(error.Message, "document is missing");
    }

    [TestMethod]
    public async Task SelectorBinding_HistoryPreventsLaterContractExtension()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        plan.States[0].Verification = new StateVerificationContract
        {
            RequiredActions =
            [
                new RequiredStateAction(
                    "open-surface",
                    0,
                    ["inspect", "Original"])
            ],
            TreeProbeIndex = 1
        };
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        await UwpEnforcementRunner.RebindSelectorAsync(
            fixture.Root,
            "state-1",
            "open-surface",
            ["inspect", "Replacement"],
            "Initial selector repair.");
        var verification = plan.States[0].Verification!;
        verification.RequiredActions.Add(
            new RequiredStateAction(
                "observe-again",
                1,
                ["inspect", "Again"]));
        verification.TreeProbeIndex = 2;
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SealContractAsync(
                fixture.Root,
                extend: true));
    }
}
