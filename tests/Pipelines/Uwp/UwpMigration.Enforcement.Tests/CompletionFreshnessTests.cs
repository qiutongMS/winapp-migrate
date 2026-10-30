// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class EnforcementEvaluatorTests
{
    [TestMethod]
    [DataRow("target-fingerprint")]
    [DataRow("build-fingerprint")]
    [DataRow("replay-fingerprint")]
    [DataRow("replay-build-link")]
    [DataRow("build-failed")]
    [DataRow("replay-failed")]
    [DataRow("build-output")]
    [DataRow("replay-output")]
    [DataRow("build-timestamps")]
    [DataRow("replay-timestamps")]
    [DataRow("future-build")]
    [DataRow("future-replay")]
    [DataRow("replay-process")]
    [DataRow("producer")]
    [DataRow("analyzer")]
    public void Completion_RetainingTerminalSuccessRequiresTheEntireCurrentCanonicalChain(string invalid)
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        var complete = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual("complete", complete.Disposition?.Kind);
        input = input with
        {
            Progress = complete.Workflow, PersistedDisposition = complete.Disposition,
            GeneratedAt = input.GeneratedAt.AddMinutes(1)
        };
        switch (invalid)
        {
            case "target-fingerprint": input = input with { TargetFingerprint = "changed" }; break;
            case "build-fingerprint": input.Build!.TargetFingerprint = "old"; break;
            case "replay-fingerprint": input.Replay!.TargetFingerprint = "old"; break;
            case "replay-build-link": input.Replay!.BuildAttestationId = "different-build"; break;
            case "build-failed": input.Build!.Success = false; break;
            case "replay-failed": input.Replay!.Success = false; break;
            case "build-output": File.Delete(fixture.Path(input.Build!.StandardOutputPath)); break;
            case "replay-output": File.Delete(fixture.Path(input.Replay!.StandardOutputPath)); break;
            case "build-timestamps": input.Build!.StartedAt = input.Build.CompletedAt.AddSeconds(1); break;
            case "replay-timestamps": input.Replay!.StartedAt = input.Build!.CompletedAt.AddSeconds(-1); break;
            case "future-build": input.Build!.CompletedAt = input.GeneratedAt.AddMinutes(1); break;
            case "future-replay": input.Replay!.CompletedAt = input.GeneratedAt.AddMinutes(1); break;
            case "replay-process": input.Replay!.ProcessId = null; break;
            case "producer": input.Replay!.Producer = "agent"; break;
            case "analyzer": input = input with { AnalyzerReferencePresent = false }; break;
        }

        var reopened = EnforcementEvaluator.Evaluate(input);

        Assert.AreNotEqual("passed", reopened.Status);
        Assert.AreNotEqual("complete", reopened.Disposition?.Kind);
        Assert.IsNotEmpty(reopened.Gates);
    }
}

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    public async Task Completion_ChangedTargetCannotStayPassedThroughPersistedOldReplay()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        await fixture.VerifyStateAsync();
        var complete = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("complete", complete.Disposition?.Kind);
        await File.AppendAllTextAsync(fixture.ProjectPath, Environment.NewLine);

        var reopened = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreNotEqual("complete", reopened.Disposition?.Kind);
        Assert.AreEqual("blocked", reopened.Status);
        Assert.AreEqual("blocked", reopened.Gates.Single(gate => gate.Id == "fresh-build").Status);
        Assert.AreEqual("blocked", reopened.Gates.Single(gate => gate.Id == "canonical-replay").Status);
        var persisted = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        Assert.AreEqual(reopened.TargetFingerprint, persisted.TargetFingerprint);
        Assert.AreNotEqual("passed", persisted.Status);
    }
}
