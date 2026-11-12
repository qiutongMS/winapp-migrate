// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.CommandLine;
using WinAppMigration.Cli.Pipelines.Uwp;
using WinAppMigration.Enforcement;
using UwpMigration.Contracts;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    public async Task RequiredSurfaceContract_TodoStatusBeforeSealDoesNotStartOperations()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var command = new UwpTodoCommand(TestWindowOwnerResolver.Shared);
        Assert.AreEqual(0, await command.Parse(["status", fixture.Root.FullName]).InvokeAsync());
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        Assert.IsNull(progress.OperationsStartedAt);
        Assert.IsEmpty(progress.Operations);

        var contract = await UwpEnforcementRunner.SealContractAsync(fixture.Root);

        Assert.HasCount(1, contract.States);
        Assert.AreEqual(contract.RequiredContractHash,
            RequiredStateContracts.Require(fixture.Root, contract.States[0]));
    }

    [TestMethod]
    public void SemanticFindingContract_UsesSurfaceTerminology()
    {
        var json = JsonSerializer.Serialize(
            new SemanticFindingLedger
            {
                Findings =
                [
                    new SemanticFinding
                    {
                        Id = "SEM001",
                        Category = "ui",
                        StateIds = ["settings"],
                        SourceInvariant = "Settings remains reachable.",
                        Status = "open",
                        CompletionCondition = "The settings surface matches.",
                        CompletionAssertions =
                        [
                            new FindingCompletionAssertion
                            {
                                StateId = "settings",
                                AssertionId =
                                    SurfaceCheckIds.StructureMatch
                            }
                        ]
                    }
                ]
            },
            EnforcementStore.JsonOptions);

        StringAssert.Contains(json, "\"surfaceIds\"");
        StringAssert.Contains(json, "\"completionChecks\"");
        StringAssert.Contains(json, "\"surfaceId\"");
        StringAssert.Contains(json, "\"checkId\"");
        Assert.IsFalse(json.Contains("\"stateIds\"", StringComparison.Ordinal));
        Assert.IsFalse(
            json.Contains(
                "\"completionAssertions\"",
                StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("kind")]
    [DataRow("definition")]
    [DataRow("navigation")]
    public async Task RequiredSurfaceContract_SourceCaptureFreezesInventory(
        string mutation)
    {
        using var fixture =
            await RunnerFixture.CreateRequiredAsync(observeSource: true);
        var plan = fixture.RequiredPlan;
        if (mutation == "kind")
            plan.States[0].Kind = "dialog";
        if (mutation == "definition")
            plan.States[0].SourceDefinitions[0] = "OtherPage.xaml";
        if (mutation == "navigation")
            plan.States[0].Verification!.TreeProbeIndex = 1;
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            RequiredStateContracts.Require(
                fixture.Root,
                plan.States[0]));
    }

    [TestMethod]
    public async Task RequiredSurfaceContract_AppendOnlyInventoryExtensionIsExplicit()
    {
        using var fixture = await RunnerFixture.CreateRequiredAsync();
        var plan = fixture.RequiredPlan;
        var initial = fixture.RequiredContract;
        plan.States.Add(new MigrationState
        {
            Id = "settings-dialog",
            Kind = "dialog",
            SourceDefinitions = ["SettingsDialog.xaml"],
            Verification = new StateVerificationContract
            {
                TreeProbeIndex = 0
            },
            Source = new StateObservation { Status = "not-run" },
            Target = new StateObservation { Status = "not-run" },
            Comparison = new StateComparison { Status = "not-run" }
        });
        await File.WriteAllTextAsync(
            fixture.Path("SettingsDialog.xaml"),
            """
            <ContentDialog xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />
            """);
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);

        var extended =
            await UwpEnforcementRunner.SealContractAsync(
                fixture.Root,
                extend: true);

        Assert.AreEqual(initial.BaselineHash, extended.BaselineHash);
        Assert.AreNotEqual(
            initial.RequiredContractHash,
            extended.RequiredContractHash);
    }

    [TestMethod]
    public async Task RequiredSurfaceContract_ExcludedSurfaceRequiresReason()
    {
        using var fixture =
            await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        Directory.CreateDirectory(
            fixture.Path("Controls"));
        await File.WriteAllTextAsync(
            fixture.Path(
                "Controls/InternalTemplate.xaml"),
            """
            <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />
            """);
        plan.States.Add(new MigrationState
        {
            Id = "internal-template",
            Kind = "user-control",
            Disposition = "excluded",
            SourceDefinitions = ["Controls/InternalTemplate.xaml"],
            Source = new StateObservation { Status = "excluded" },
            Target = new StateObservation { Status = "excluded" },
            Comparison = new StateComparison { Status = "excluded" }
        });
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SealContractAsync(fixture.Root));

        plan.States[^1].ExclusionReason =
            "This reusable template is not independently reachable.";
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
    }

    [TestMethod]
    public async Task RequiredSurfaceContract_DeletingJsonDoesNotResetAnchor()
    {
        using var fixture = await RunnerFixture.CreateRequiredAsync();
        File.Delete(fixture.Path(RequiredStateContracts.Path));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SealContractAsync(fixture.Root));
    }

    [TestMethod]
    public async Task SurfaceClosure_SemanticTreeMismatchIsDefiniteFailure()
    {
        using var fixture =
            await RunnerFixture.CreateRequiredAsync(observeSource: true);
        await AddRequiredTodoAndFindingAsync(
            fixture,
            [SurfaceCheckIds.TargetReachable, SurfaceCheckIds.StructureMatch]);
        await fixture.RunTargetAsync();
        var probe = await ContractProbeAsync(
            fixture,
            ["inspect", "-w", "42", "--json"],
            ClosureTree.Replace("Ready", "Different", StringComparison.Ordinal));

        var recorded =
            await UwpEnforcementRunner.RecordSurfaceAsync(
                fixture.Root,
                TestWindowOwnerResolver.Shared,
                "state-1",
                [probe.ArtifactPath]);

        Assert.AreEqual(
            StateVerificationLevel.SurfaceReachable,
            recorded.StateVerifications.Single().Level);
        var state =
            (await fixture.LoadAsync<StatePlanDocument>(ClosurePlan))
            .States.Single();
        Assert.AreEqual("passed", state.Target.Status);
        Assert.AreEqual("failed", state.Comparison.Status);
        StringAssert.Contains(
            state.Comparison.Reason!,
            "/name");
    }

    [TestMethod]
    public async Task SurfaceClosure_MatchingSemanticTreeVerifiesBothChecks()
    {
        using var fixture =
            await RunnerFixture.CreateRequiredAsync(observeSource: true);
        await fixture.RunTargetAsync();
        var probe = await ContractProbeAsync(
            fixture,
            ["inspect", "-w", "42", "--json"],
            ClosureTree);

        await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root,
            TestWindowOwnerResolver.Shared,
            "state-1",
            [probe.ArtifactPath]);

        var state =
            (await fixture.LoadAsync<StatePlanDocument>(ClosurePlan))
            .States.Single();
        var proof =
            await fixture.LoadAsync<StateClosureAttestation>(
                state.Target.Attestation!);
        CollectionAssert.AreEqual(
            SurfaceCheckIds.All.ToArray(),
            proof.CheckIds);
        Assert.AreEqual(
            StateVerificationLevel.SurfaceParityVerified,
            proof.VerificationLevel);
    }

    [TestMethod]
    public async Task SurfaceClosure_TamperedAttestationFailsBeforeCanonicalMutation()
    {
        using var fixture =
            await RunnerFixture.CreateRequiredAsync(observeSource: true);
        await fixture.RunTargetAsync();
        var probe = await ContractProbeAsync(
            fixture,
            ["inspect", "-w", "42", "--json"],
            ClosureTree);
        await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root,
            TestWindowOwnerResolver.Shared,
            "state-1",
            [probe.ArtifactPath]);
        var state =
            (await fixture.LoadAsync<StatePlanDocument>(ClosurePlan))
            .States.Single();
        var proof =
            await fixture.LoadAsync<StateClosureAttestation>(
                state.Target.Attestation!);
        proof.Issues.Add("tampered");
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            state.Target.Attestation!,
            proof);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SynchronizeReportAsync(
                fixture.Root,
                TestWindowOwnerResolver.Shared));
    }

    [TestMethod]
    public async Task RequiredSurfaceContract_WorkflowStartLocksPlan()
    {
        using var fixture = await RunnerFixture.CreateRequiredAsync();
        var plan = fixture.RequiredPlan;
        await UwpEnforcementRunner.AssessAsync(
            fixture.Root,
            TestWindowOwnerResolver.Shared);
        plan.States[0].SourceDefinitions[0] = "OtherPage.xaml";
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SealContractAsync(
                fixture.Root,
                extend: true));
    }

    [TestMethod]
    public async Task RequiredSurfaceContract_LegacySchemaIsRejected()
    {
        using var fixture =
            await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        plan.SchemaVersion = "2.0";
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SealContractAsync(fixture.Root));
    }

    [TestMethod]
    public async Task RequiredSurfaceContract_RejectsUnaccountedSourceSurface()
    {
        using var fixture =
            await RunnerFixture.CreateAsync(unstarted: true);
        _ = await NewRequiredPlanAsync(fixture);
        await File.WriteAllTextAsync(
            fixture.Path("SettingsPage.xaml"),
            """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />
            """);

        var exception =
            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => UwpEnforcementRunner.SealContractAsync(
                    fixture.Root));

        StringAssert.Contains(
            exception.Message,
            "SettingsPage.xaml");
    }

    private static async Task<StatePlanDocument> NewRequiredPlanAsync(
        RunnerFixture fixture)
    {
        var plan =
            await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        var state = plan.States[0];
        state.Kind = "page";
        state.Disposition = "required";
        state.SourceDefinitions = ["MainPage.xaml"];
        state.Verification = new StateVerificationContract
        {
            TreeProbeIndex = 0
        };
        state.Source.Status = "not-run";
        state.Source.Evidence = [];
        state.Source.TreeEvidence = null;
        state.Target.Status = "not-run";
        state.Target.Evidence = [];
        state.Target.TreeEvidence = null;
        state.Comparison.Status = "not-run";
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);
        return plan;
    }

    private static async Task ObserveSourceAsync(
        RunnerFixture fixture,
        StatePlanDocument plan,
        string? tree = null,
        byte[]? png = null)
    {
        const string path =
            ".migration-evidence/states/state-1/source-ui.json";
        Directory.CreateDirectory(
            fixture.Path(".migration-evidence/states/state-1"));
        await File.WriteAllTextAsync(
            fixture.Path(path),
            tree ?? ClosureTree);
        plan.States[0].Source.Status = "captured";
        plan.States[0].Source.Evidence = [path];
        plan.States[0].Source.TreeEvidence = path;
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);
    }

    private static Task<UiDevelopmentProbe> ContractProbeAsync(
        RunnerFixture fixture,
        string[] arguments,
        string output) =>
        new UwpEnforcementRunner(
            new FakeProcessRunner(
                new EnforcementProcessResult(0, output, "")),
            windowOwnerResolver: TestWindowOwnerResolver.Shared)
        .InvokeUiAsync(
            fixture.Root,
            "winapp",
            arguments,
            [],
            "state-1");

    private static async Task AddRequiredTodoAndFindingAsync(
        RunnerFixture fixture,
        string[] checkIds)
    {
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(new MigrationTodo
        {
            Id = "SURFACE-REQUIRED",
            Category = "behavior",
            Priority = "required",
            Summary = "Complete the required UI surface.",
            Reason = "Requires reachability and semantic UIA parity.",
            AffectedStates = ["state-1"],
            Guidance = new MigrationRepairGuidance
            {
                ReasonCode = "surface-parity",
                CompletionChecks =
                [
                    new MigrationCompletionCheck
                    {
                        Id = "surface-contract",
                        Summary =
                            "Verify reachability and semantic UIA structure.",
                        VerificationKind = "surface-check",
                        Target = "state-1",
                        Expected = "passed",
                        AssertionIds = [.. checkIds]
                    }
                ]
            }
        });
        await fixture.SaveReportAsync(report);
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            EnforcementStore.FindingLedgerPath,
            new SemanticFindingLedger
            {
                Findings =
                [
                    new SemanticFinding
                    {
                        Id = "surface-contract",
                        Category = "surface",
                        StateIds = ["state-1"],
                        Status = "open",
                        SourceInvariant =
                            "Preserve the complete semantic UIA surface.",
                        CompletionCondition =
                            "Reachability and structure checks pass.",
                        NextActions =
                            ["Verify complete UI surface parity."],
                        CompletionAssertions = checkIds.Select(id =>
                            new FindingCompletionAssertion
                            {
                                StateId = "state-1",
                                AssertionId = id
                            }).ToList()
                    }
                ]
            });
    }
}
