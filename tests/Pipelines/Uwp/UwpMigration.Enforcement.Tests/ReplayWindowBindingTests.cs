// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinAppMigration.Cli.Pipelines.Uwp;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    [DataRow("inspect")]
    [DataRow("screenshot")]
    [DataRow("invoke")]
    [DataRow("click")]
    public async Task ReplayWindow_StateBoundCapturesRequireExplicitWindowBeforeConsumingAnOperation(string command)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var process = new FakeProcessRunner();
        var owner = new TestWindowOwnerResolver(4242);
        var runner = new UwpEnforcementRunner(process, windowOwnerResolver: owner);

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(fixture.Root, "winapp", [command, "--json"], [], "state-1"));

        StringAssert.Contains(error.Message, "-w/--window");
        Assert.AreEqual(0, process.CallCount);
        Assert.AreEqual(0, owner.Calls);
        Assert.HasCount(2, (await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath)).Operations);
    }

    [TestMethod]
    [DataRow("source", "inspect")]
    [DataRow("source", "screenshot")]
    [DataRow("source", "invoke")]
    [DataRow("other-process", "inspect")]
    [DataRow("other-process", "click")]
    [DataRow("nonexistent", "inspect")]
    public async Task ReplayWindow_SourceAndOtherProcessWindowsCannotSupplyTargetEvidence(string kind, string command)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var process = new FakeProcessRunner(new EnforcementProcessResult(0, ClosureTree, ""));
        var owner = new TestWindowOwnerResolver(kind switch { "source" => 1111, "other-process" => 4343, _ => 0 }, 84);
        var runner = new UwpEnforcementRunner(process, windowOwnerResolver: owner);

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(fixture.Root, "winapp", [command, "-w", "84", "--json"], [], "state-1"));

        StringAssert.Contains(error.Message, "canonical replay process 4242");
        Assert.AreEqual(0, process.CallCount);
        Assert.AreEqual(1, owner.Calls);
        Assert.IsFalse(File.Exists(fixture.Path(EnforcementStore.UiProbePath)));
    }

    [TestMethod]
    [DataRow("-w", "42")]
    [DataRow("--window", "42")]
    [DataRow("--window=", "42")]
    [DataRow("-w=", "42")]
    [DataRow("--window:", "42")]
    [DataRow("-w:", "42")]
    [DataRow("--window=", "0x2a")]
    public async Task ReplayWindow_CaptureSealsTheExplicitHandleAndReplayOwner(string option, string value)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var owner = new TestWindowOwnerResolver(4242);
        var runner = new UwpEnforcementRunner(new FakeProcessRunner(new EnforcementProcessResult(0, ClosureTree, "")),
            windowOwnerResolver: owner);
        string[] arguments = option.EndsWith('=') || option.EndsWith(':') ? ["inspect", option + value, "--json"] :
            ["inspect", option, value, "--json"];

        var probe = await runner.InvokeUiAsync(fixture.Root, "winapp", arguments, [], "state-1");
        var recorded = await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);

        Assert.AreEqual(TestWindowOwnerResolver.WindowHandle, probe.WindowHandle);
        Assert.AreEqual(4242, probe.WindowProcessId);
        Assert.AreEqual(2, owner.Calls);
        Assert.IsTrue(StateClosureSeal.Verify(probe));
        Assert.AreEqual("passed", recorded.Gates.Single(gate => gate.Id == "state-closure").Status);
    }

    [TestMethod]
    [DataRow("window")]
    [DataRow("process")]
    public async Task ReplayWindow_ReceiptSealCoversWindowAndProcess(string field)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var probe = await CaptureClosureProbeAsync(fixture);
        Assert.IsTrue(StateClosureSeal.Verify(probe));
        if (field == "window") probe.WindowHandle = 84;
        else probe.WindowProcessId = 1111;

        Assert.IsFalse(StateClosureSeal.Verify(probe));
    }

    [TestMethod]
    [DataRow("duplicate")]
    [DataRow("mixed-colon-duplicate")]
    [DataRow("terminator")]
    [DataRow("zero")]
    [DataRow("missing-value")]
    [DataRow("empty-colon")]
    public async Task ReplayWindow_AmbiguousOrInvalidWindowSelectorsFailClosed(string invalid)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var process = new FakeProcessRunner();
        var runner = new UwpEnforcementRunner(process, windowOwnerResolver: new TestWindowOwnerResolver(4242));
        string[] arguments = invalid switch
        {
            "duplicate" => ["inspect", "-w", "42", "--window", "84"],
            "mixed-colon-duplicate" => ["inspect", "--window:42", "-w=84"],
            "terminator" => ["inspect", "--", "-w", "42"],
            "zero" => ["inspect", "-w", "0"],
            "empty-colon" => ["inspect", "-w:"],
            _ => ["inspect", "-w"]
        };

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(fixture.Root, "winapp", arguments, [], "state-1"));

        Assert.AreEqual(0, process.CallCount);
    }

    [TestMethod]
    public async Task ReplayWindow_UnboundDiscoveryDoesNotRequireOrResolveAWindow()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var owner = new TestWindowOwnerResolver(0);
        var process = new FakeProcessRunner(new EnforcementProcessResult(0, """{"windows":[]}""", ""));
        var probe = await new UwpEnforcementRunner(process, windowOwnerResolver: owner)
            .InvokeUiAsync(fixture.Root, "winapp", ["list-windows", "--json"]);

        Assert.AreEqual(1, process.CallCount);
        Assert.AreEqual(0, owner.Calls);
        Assert.IsNull(probe.StateId);
        Assert.IsNull(probe.WindowHandle);
        Assert.IsNull(probe.WindowProcessId);
    }

    [TestMethod]
    public async Task ReplayWindow_CliRejectsMissingWindowForStateCapture()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var process = new FakeProcessRunner();
        var output = await InvokeWithCapturedOutputAsync(new UwpEnforceCommand(process, new TestWindowOwnerResolver(4242))
            .Parse(["capture", fixture.Root.FullName, "--state", "state-1", "--", "inspect", "--json"]));

        StringAssert.Contains(output, "-w/--window");
        Assert.AreEqual(0, process.CallCount);
    }

    [TestMethod]
    public async Task ReplayWindow_OwnershipChangeDuringCaptureDoesNotProduceAStateReceipt()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var process = new FakeProcessRunner(new EnforcementProcessResult(0, ClosureTree, ""));
        var runner = new UwpEnforcementRunner(process, windowOwnerResolver: new ChangingWindowOwnerResolver());

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(fixture.Root, "winapp", ["inspect", "-w", "42", "--json"], [], "state-1"));

        Assert.AreEqual(1, process.CallCount);
        Assert.IsFalse(File.Exists(fixture.Path(EnforcementStore.UiProbePath)));
    }

    [TestMethod]
    [DataRow("handle")]
    [DataRow("owner")]
    [DataRow("missing-handle")]
    [DataRow("missing-owner")]
    [DataRow("command-window")]
    [DataRow("command-no-window")]
    [DataRow("replay-process")]
    public async Task ReplayWindow_DerivationRevalidatesTheSealedCommandAndReplayProcessBinding(string invalid)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var probe = await CaptureClosureProbeAsync(fixture);
        switch (invalid)
        {
            case "handle": probe.WindowHandle = 84; break;
            case "owner": probe.WindowProcessId = 1111; break;
            case "missing-handle": probe.WindowHandle = null; break;
            case "missing-owner": probe.WindowProcessId = null; break;
            case "command-window": probe.Command[^1] = "84"; break;
            case "command-no-window": probe.Command.RemoveRange(probe.Command.Count - 2, 2); break;
            case "replay-process":
                var replay = await fixture.LoadAsync<ReplayAttestation>(EnforcementStore.ReplayAttestationPath);
                replay.ProcessId = 4343;
                StateClosureSeal.Sign(replay);
                await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.ReplayAttestationPath, replay);
                await RefreshCanonicalOperationAsync(fixture, replay.OperationId!, EnforcementStore.ReplayAttestationPath);
                break;
        }
        probe.CommandSignature = DiagnosticGrouper.CreateSignature(
            "ui-command", [], string.Join('\u001f', probe.Command.Skip(1)));
        StateClosureSeal.Sign(probe);
        await PersistTrackedProbeAsync(fixture, probe);

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));

        StringAssert.Contains(error.Message, invalid == "command-no-window" ? "-w/--window" : "window/process binding");
    }

    [TestMethod]
    public async Task Closure_PortableIntegrityDoesNotAuthorizeAnUnknownSigningKey()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var probe = await CaptureClosureProbeAsync(fixture);
        using var portableKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var localPublicKey = probe.SealPublicKey;
        SealPortable(probe, portableKey);
        Assert.AreNotEqual(localPublicKey, probe.SealPublicKey);
        await PersistTrackedProbeAsync(fixture, probe);
        Assert.IsTrue(StateClosureSeal.VerifyPortableIntegrity(probe));
        Assert.IsFalse(StateClosureSeal.Verify(probe));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));
    }

    [TestMethod]
    public async Task Closure_SameUserRelocationRetainsTrustWhileOriginalWindowIsLive()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Target.Root = ".";
        await fixture.SaveReportAsync(report);
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        await fixture.VerifyStateAsync();
        var complete = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("complete", complete.Disposition?.Kind);
        var relocated = TestDirectories.Create("closure-relocated-");
        try
        {
            foreach (var source in Directory.EnumerateFiles(fixture.Root.FullName, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(relocated.FullName, Path.GetRelativePath(fixture.Root.FullName, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }

            var validated = await UwpEnforcementRunner.AssessAsync(relocated, TestWindowOwnerResolver.Shared);

            Assert.AreEqual(complete.TargetFingerprint, validated.TargetFingerprint);
            Assert.AreEqual("passed", validated.Status);
            Assert.AreEqual("complete", validated.Disposition?.Kind);
        }
        finally { TestDirectories.Delete(relocated); }
    }

    [TestMethod]
    public void ReplayWindow_NativeResolverRejectsInvalidHandles()
    {
        Assert.IsFalse(WindowsWindowOwnerResolver.Instance.TryGetProcessId(0, out var owner));
        Assert.AreEqual(0, owner);
        Assert.IsFalse(WindowsWindowOwnerResolver.Instance.TryGetProcessId(-1, out _));
    }

    private static void SealPortable(UiDevelopmentProbe probe, ECDsa key)
    {
        probe.Seal = null;
        probe.SealPublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        probe.Seal = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(
            "ui-probe\n" + JsonSerializer.Serialize(probe, EnforcementStore.JsonOptions)), HashAlgorithmName.SHA256));
    }

    private static async Task PersistTrackedProbeAsync(RunnerFixture fixture, UiDevelopmentProbe probe)
    {
        await EnforcementStore.WriteAtomicAsync(fixture.Root, probe.ArtifactPath, probe);
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        var index = progress.Operations.FindIndex(operation => operation.ProbeId == probe.Id);
        progress.Operations[index] = progress.Operations[index] with
        {
            Signature = probe.CommandSignature,
            ProbeSha256 = StateClosureProtocol.FileHash(fixture.Root, probe.ArtifactPath)
        };
        StateClosureSeal.Sign(progress.Operations[index]);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, progress);
    }

    private sealed class ChangingWindowOwnerResolver : IWindowOwnerResolver
    {
        private int _calls;
        public bool TryGetProcessIdentity(int processId, out ProcessIdentity identity)
        {
            identity = new ProcessIdentity(processId, TestWindowOwnerResolver.ProcessStartedAt);
            return processId > 0;
        }

        public bool TryGetProcessId(long windowHandle, out int processId)
        {
            processId = ++_calls == 1 ? 4242 : 1111;
            return true;
        }
    }
}
