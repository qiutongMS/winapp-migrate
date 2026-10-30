// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text;
using System.Text.Json;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    [DataRow("-o", false)]
    [DataRow("--output", false)]
    [DataRow("--output=", false)]
    [DataRow("-o:", false)]
    [DataRow("-o=", false)]
    [DataRow("--output:", false)]
    [DataRow("-o:", true)]
    [DataRow("--output=", true)]
    public async Task ScreenshotCompatibility_AllOutputFormsProduceBoundSnapshots(string form, bool absolute)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var inspect = await CaptureClosureProbeAsync(fixture);
        var output = Path.Combine(".migration-evidence", "screenshots with spaces", "capture.png");
        var requested = absolute ? fixture.Path(output) : output;
        var arguments = ScreenshotArguments(form, requested);
        var process = new ScreenshotCliProcessRunner();
        var runner = new UwpEnforcementRunner(process, windowOwnerResolver: new TestWindowOwnerResolver(4242));

        var screenshot = await runner.InvokeUiAsync(fixture.Root, "winapp", arguments, [], "state-1");

        Assert.IsNotNull(screenshot.ScreenshotPath);
        Assert.IsNotNull(screenshot.ScreenshotSha256);
        Assert.IsNull(screenshot.WorkingDirectory, "Explicit output paths retain the target working directory.");
        Assert.AreEqual(fixture.Root.FullName, process.WorkingDirectories.Single());
        Assert.AreEqual(StateClosureProtocol.FileHash(fixture.Root, screenshot.ScreenshotPath), screenshot.ScreenshotSha256);
        CollectionAssert.AreEqual(new[] { "ui" }.Concat(arguments).ToArray(), process.Calls.Single());
        File.Delete(fixture.Path(output));
        var recorded = await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [inspect.ArtifactPath, screenshot.ArtifactPath]);
        Assert.AreEqual("passed", recorded.Gates.Single(gate => gate.Id == "state-closure").Status);
        var finalized = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("passed", finalized.Status);
        CollectionAssert.AreEqual(PngTestData.Create(),
            await File.ReadAllBytesAsync(fixture.Path(".migration-evidence/target/state-1.png")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScreenshotCompatibility_StateBoundDefaultUsesWinAppDefaultInsideProbeStorage(bool existingRootImage)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var original = PngTestData.Create(uniform: true);
        if (existingRootImage)
            await File.WriteAllBytesAsync(fixture.Path("screenshot.png"), original);
        var inspect = await CaptureClosureProbeAsync(fixture);
        var process = new ScreenshotCliProcessRunner();
        var runner = new UwpEnforcementRunner(process, windowOwnerResolver: new TestWindowOwnerResolver(4242));

        var screenshot = await runner.InvokeUiAsync(
            fixture.Root, "winapp", ["screenshot", "-w", "42", "--json"], [], "state-1");

        CollectionAssert.AreEqual(new[] { "ui", "screenshot", "-w", "42", "--json" }, process.Calls.Single());
        Assert.AreEqual(Path.GetDirectoryName(screenshot.ArtifactPath)?.Replace('\\', '/'), screenshot.WorkingDirectory);
        Assert.AreEqual(fixture.Path(screenshot.WorkingDirectory!), process.WorkingDirectories.Single());
        Assert.AreEqual($"{screenshot.WorkingDirectory}/screenshot.png", screenshot.ScreenshotPath);
        Assert.AreEqual(StateClosureProtocol.FileHash(fixture.Root, screenshot.ScreenshotPath!), screenshot.ScreenshotSha256);
        Assert.AreEqual(inspect.TargetFingerprint, await TargetFingerprint.ComputeAsync(fixture.Root));
        if (existingRootImage)
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(fixture.Path("screenshot.png")));
        else
            Assert.IsFalse(File.Exists(fixture.Path("screenshot.png")));
        var recorded = await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [inspect.ArtifactPath, screenshot.ArtifactPath]);
        Assert.AreEqual("passed", recorded.Gates.Single(gate => gate.Id == "state-closure").Status);
        Assert.AreEqual("passed", (await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared)).Status);
    }

    [TestMethod]
    public async Task ScreenshotCompatibility_UnboundDefaultPreservesTargetWorkingDirectory()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var process = new ScreenshotCliProcessRunner();

        var screenshot = await new UwpEnforcementRunner(process).InvokeUiAsync(
            fixture.Root, "winapp", ["screenshot", "-w", "42", "--json"]);

        Assert.IsNull(screenshot.WorkingDirectory);
        Assert.AreEqual(fixture.Root.FullName, process.WorkingDirectories.Single());
        Assert.IsTrue(File.Exists(fixture.Path("screenshot.png")));
        Assert.IsNotNull(screenshot.ScreenshotPath);
        Assert.AreEqual(StateClosureProtocol.FileHash(fixture.Root, screenshot.ScreenshotPath), screenshot.ScreenshotSha256);
    }

    [TestMethod]
    [DataRow("-o")]
    [DataRow("--output")]
    [DataRow("--output=")]
    [DataRow("-o:")]
    [DataRow("default")]
    public async Task ScreenshotCompatibility_ExitZeroWithoutActualOutputIsRejected(string form)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var inspect = await CaptureClosureProbeAsync(fixture);
        var process = new ScreenshotCliProcessRunner(ScreenshotWriteBehavior.NoWrite);
        var runner = new UwpEnforcementRunner(process, windowOwnerResolver: new TestWindowOwnerResolver(4242));
        string[] arguments = form == "default" ? ["screenshot", "-w", "42", "--json"] :
            ScreenshotArguments(form, ".migration-evidence/not-written.png");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(fixture.Root, "winapp", arguments, [], "state-1"));

        Assert.AreEqual(inspect.Id, (await fixture.LoadAsync<UiDevelopmentProbe>(EnforcementStore.UiProbePath)).Id);
    }

    [TestMethod]
    [DataRow("stale")]
    [DataRow("invalid-png")]
    public async Task ScreenshotCompatibility_StaleOrInvalidSuccessfulOutputIsRejected(string invalid)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var inspect = await CaptureClosureProbeAsync(fixture);
        const string output = ".migration-evidence/not-fresh.png";
        if (invalid == "stale")
            await File.WriteAllBytesAsync(fixture.Path(output), PngTestData.Create());
        var process = new ScreenshotCliProcessRunner(invalid == "stale" ? ScreenshotWriteBehavior.NoWrite : ScreenshotWriteBehavior.InvalidPng);
        var runner = new UwpEnforcementRunner(process, windowOwnerResolver: new TestWindowOwnerResolver(4242));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(fixture.Root, "winapp", ScreenshotArguments("--output=", output), [], "state-1"));

        Assert.AreEqual(inspect.Id, (await fixture.LoadAsync<UiDevelopmentProbe>(EnforcementStore.UiProbePath)).Id);
    }

    [TestMethod]
    [DataRow("missing-value")]
    [DataRow("empty-attached")]
    [DataRow("mixed-duplicate")]
    [DataRow("outside-root")]
    public async Task ScreenshotCompatibility_InvalidOrAmbiguousOutputCannotFallBackSilently(string invalid)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await CaptureClosureProbeAsync(fixture);
        var process = new ScreenshotCliProcessRunner();
        var runner = new UwpEnforcementRunner(process, windowOwnerResolver: new TestWindowOwnerResolver(4242));
        string[] arguments = invalid switch
        {
            "missing-value" => ["screenshot", "-w", "42", "-o"],
            "empty-attached" => ["screenshot", "-w", "42", "--output="],
            "mixed-duplicate" => ["screenshot", "-w", "42", "-o:one.png", "--output=two.png"],
            _ => ["screenshot", "-w", "42", "--output=../outside.png"]
        };

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(fixture.Root, "winapp", arguments, [], "state-1"));

        Assert.AreEqual(0, process.Calls.Count);
    }

    [TestMethod]
    [DataRow("missing-png")]
    [DataRow("missing-hash")]
    [DataRow("missing-default-working-directory")]
    public async Task ScreenshotCompatibility_DerivationRejectsDroppedSnapshotOrDefaultBinding(string invalid)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var inspect = await CaptureClosureProbeAsync(fixture);
        var runner = new UwpEnforcementRunner(new ScreenshotCliProcessRunner(), windowOwnerResolver: new TestWindowOwnerResolver(4242));
        var screenshot = await runner.InvokeUiAsync(
            fixture.Root, "winapp", ["screenshot", "-w", "42", "--json"], [], "state-1");
        if (invalid == "missing-png") screenshot.ScreenshotPath = null;
        if (invalid == "missing-hash") screenshot.ScreenshotSha256 = null;
        if (invalid == "missing-default-working-directory") screenshot.WorkingDirectory = null;
        StateClosureSeal.Sign(screenshot);
        await PersistTrackedProbeAsync(fixture, screenshot);

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [inspect.ArtifactPath, screenshot.ArtifactPath]));

        StringAssert.Contains(error.Message, "snapshotted PNG/hash");
    }

    [TestMethod]
    public async Task ScreenshotCompatibility_DefaultOutputDoesNotBypassImmediateRepeatGuard()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await CaptureClosureProbeAsync(fixture);
        const string intent = ".migration-evidence/default-screenshot-intent.json";
        await File.WriteAllTextAsync(fixture.Path(intent), """{"stateId":"state-1","intent":"Observe the same planned state."}""");
        var process = new ScreenshotCliProcessRunner();
        var runner = new UwpEnforcementRunner(process, windowOwnerResolver: new TestWindowOwnerResolver(4242));
        await runner.InvokeUiAsync(fixture.Root, "winapp", ["screenshot", "-w", "42", "--json"], [intent], "state-1");

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(fixture.Root, "winapp", ["screenshot", "-w", "42", "--json"], [intent], "state-1"));

        StringAssert.Contains(error.Message, "same state intent and command signature");
        Assert.AreEqual(1, process.Calls.Count);
    }

    [TestMethod]
    [DataRow("--window")]
    [DataRow("-w")]
    [DataRow("--window=")]
    [DataRow("-w=")]
    [DataRow("--window:")]
    [DataRow("-w:")]
    public async Task ScreenshotCompatibility_SystemCommandLineWindowFormsStillCheckOwnership(string form)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await CaptureClosureProbeAsync(fixture);
        var process = new ScreenshotCliProcessRunner();
        var owner = new TestWindowOwnerResolver(1111, 84);
        var runner = new UwpEnforcementRunner(process, windowOwnerResolver: owner);
        string[] arguments = form.EndsWith('=') || form.EndsWith(':') ? ["screenshot", form + "84", "--json"] :
            ["screenshot", form, "84", "--json"];

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(fixture.Root, "winapp", arguments, [], "state-1"));

        StringAssert.Contains(error.Message, "canonical replay process 4242");
        Assert.AreEqual(1, owner.Calls);
        Assert.AreEqual(0, process.Calls.Count);
    }

    private static string[] ScreenshotArguments(string form, string output) =>
        form.EndsWith('=') || form.EndsWith(':')
            ? ["screenshot", "-w", "42", "--json", form + output]
            : ["screenshot", "-w", "42", "--json", form, output];

    private enum ScreenshotWriteBehavior { Valid, NoWrite, InvalidPng }

    private sealed class ScreenshotCliProcessRunner(ScreenshotWriteBehavior behavior = ScreenshotWriteBehavior.Valid, byte[]? png = null) : IEnforcementProcessRunner
    {
        internal List<string[]> Calls { get; } = [];
        internal List<string> WorkingDirectories { get; } = [];

        public async Task<EnforcementProcessResult> RunAsync(string executable, DirectoryInfo workingDirectory,
            IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add([.. arguments]);
            WorkingDirectories.Add(workingDirectory.FullName);
            var command = new Command("screenshot");
            var output = new Option<string?>("--output");
            output.Aliases.Add("-o");
            var window = new Option<long?>("--window");
            window.Aliases.Add("-w");
            command.Options.Add(output);
            command.Options.Add(window);
            command.Options.Add(new Option<bool>("--json"));
            var parsed = command.Parse(arguments.Skip(2).ToArray());
            Assert.IsEmpty(parsed.Errors, "Use the same System.CommandLine 2.0.5 option forms as pinned WinApp.");
            // UiScreenshotCommand.PublishAsync uses screenshot.png in its process working directory.
            var path = Path.GetFullPath(Path.Combine(workingDirectory.FullName, parsed.GetValue(output) ?? "screenshot.png"));
            if (behavior != ScreenshotWriteBehavior.NoWrite)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, behavior == ScreenshotWriteBehavior.Valid
                    ? png ?? PngTestData.Create() : Encoding.UTF8.GetBytes("not a PNG"), cancellationToken);
            }
            return new EnforcementProcessResult(0,
                JsonSerializer.Serialize(new { filePath = path, width = 64, height = 64, processId = 4242, hwnd = 42 }), "");
        }
    }
}
