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
    [DataRow("blocked-required")]
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
        else if (kind == "blocked-required")
        {
            var plan = await fixture.LoadAsync<StatePlanDocument>(
                ".migration-evidence/state-plan.json");
            plan.States[0].Target.Status = "blocked";
            await EnforcementStore.WriteAtomicAsync(
                fixture.Root, ".migration-evidence/state-plan.json", plan);
            terminal = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        }
        else
        {
            await EnforcementStore.WriteTextAtomicAsync(fixture.Root, EnforcementStore.PolicyPath, "{");
            terminal = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
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
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.ConfigurePolicyAsync(fixture.Root, EnforcementPolicy.Balanced()));
        Assert.AreEqual(callCount, process.CallCount);
        var observed = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual(expected, JsonSerializer.Serialize(observed.Disposition, EnforcementStore.JsonOptions));
        var saved = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        Assert.AreEqual(expectedStatus, saved.Status);
        Assert.AreEqual(expected, JsonSerializer.Serialize(saved.Disposition, EnforcementStore.JsonOptions));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProgressSchema10_ImportsSafeJsonAndPreservesAmbiguousJson(bool ambiguous)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var initial = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        var json = JsonSerializer.SerializeToNode(initial.Workflow, EnforcementStore.JsonOptions)!.AsObject();
        json["schemaVersion"] = "1.0";
        json.Remove("slices");
        json.Remove("disposition");
        var units = json["units"]!.AsArray();
        units[0]!.AsObject().Remove("epochs");
        units[0]!.AsObject().Remove("accountId");
        if (ambiguous)
        {
            var other = units[0]!.DeepClone();
            other["stage"] = "source-oracle";
            units.Add(other);
        }
        var original = json.ToJsonString();
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, original);
        var process = new FakeProcessRunner();
        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        if (ambiguous)
        {
            Assert.AreEqual("infrastructure-failure", snapshot.Disposition?.Kind);
            StringAssert.Contains(snapshot.Gates.Single(gate => gate.Id == "enforcement-artifacts").Reason, "Cannot safely import");
            Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path(EnforcementStore.ProgressPath)));
            await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());
            Assert.AreEqual(0, process.CallCount);
        }
        else
        {
            var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
            Assert.AreEqual("3.0", progress.SchemaVersion);
            Assert.AreEqual(initial.Workflow!.StartedAt, progress.StartedAt);
            Assert.AreEqual(initial.Workflow.Units[0].StartedAt, progress.Units[0].Epochs[0].EnteredAt);
        }
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
            "exactly one reconstructable budget unit");
        Assert.IsNull(snapshot.Workflow);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path(EnforcementStore.ProgressPath)));
        Assert.AreEqual(0, process.CallCount);
    }
}
