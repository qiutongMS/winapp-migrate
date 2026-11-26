// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Enforcement;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    public async Task WorkspaceLifecycle_LoadingAndAssessmentDoNotPerformPreparationWrites()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await fixture.LoadAsync<StatePlanDocument>(".migration-evidence/state-plan.json");
        var contract = await RequiredStateContracts.EstablishAsync(fixture.Root, plan);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.FindingLedgerPath,
            new SemanticFindingLedger
            {
                Findings =
                [
                    new SemanticFinding
                    {
                        Id = "pending-import", Category = "behavior", Status = "open",
                        SourceInvariant = "Preserve the original activation behavior.",
                        CompletionCondition = "Review the original activation scope."
                    }
                ]
            });
        await using var enforcementLock = await EnforcementStore.AcquireLockAsync(fixture.Root);
        var beforeLoad = CaptureWorkspaceFiles(fixture.Root);
        var anchor = StateClosureSeal.ReadContractAnchor("rc-doc-" + contract.Id);

        var loaded = await UwpEnforcementWorkspaceLoader.LoadAsync(
            fixture.Root, TimeProvider.System, CancellationToken.None);
        var todos = await TodoExecutionProgressStore.LoadAsync(fixture.Root, CancellationToken.None);

        Assert.IsEmpty(todos.Report.Todos);
        CollectionAssert.AreEquivalent(beforeLoad, CaptureWorkspaceFiles(fixture.Root));
        Assert.AreEqual(anchor, StateClosureSeal.ReadContractAnchor("rc-doc-" + contract.Id));
        Assert.AreEqual(contract.RequiredContractHash + "|open", anchor);
        var prepared = await UwpEnforcementWorkspacePreparation.PrepareAsync(loaded, CancellationToken.None);
        Assert.HasCount(1, prepared.Loaded.Files.Report.Todos);
        Assert.IsTrue(File.Exists(fixture.Path(EnforcementStore.FindingLedgerPath + ".imported")));
        Assert.AreEqual(contract.RequiredContractHash + "|frozen",
            StateClosureSeal.ReadContractAnchor("rc-doc-" + contract.Id));
        var beforeAssessment = CaptureWorkspaceFiles(fixture.Root);

        var workspace = UwpEnforcementWorkspaceAssessment.Assess(prepared, TestWindowOwnerResolver.Shared);
        var snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        UwpEnforcementWorkspaceAssessment.Assess(prepared, TestWindowOwnerResolver.Shared);

        Assert.HasCount(1, snapshot.HumanValidationItems);
        CollectionAssert.AreEquivalent(beforeAssessment, CaptureWorkspaceFiles(fixture.Root));
        Assert.AreEqual(contract.RequiredContractHash + "|frozen",
            StateClosureSeal.ReadContractAnchor("rc-doc-" + contract.Id));
    }

    [TestMethod]
    public async Task WorkspaceLifecycle_PreparationRecoversInterruptedHistoryWithoutRewritingAttestations()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(new FakeProcessRunner(
            BuildSuccess(), new EnforcementProcessResult(1, "", "Canonical launch failed.")))
            .RunAsync(fixture.Root, new UwpEnforcementOptions());
        var build = await File.ReadAllBytesAsync(fixture.Path(EnforcementStore.BuildAttestationPath));
        var replay = await File.ReadAllBytesAsync(fixture.Path(EnforcementStore.ReplayAttestationPath));
        File.Delete(fixture.Path(EnforcementStore.AttemptLedgerPath));
        await using var enforcementLock = await EnforcementStore.AcquireLockAsync(fixture.Root);

        var loaded = await UwpEnforcementWorkspaceLoader.LoadAsync(
            fixture.Root, TimeProvider.System, CancellationToken.None);

        Assert.IsEmpty(loaded.Attempts.Attempts);
        Assert.IsFalse(File.Exists(fixture.Path(EnforcementStore.AttemptLedgerPath)));
        var prepared = await UwpEnforcementWorkspacePreparation.PrepareAsync(loaded, CancellationToken.None);
        Assert.IsNull(prepared.ArtifactError);
        var recovered = await fixture.LoadAsync<AttemptLedger>(EnforcementStore.AttemptLedgerPath);
        Assert.HasCount(1, recovered.Attempts);
        Assert.AreEqual("runtime", recovered.Attempts.Single().Kind);
        Assert.AreEqual(loaded.Runtime.Replay!.OperationId, recovered.Attempts.Single().OperationId);
        CollectionAssert.AreEqual(build, await File.ReadAllBytesAsync(fixture.Path(EnforcementStore.BuildAttestationPath)));
        CollectionAssert.AreEqual(replay, await File.ReadAllBytesAsync(fixture.Path(EnforcementStore.ReplayAttestationPath)));
        var beforeAssessment = CaptureWorkspaceFiles(fixture.Root);

        var workspace = UwpEnforcementWorkspaceAssessment.Assess(prepared, TestWindowOwnerResolver.Shared);
        EnforcementEvaluator.Evaluate(workspace.Input);
        EnforcementEvaluator.Evaluate(workspace.Input);

        CollectionAssert.AreEquivalent(beforeAssessment, CaptureWorkspaceFiles(fixture.Root));
        Assert.HasCount(1, (await fixture.LoadAsync<AttemptLedger>(
            EnforcementStore.AttemptLedgerPath)).Attempts);
    }

    private static string[] CaptureWorkspaceFiles(DirectoryInfo root) =>
        Directory.GetFiles(root.FullName, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root.FullName, path).StartsWith(
                Path.Combine(EnforcementStore.EvidenceDirectory, ".locks") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                var relative = Path.GetRelativePath(root.FullName, path);
                return $"{relative}|{File.GetLastWriteTimeUtc(path).Ticks}|{StateClosureProtocol.FileHash(root, relative)}";
            }).ToArray();
}
