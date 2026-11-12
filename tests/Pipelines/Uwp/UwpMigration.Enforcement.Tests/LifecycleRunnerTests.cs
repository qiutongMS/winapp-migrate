// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Run_PersistedCompleteRemainsPassedWithoutInvokingProcess(bool forceReplay)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = SuccessfulLaunch();
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        await fixture.VerifyStateAsync();
        var complete = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("complete", complete.Disposition?.Kind);
        Assert.AreEqual("passed", complete.Status);
        var callCount = process.CallCount;
        var progress = await File.ReadAllTextAsync(fixture.Path(EnforcementStore.ProgressPath));

        var resumed = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions
        {
            ForceReplay = forceReplay, DiscriminatingEvidence = [".migration-evidence/missing.txt"]
        });

        Assert.AreEqual("passed", resumed.Status);
        Assert.AreEqual("complete", resumed.Disposition?.Kind);
        Assert.IsTrue(resumed.Disposition!.Terminal);
        Assert.IsEmpty(resumed.Disposition.AllowedOperations);
        var saved = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        Assert.AreEqual("passed", saved.Status);
        Assert.AreEqual("complete", saved.Disposition?.Kind);
        Assert.AreEqual(progress, await File.ReadAllTextAsync(fixture.Path(EnforcementStore.ProgressPath)));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.DiagnoseAsync(fixture.Root, new UwpEnforcementOptions()));
        Assert.AreEqual("passed", (await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath)).Status);
        Assert.AreEqual("passed", (await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared)).Status);
        Assert.AreEqual(callCount, process.CallCount);
    }

    [TestMethod]
    public async Task Disposition_ProcessInfrastructureResultIsDurablyTerminal()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(new EnforcementProcessResult(-1, "", "Executable not found.")
        {
            InfrastructureFailure = true
        });
        var runner = new UwpEnforcementRunner(process);
        await Assert.ThrowsExactlyAsync<IOException>(() => runner.RunAsync(fixture.Root, new UwpEnforcementOptions()));
        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("infrastructure-failure", snapshot.Disposition?.Kind);
        Assert.AreEqual("process-invocation-failed", snapshot.Disposition?.ReasonCode);
        Assert.AreEqual(WorkflowOperation.Build, snapshot.Workflow!.Operations.Single().Kind);
        Assert.AreEqual(1, snapshot.Workflow.Units.Single().Operations.Builds);
        Assert.IsFalse(File.Exists(fixture.Path(EnforcementStore.BuildAttestationPath)));
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreEqual(1, process.CallCount);
    }

    [TestMethod]
    [DataRow("complete")]
    [DataRow("infrastructure-failure")]
    public async Task Disposition_PersistedTerminalRejectsAllEntryPointsBeforeFakeProcess(string kind)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = SuccessfulLaunch();
        var runner = new UwpEnforcementRunner(process);
        EnforcementSnapshot terminal;
        if (kind == "complete")
        {
            await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
            await fixture.VerifyStateAsync();
            terminal = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        }
        else
        {
            runner = new UwpEnforcementRunner(new InterruptingProcessRunner());
            await Assert.ThrowsExactlyAsync<IOException>(() =>
                runner.RunAsync(fixture.Root, new UwpEnforcementOptions()));
            terminal = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        }
        Assert.AreEqual(kind, terminal.Disposition?.Kind);
        Assert.IsTrue(terminal.Disposition!.Terminal);
        var expected = JsonSerializer.Serialize(terminal.Disposition, EnforcementStore.JsonOptions);
        var callCount = process.CallCount;
        if (kind != "complete")
            await File.AppendAllTextAsync(fixture.ProjectPath, Environment.NewLine);
        await File.WriteAllTextAsync(fixture.Path(".migration-evidence/terminal.txt"), "A terminal run cannot consume new evidence.");
        var rejected = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions
        {
            ForceReplay = true, DiscriminatingEvidence = [".migration-evidence/terminal.txt"]
        });
        var expectedStatus = kind == "complete" ? "passed" : "blocked";
        Assert.AreEqual(expectedStatus, rejected.Status);
        Assert.AreEqual(expected, JsonSerializer.Serialize(rejected.Disposition, EnforcementStore.JsonOptions));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.DiagnoseAsync(fixture.Root, new UwpEnforcementOptions()));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(fixture.Root, "winapp", ["list"], [".migration-evidence/terminal.txt"]));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordFailureAsync(fixture.Root, TestWindowOwnerResolver.Shared, "runtime", "terminal", "state-1", [".migration-evidence/terminal.txt"]));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordDiagnosisAsync(fixture.Root, TestWindowOwnerResolver.Shared, "terminal", "state-1",
                ["source.cs:1"], ["target.cs:1"], "Invariant", "Hypothesis", "Test", [".migration-evidence/terminal.txt"]));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared));
        Assert.AreEqual(callCount, process.CallCount);
        var observed = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual(expected, JsonSerializer.Serialize(observed.Disposition, EnforcementStore.JsonOptions));
        var saved = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        Assert.AreEqual(expectedStatus, saved.Status);
        Assert.AreEqual(expected, JsonSerializer.Serialize(saved.Disposition, EnforcementStore.JsonOptions));
    }

    [TestMethod]
    public async Task Disposition_SurfaceBlockLabelDoesNotPreventCaptureAndRecording()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(
            fixture.Root, new UwpEnforcementOptions());
        var plan = await fixture.LoadAsync<StatePlanDocument>(
            ".migration-evidence/state-plan.json");
        plan.States[0].Target.Status = "blocked";
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root, ".migration-evidence/state-plan.json", plan);

        var blocked = await UwpEnforcementRunner.AssessAsync(
            fixture.Root, TestWindowOwnerResolver.Shared);
        var probe = await new UwpEnforcementRunner(
            new FakeProcessRunner(new EnforcementProcessResult(0, ClosureTree, "")),
            windowOwnerResolver: TestWindowOwnerResolver.Shared)
            .InvokeUiAsync(fixture.Root, "winapp", ["inspect", "--json", "-w", "42"], [], "state-1");
        var recorded = await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root,
            TestWindowOwnerResolver.Shared,
            "state-1",
            [probe.ArtifactPath]);

        Assert.AreEqual("continue", blocked.Disposition?.Kind);
        Assert.IsFalse(blocked.Disposition!.Terminal);
        Assert.AreEqual(
            "passed",
            recorded.Gates.Single(gate => gate.Id == "state-closure").Status);
    }

    [TestMethod]
    [DataRow("1.0")]
    [DataRow("2.0")]
    public async Task Progress_UnsupportedSchemasPreserveBothArtifacts(string schema)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var initial = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        var json = JsonSerializer.SerializeToNode(initial.Workflow, EnforcementStore.JsonOptions)!.AsObject();
        json["schemaVersion"] = schema;
        var original = json.ToJsonString();
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, original);
        var stateJson = await File.ReadAllTextAsync(fixture.Path(EnforcementStore.StatePath));
        var process = new FakeProcessRunner();
        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("infrastructure-failure", snapshot.Disposition?.Kind);
        StringAssert.Contains(snapshot.Gates.Single(gate => gate.Id == "enforcement-artifacts").Reason, "Unsupported workflow progress schema");
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path(EnforcementStore.ProgressPath)));
        Assert.AreEqual(stateJson, await File.ReadAllTextAsync(fixture.Path(EnforcementStore.StatePath)));
        await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());
        Assert.AreEqual(0, process.CallCount);
    }

    [TestMethod]
    public async Task ProgressSchema10_ZeroUnitsFailsClosedWithoutRestartingEpoch()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var initial = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        var json = JsonSerializer.SerializeToNode(initial.Workflow, EnforcementStore.JsonOptions)!.AsObject();
        json["schemaVersion"] = "1.0";
        json["units"] = new JsonArray();
        json.Remove("slices");
        json.Remove("disposition");
        var original = json.ToJsonString();
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, original);
        var process = new FakeProcessRunner();

        var snapshot = await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());

        Assert.AreEqual("infrastructure-failure", snapshot.Disposition?.Kind);
        Assert.IsTrue(snapshot.Disposition!.Terminal);
        StringAssert.Contains(snapshot.Gates.Single(gate => gate.Id == "enforcement-artifacts").Reason,
            "Unsupported workflow progress schema");
        Assert.IsNull(snapshot.Workflow);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path(EnforcementStore.ProgressPath)));
        Assert.AreEqual(0, process.CallCount);
    }
}
