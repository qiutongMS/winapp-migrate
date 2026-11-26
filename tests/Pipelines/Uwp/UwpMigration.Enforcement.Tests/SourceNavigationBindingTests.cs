// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class SourceGatewayTests
{
    private const string InitialNavigationTree = """{"windows":[{"elements":[{"type":"Window","name":"Ready"}]}]}""";
    private const string StoreNavigationTree = """{"windows":[{"elements":[{"type":"Window","name":"Store"}]}]}""";
    private const string NavigationPlanPath = ".migration-evidence/state-plan.json";

    [TestMethod]
    public async Task SourceNavigation_SupplementalCaptureCannotReplaceTheFrozenBaseline()
    {
        using var fixture = await SourceFixture.CreateAsync(sourceVerified: true);
        var gateway = CreateGateway(LaunchedProcess(
            new(0, InitialNavigationTree, ""), new(0, StoreNavigationTree, "")), fixture);
        await gateway.RunAsync(fixture.Target, TestOptions());
        var initial = await gateway.CaptureAsync(fixture.Target, "winapp", ["inspect", "--json", "-w", "20"]);
        var plan = (await EnforcementStore.TryLoadAsync<StatePlanDocument>(fixture.Target, NavigationPlanPath)).Value!;
        plan.States[0].Verification = new StateVerificationContract { TreeProbeIndex = 0 };
        await EnforcementStore.WriteAtomicAsync(fixture.Target, NavigationPlanPath, plan);
        await UwpEnforcementRunner.BindSourceAsync(fixture.Target, "source-state", [initial.ArtifactPath]);
        var path = fixture.Resolve(SourceNavigationContracts.ContractPath("source-state"));
        var original = await File.ReadAllTextAsync(path);
        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Target, TestWindowOwnerResolver.Shared);
        Assert.IsFalse(snapshot.Gates.Any(gate => gate.Id == "source-oracle"));

        var supplemental = await gateway.CaptureAsync(fixture.Target, "winapp", ["inspect", "--json", "-w", "20"]);

        Assert.AreNotEqual(initial.ArtifactPath, supplemental.ArtifactPath);
        Assert.AreEqual(StoreNavigationTree, await File.ReadAllTextAsync(fixture.Resolve(supplemental.StandardOutputPath)));
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.BindSourceAsync(fixture.Target, "source-state", [supplemental.ArtifactPath]));
        StringAssert.Contains(error.Message, "changed after source verification");
        Assert.AreEqual(original, await File.ReadAllTextAsync(path));
        plan = (await EnforcementStore.TryLoadAsync<StatePlanDocument>(fixture.Target, NavigationPlanPath)).Value!;
        Assert.AreEqual(initial.StandardOutputPath, plan.States[0].Source.TreeEvidence);
        Assert.IsNotNull(SourceNavigationContracts.Require(fixture.Target, plan.States[0]));
    }

    [TestMethod]
    public async Task SourceNavigation_DoesNotFreezeAuthenticatedButTruncatedSourceTrees()
    {
        using var fixture = await SourceFixture.CreateAsync(sourceVerified: true);
        var process = LaunchedProcess(new EnforcementProcessResult(0,
            """{"windows":[{"elements":[{"type":"Window","name":"Ready","hasMoreChildren":true}]}]}""", ""));
        var gateway = CreateGateway(process, fixture);
        await gateway.RunAsync(fixture.Target, TestOptions());
        var initial = await gateway.CaptureAsync(fixture.Target, "winapp", ["inspect", "--json", "-w", "20"]);
        var plan = (await EnforcementStore.TryLoadAsync<StatePlanDocument>(fixture.Target, NavigationPlanPath)).Value!;
        plan.States[0].Verification = new StateVerificationContract { TreeProbeIndex = 0 };
        await EnforcementStore.WriteAtomicAsync(fixture.Target, NavigationPlanPath, plan);

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.BindSourceAsync(fixture.Target, "source-state", [initial.ArtifactPath]));

        StringAssert.Contains(error.Message, "complete initial and final");
        Assert.IsFalse(File.Exists(fixture.Resolve(SourceNavigationContracts.ContractPath("source-state"))));
    }

    [TestMethod]
    [DataRow(false, "")]
    [DataRow(true, "")]
    [DataRow(true, @".\")]
    public async Task SourceNavigation_BindsSurfacesIncrementallyAndFreezesObservedRoutes(bool windowsPaths, string prefix)
    {
        string ProbePath(SourceUiProbe probe) =>
            prefix + (windowsPaths ? probe.ArtifactPath.Replace('/', '\\') : probe.ArtifactPath);

        using var fixture = await SourceFixture.CreateAsync(sourceVerified: true, secondSurface: true);
        var process = LaunchedProcess(
            new(0, InitialNavigationTree, ""), new(0, """{"success":true}""", ""), new(0, StoreNavigationTree, ""));
        var gateway = CreateGateway(process, fixture);
        await gateway.RunAsync(fixture.Target, TestOptions());
        var initial = await gateway.CaptureAsync(fixture.Target, "winapp", ["inspect", "--json", "-w", "20"]);
        var plan = (await EnforcementStore.TryLoadAsync<StatePlanDocument>(fixture.Target, NavigationPlanPath)).Value!;
        plan.States[0].Verification = new StateVerificationContract { TreeProbeIndex = 0 };
        await EnforcementStore.WriteAtomicAsync(fixture.Target, NavigationPlanPath, plan);
        var first = await UwpEnforcementRunner.BindSourceAsync(fixture.Target, "source-state", [ProbePath(initial)]);
        plan = (await EnforcementStore.TryLoadAsync<StatePlanDocument>(fixture.Target, NavigationPlanPath)).Value!;
        Assert.IsNotNull(SourceNavigationContracts.Require(fixture.Target, plan.States[0]));
        CollectionAssert.AreEqual(
            new[] { initial.ArtifactPath, initial.StandardOutputPath, initial.StandardErrorPath },
            plan.States[0].Source.Evidence.ToArray());
        Assert.ThrowsExactly<InvalidDataException>(() => SourceNavigationContracts.Require(fixture.Target, plan.States[1]));

        var action = await gateway.CaptureAsync(fixture.Target, "winapp", ["invoke", "Shop", "-w", "20"]);
        var store = await gateway.CaptureAsync(fixture.Target, "winapp", ["inspect", "--json", "-w", "20"]);
        plan.States[1].Verification = new StateVerificationContract
        {
            StartSurfaceId = "source-state",
            RequiredActions = [new("open-store", 1, ["invoke", "Shop"])],
            TreeProbeIndex = 2
        };
        await EnforcementStore.WriteAtomicAsync(fixture.Target, NavigationPlanPath, plan);
        var second = await UwpEnforcementRunner.BindSourceAsync(fixture.Target, "store",
            [initial.ArtifactPath, ProbePath(action), ProbePath(store)]);
        Assert.AreEqual(first.RequiredContractHash, second.RequiredContractHash);
        plan = (await EnforcementStore.TryLoadAsync<StatePlanDocument>(fixture.Target, NavigationPlanPath)).Value!;
        Assert.IsNotNull(SourceNavigationContracts.Require(fixture.Target, plan.States[1]));
        plan.States[1].Verification!.RequiredActions[0].Command[1] = "Buy";
        Assert.ThrowsExactly<InvalidDataException>(() => SourceNavigationContracts.Require(fixture.Target, plan.States[1]));
    }

    [TestMethod]
    public async Task SourceNavigation_RejectsDuplicateReceiptPathAliases()
    {
        using var fixture = await SourceFixture.CreateAsync(sourceVerified: true);
        var gateway = CreateGateway(LaunchedProcess(new EnforcementProcessResult(0, InitialNavigationTree, "")), fixture);
        await gateway.RunAsync(fixture.Target, TestOptions());
        var initial = await gateway.CaptureAsync(fixture.Target, "winapp", ["inspect", "--json", "-w", "20"]);

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.BindSourceAsync(fixture.Target, "source-state",
                [initial.ArtifactPath, @".\" + initial.ArtifactPath.Replace('/', '\\')]));

        StringAssert.Contains(error.Message, "distinct source probe receipts");
        Assert.IsFalse(File.Exists(fixture.Resolve(SourceNavigationContracts.ContractPath("source-state"))));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SourceNavigation_RejectsUnsafeReceiptPathsBeforeNormalization(bool absolute)
    {
        using var fixture = await SourceFixture.CreateAsync(sourceVerified: true);
        var gateway = CreateGateway(LaunchedProcess(new EnforcementProcessResult(0, InitialNavigationTree, "")), fixture);
        await gateway.RunAsync(fixture.Target, TestOptions());
        var initial = await gateway.CaptureAsync(fixture.Target, "winapp", ["inspect", "--json", "-w", "20"]);
        var path = absolute ? fixture.Resolve(initial.ArtifactPath) :
            @"..\" + fixture.Target.Name + @"\" + initial.ArtifactPath.Replace('/', '\\');

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.BindSourceAsync(fixture.Target, "source-state", [path]));

        StringAssert.Contains(error.Message, absolute ? "target-relative" : "escapes");
        Assert.IsFalse(File.Exists(fixture.Resolve(SourceNavigationContracts.ContractPath("source-state"))));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SourceNavigation_RejectsWarmStartupAndTamperedBaseline(bool tamper)
    {
        using var fixture = await SourceFixture.CreateAsync(sourceVerified: true);
        var process = LaunchedProcess(new(0, InitialNavigationTree, ""),
            new(0, """{"success":true}""", ""), new(0, InitialNavigationTree, ""));
        var gateway = CreateGateway(process, fixture);
        await gateway.RunAsync(fixture.Target, TestOptions());
        var initial = await gateway.CaptureAsync(fixture.Target, "winapp", ["inspect", "--json", "-w", "20"]);
        await gateway.CaptureAsync(fixture.Target, "winapp", ["invoke", "Shop", "-w", "20"]);
        var late = await gateway.CaptureAsync(fixture.Target, "winapp", ["inspect", "--json", "-w", "20"]);
        var plan = (await EnforcementStore.TryLoadAsync<StatePlanDocument>(fixture.Target, NavigationPlanPath)).Value!;
        plan.States[0].Verification = new StateVerificationContract { TreeProbeIndex = 0 };
        await EnforcementStore.WriteAtomicAsync(fixture.Target, NavigationPlanPath, plan);
        if (tamper)
            await File.WriteAllTextAsync(fixture.Resolve(initial.StandardOutputPath), StoreNavigationTree);
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.BindSourceAsync(fixture.Target, "source-state",
                [(tamper ? initial.ArtifactPath : late.ArtifactPath).Replace('/', '\\')]));
        StringAssert.Contains(error.Message, tamper ? "authenticated" : "after navigation");
        Assert.IsFalse(File.Exists(fixture.Resolve(SourceNavigationContracts.ContractPath("source-state"))));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SourceNavigation_RejectsOmittedFailedActionEvenIfReceiptDeleted(bool delete)
    {
        using var fixture = await SourceFixture.CreateAsync(sourceVerified: true);
        var process = LaunchedProcess(new(0, InitialNavigationTree, ""), new(1, "", "disabled"),
            new(0, """{"success":true}""", ""), new(0, StoreNavigationTree, ""));
        var gateway = CreateGateway(process, fixture);
        await gateway.RunAsync(fixture.Target, TestOptions());
        var initial = await gateway.CaptureAsync(fixture.Target, "winapp", ["inspect", "--json", "-w", "20"]);
        var failed = await gateway.CaptureAsync(fixture.Target, "winapp", ["invoke", "Shop", "-w", "20"]);
        var succeeded = await gateway.CaptureAsync(fixture.Target, "winapp", ["invoke", "Shop", "-w", "20"]);
        var tree = await gateway.CaptureAsync(fixture.Target, "winapp", ["inspect", "--json", "-w", "20"]);
        if (delete)
            File.Delete(fixture.Resolve(failed.ArtifactPath));
        var plan = (await EnforcementStore.TryLoadAsync<StatePlanDocument>(fixture.Target, NavigationPlanPath)).Value!;
        plan.States[0].Verification = new StateVerificationContract
        {
            RequiredActions = [new("open-store", 1, ["invoke", "Shop"])],
            TreeProbeIndex = 2
        };
        await EnforcementStore.WriteAtomicAsync(fixture.Target, NavigationPlanPath, plan);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.BindSourceAsync(fixture.Target, "source-state",
                [initial.ArtifactPath, succeeded.ArtifactPath, tree.ArtifactPath]));
    }
}
