// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinAppMigration.Cli.Pipelines.Uwp;
using UwpMigration.Contracts;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

[TestClass]
public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    public async Task Run_MissingAnalyzerDoesNotInvokeWinApp()
    {
        using var fixture = await RunnerFixture.CreateAsync(analyzer: false);
        var process = new FakeProcessRunner();
        var runner = new UwpEnforcementRunner(process);

        var snapshot = await runner.RunAsync(
            fixture.Root,
            new UwpEnforcementOptions());

        Assert.AreEqual("analyzer", snapshot.Frontier?.Kind);
        Assert.AreEqual(0, process.CallCount);
    }

    [TestMethod]
    public async Task Cli_RunDispatchesBuildAndReplay()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            new EnforcementProcessResult(0, """{"AUMID":"Contoso.App"}""", ""),
            new EnforcementProcessResult(0, """{"ProcessId":4242}""", ""));
        var command = new UwpEnforceCommand(process);
        var parseResult = command.Parse(
            ["run", fixture.Root.FullName]);

        var exitCode = await parseResult.InvokeAsync(
            parseResult.InvocationConfiguration,
            CancellationToken.None);

        Assert.AreEqual(2, exitCode);
        Assert.AreEqual(2, process.CallCount);
        Assert.IsTrue(File.Exists(
            fixture.Path(EnforcementStore.StatePath)));
    }

    [TestMethod]
    public async Task Run_InvalidBuildJsonRecordsFailure()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            new EnforcementProcessResult(0, "not json", ""));
        var runner = new UwpEnforcementRunner(process);

        var snapshot = await runner.RunAsync(
            fixture.Root,
            new UwpEnforcementOptions());

        Assert.AreEqual("change-required", snapshot.Frontier?.Kind);
        Assert.AreEqual(1, process.CallCount);
        var attestation = await fixture.LoadAsync<BuildAttestation>(
            EnforcementStore.BuildAttestationPath);
        Assert.IsFalse(attestation.StructuredOutput);
        Assert.IsFalse(attestation.Success);
    }

    [TestMethod]
    public async Task Run_EmptyBuildObjectDoesNotAttestPackagedSuccess()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            new EnforcementProcessResult(0, "{}", ""));
        var runner = new UwpEnforcementRunner(process);

        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());

        var attestation = await fixture.LoadAsync<BuildAttestation>(
            EnforcementStore.BuildAttestationPath);
        Assert.IsFalse(attestation.Success);
    }

    [TestMethod]
    public async Task Run_GroupsBuildDiagnostics()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        const string errors =
            "a.cs(1,2): error CS1002: ; expected\n" +
            "b.cs(5,6): error CS1002: ; expected\n";
        var process = new FakeProcessRunner(
            new EnforcementProcessResult(1, """{"Error":"Build failed"}""", errors));
        var runner = new UwpEnforcementRunner(process);

        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());

        var attestation = await fixture.LoadAsync<BuildAttestation>(
            EnforcementStore.BuildAttestationPath);
        Assert.HasCount(1, attestation.Diagnostics);
        Assert.AreEqual(2, attestation.Diagnostics[0].Count);
        Assert.AreEqual("CS1002", attestation.Diagnostics[0].Code);
    }

    [TestMethod]
    public async Task Run_SuccessfulBuildAndDetachedLaunchAreAttested()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            new EnforcementProcessResult(0, """{"AUMID":"Contoso.App"}""", ""),
            new EnforcementProcessResult(
                0,
                """{"AUMID":"Contoso.App","ProcessId":4242}""",
                ""));
        var runner = new UwpEnforcementRunner(process);

        var snapshot = await runner.RunAsync(
            fixture.Root,
            new UwpEnforcementOptions());

        Assert.AreEqual(2, process.CallCount);
        Assert.AreEqual("state", snapshot.Frontier?.Kind);
        CollectionAssert.AreEqual(
            new[] { "run", fixture.ProjectPath, "--no-launch", "--json" },
            process.Calls[0]);
        CollectionAssert.AreEqual(
            new[] { "run", fixture.ProjectPath, "--no-build", "--detach", "--json" },
            process.Calls[1]);
        CollectionAssert.AreEqual(
            new[] { WorkflowOperation.Build, WorkflowOperation.Replay },
            snapshot.Workflow!.Operations.Select(operation => operation.Kind).ToArray());
        var build = await fixture.LoadAsync<BuildAttestation>(
            EnforcementStore.BuildAttestationPath);
        CollectionAssert.AreEqual(
            new[] { "winapp" }.Concat(process.Calls[0]).ToArray(),
            build.Command);
        var replay = await fixture.LoadAsync<ReplayAttestation>(
            EnforcementStore.ReplayAttestationPath);
        Assert.IsTrue(replay.Success);
        Assert.AreEqual(4242, replay.ProcessId);
        Assert.AreEqual("Contoso.App", replay.Aumid);
        CollectionAssert.AreEqual(
            new[] { "winapp" }.Concat(process.Calls[1]).ToArray(),
            replay.Command);
    }

    [TestMethod]
    public async Task Assess_RawTargetOutputCannotReplaceGatewayAttestation()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.Path(".migration-evidence/raw-winapp-output.json"),
            """{"AUMID":"Contoso.App","ProcessId":4242}""");

        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root);

        Assert.AreEqual("build", snapshot.Frontier?.Kind);
        Assert.AreEqual(
            "blocked",
            snapshot.Gates.Single(gate => gate.Id == "fresh-build").Status);
        Assert.IsEmpty(snapshot.Workflow!.Operations);
        Assert.IsFalse(File.Exists(fixture.Path(EnforcementStore.BuildAttestationPath)));
        Assert.IsFalse(File.Exists(fixture.Path(EnforcementStore.ReplayAttestationPath)));
    }

    [TestMethod]
    public async Task Assess_SourceBaselineEvidenceDoesNotRequireTargetOperation()
    {
        using var fixture = await RunnerFixture.CreateAsync();

        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root);

        Assert.AreEqual("passed",
            snapshot.Gates.Single(gate => gate.Id == "source-oracle").Status);
        Assert.AreEqual(WorkflowStage.MechanicalStabilization, snapshot.Stage);
        Assert.IsEmpty(snapshot.Workflow!.Operations);
    }

    [TestMethod]
    public async Task Run_ForceReplayReusesBuildParameters()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            new EnforcementProcessResult(0, """{"AUMID":"Contoso.App"}""", ""),
            new EnforcementProcessResult(0, """{"ProcessId":4242}""", ""),
            new EnforcementProcessResult(0, """{"ProcessId":4343}""", ""));
        var runner = new UwpEnforcementRunner(process);
        var options = new UwpEnforcementOptions
        {
            Configuration = "Release",
            Architecture = "x64",
            Runtime = "win-x64",
            Framework = "net10.0-windows10.0.19041.0",
            Properties = ["Feature=Enabled"]
        };
        await runner.RunAsync(fixture.Root, options);
        var evidence = ".migration-evidence/new-discriminating-observation.txt";
        await File.WriteAllTextAsync(fixture.Path(evidence), "A new navigation observation requires another controlled launch.");

        await runner.RunAsync(
            fixture.Root,
            new UwpEnforcementOptions
            {
                Configuration = options.Configuration,
                Architecture = options.Architecture,
                Runtime = options.Runtime,
                Framework = options.Framework,
                Properties = options.Properties,
                DiscriminatingEvidence = [evidence],
                ForceReplay = true
            });

        Assert.AreEqual(3, process.CallCount);
        CollectionAssert.Contains(process.Calls[0], "--no-launch");
        CollectionAssert.Contains(process.Calls[1], "--no-build");
        CollectionAssert.Contains(process.Calls[2], "--no-build");
        foreach (var arguments in process.Calls)
        {
            CollectionAssert.Contains(arguments, "Release");
            CollectionAssert.Contains(arguments, "x64");
            CollectionAssert.Contains(arguments, "win-x64");
            CollectionAssert.Contains(
                arguments,
                "net10.0-windows10.0.19041.0");
            CollectionAssert.Contains(arguments, "Feature=Enabled");
        }
        var replay = await fixture.LoadAsync<ReplayAttestation>(
            EnforcementStore.ReplayAttestationPath);
        Assert.AreEqual(4343, replay.ProcessId);
    }

    [TestMethod]
    public async Task Run_MissingProcessIdRecordsRuntimeFailure()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            new EnforcementProcessResult(0, """{"AUMID":"Contoso.App"}""", ""),
            new EnforcementProcessResult(0, """{"AUMID":"Contoso.App"}""", ""));
        var runner = new UwpEnforcementRunner(process);

        var snapshot = await runner.RunAsync(
            fixture.Root,
            new UwpEnforcementOptions());

        Assert.AreEqual("diagnosis-required", snapshot.Frontier?.Kind);
        var replay = await fixture.LoadAsync<ReplayAttestation>(
            EnforcementStore.ReplayAttestationPath);
        Assert.IsFalse(replay.Success);
        var attempts = await fixture.LoadAsync<AttemptLedger>(
            EnforcementStore.AttemptLedgerPath);
        Assert.HasCount(1, attempts.Attempts);
        Assert.AreEqual("runtime", attempts.Attempts[0].Kind);
    }

    [TestMethod]
    public async Task Run_ForceReplayDoesNotBypassDiagnosisFrontier()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            new EnforcementProcessResult(0, """{"AUMID":"Contoso.App"}""", ""),
            new EnforcementProcessResult(0, """{"AUMID":"Contoso.App"}""", ""),
            new EnforcementProcessResult(0, """{"ProcessId":4343}""", ""));
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());

        var snapshot = await runner.RunAsync(
            fixture.Root,
            new UwpEnforcementOptions { ForceReplay = true });

        Assert.AreEqual("diagnosis-required", snapshot.Frontier?.Kind);
        Assert.AreEqual(2, process.CallCount);
    }

    [TestMethod]
    public async Task Diagnose_PersistsDebugOutputAttestation()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            new EnforcementProcessResult(0, """{"AUMID":"Contoso.App"}""", ""),
            new EnforcementProcessResult(0, """{"ProcessId":4242}""", ""),
            new EnforcementProcessResult(
                unchecked((int)0xC000027B),
                "debug output",
                "stowed exception"));
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());

        var attestation = await runner.DiagnoseAsync(
            fixture.Root,
            new UwpEnforcementOptions());

        Assert.AreEqual(unchecked((int)0xC000027B), attestation.ExitCode);
        CollectionAssert.AreEqual(
            new[]
            {
                "run",
                fixture.ProjectPath,
                "--no-build",
                "--debug-output"
            },
            process.Calls[2]);
        Assert.AreEqual(
            "debug output",
            await File.ReadAllTextAsync(fixture.Path(
                attestation.StandardOutputPath)));
        Assert.AreEqual(
            "stowed exception",
            await File.ReadAllTextAsync(fixture.Path(
                attestation.StandardErrorPath)));
        Assert.AreEqual(
            MigrationArtifactClass.DevelopmentProbe,
            attestation.ArtifactClass);
        Assert.AreEqual(MigrationArtifactClass.Producer, attestation.Producer);
        Assert.IsTrue(File.Exists(fixture.Path(
            EnforcementStore.DiagnosticAttestationPath)));
    }

    [TestMethod]
    public async Task RecordDiagnosis_RequiresFreshEvidenceAndClearsFrontier()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        Directory.CreateDirectory(fixture.Path(
            EnforcementStore.EnforcementDirectory));
        var failureEvidence =
            ".migration-evidence/enforcement/runtime-failure.log";
        await File.WriteAllTextAsync(
            fixture.Path(failureEvidence),
            "runtime failure");
        var failure = await UwpEnforcementRunner.RecordFailureAsync(
            fixture.Root,
            "runtime",
            "0x800f1000",
            "state-1",
            [failureEvidence]);
        Assert.AreEqual("diagnosis-required", failure.Frontier?.Kind);
        var diagnosisEvidence =
            ".migration-evidence/enforcement/source-target-diff.txt";
        await File.WriteAllTextAsync(
            fixture.Path(diagnosisEvidence),
            "source retained a selection guard; target removed it");

        var snapshot = await UwpEnforcementRunner.RecordDiagnosisAsync(
            fixture.Root,
            "0x800f1000",
            "state-1",
            ["OuterNavPage.xaml.cs:100"],
            ["OuterNavPage.xaml.cs:90"],
            "Selection synchronization must suppress reentrant navigation.",
            "The removed guard causes the native invalid operation.",
            "Restore only the guard and replay the failing category action.",
            [diagnosisEvidence]);

        Assert.AreEqual("change-required", snapshot.Frontier?.Kind);
        var attempts = await fixture.LoadAsync<AttemptLedger>(
            EnforcementStore.AttemptLedgerPath);
        Assert.IsNotNull(attempts.Attempts[0].Diagnosis);
    }

    [TestMethod]
    public async Task RecordDiagnosis_RejectsEvidencePredatingFailure()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        Directory.CreateDirectory(fixture.Path(
            EnforcementStore.EnforcementDirectory));
        var evidence = ".migration-evidence/enforcement/failure.log";
        await File.WriteAllTextAsync(fixture.Path(evidence), "failure");
        await UwpEnforcementRunner.RecordFailureAsync(
            fixture.Root,
            "runtime",
            "crash",
            "state-1",
            [evidence]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordDiagnosisAsync(
                fixture.Root,
                "crash",
                "state-1",
                ["source.cs:1"],
                ["target.cs:1"],
                "Invariant",
                "Hypothesis",
                "Test",
                [evidence]));
    }

    [TestMethod]
    public async Task Cli_UiForwardsArgumentsAndExitCode()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            new EnforcementProcessResult(0, """{"AUMID":"Contoso.App"}""", ""),
            new EnforcementProcessResult(0, """{"ProcessId":4242}""", ""),
            new EnforcementProcessResult(7, "ui output", "ui error"));
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        var command = new UwpEnforceCommand(process);
        var parseResult = command.Parse(
            ["ui", fixture.Root.FullName, "--", "list", "--json"]);

        using var writer = new StringWriter();
        var original = Console.Out;
        int exitCode;
        try
        {
            Console.SetOut(writer);
            exitCode = await parseResult.InvokeAsync(
                parseResult.InvocationConfiguration,
                CancellationToken.None);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.AreEqual(7, exitCode);
        var receipt = JsonNode.Parse(writer.ToString())!.AsObject();
        Assert.AreEqual(MigrationArtifactClass.DevelopmentProbe,
            receipt["artifactClass"]!.GetValue<string>());
        Assert.IsNull(receipt["output"]);
        Assert.IsFalse(writer.ToString().Contains(
            "ui output",
            StringComparison.Ordinal));
        CollectionAssert.AreEqual(
            new[] { "ui", "list", "--json" },
            process.Calls[2]);
        var probe = await fixture.LoadAsync<UiDevelopmentProbe>(
            EnforcementStore.UiProbePath);
        Assert.AreEqual(MigrationArtifactClass.DevelopmentProbe, probe.ArtifactClass);
        Assert.AreEqual(MigrationArtifactClass.Producer, probe.Producer);
        Assert.AreEqual((await fixture.LoadAsync<EnforcementSnapshot>(
            EnforcementStore.StatePath)).TargetFingerprint, probe.TargetFingerprint);
        CollectionAssert.AreEqual(
            new[] { "winapp", "ui", "list", "--json" },
            probe.Command);
        Assert.IsTrue(File.Exists(fixture.Path(probe.ArtifactPath)));
        Assert.AreEqual(probe.Id,
            (await fixture.LoadAsync<UiDevelopmentProbe>(
                probe.ArtifactPath)).Id);
        Assert.AreEqual("ui output",
            await File.ReadAllTextAsync(fixture.Path(probe.StandardOutputPath)));
        Assert.AreEqual("ui error",
            await File.ReadAllTextAsync(fixture.Path(probe.StandardErrorPath)));
    }

    [TestMethod]
    public async Task Ui_StateIntentAuthorizesSequenceButRejectsImmediateExactRepeat()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            new EnforcementProcessResult(0, """{"AUMID":"Contoso.App"}""", ""),
            new EnforcementProcessResult(0, """{"ProcessId":4242}""", ""),
            new EnforcementProcessResult(0, """{"windows":[]}""", ""),
            new EnforcementProcessResult(0, """{"nodes":[]}""", ""),
            new EnforcementProcessResult(0, """{"ProcessId":4243}""", ""));
        var runner = new UwpEnforcementRunner(process);
        var initial = await runner.RunAsync(
            fixture.Root,
            new UwpEnforcementOptions());
        const string intent = ".migration-evidence/state-intent.json";
        await File.WriteAllTextAsync(
            fixture.Path(intent),
            JsonSerializer.Serialize(new
            {
                targetFingerprint = initial.TargetFingerprint,
                replayAttestationId = initial.Replay!.Id,
                stateId = "state-1",
                expectedOutcome = "State remains usable."
            }));

        var first = await runner.InvokeUiAsync(
            fixture.Root,
            "winapp",
            ["list-windows", "--json"],
            [intent]);
        var second = await runner.InvokeUiAsync(
            fixture.Root,
            "winapp",
            ["inspect", "--window", "Main", "--json"],
            [intent]);

        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreEqual(first.ReplayAttestationId, second.ReplayAttestationId);
        Assert.AreNotEqual(first.CommandSignature, second.CommandSignature);
        Assert.IsTrue(File.Exists(fixture.Path(first.ArtifactPath)));
        Assert.IsTrue(File.Exists(fixture.Path(second.ArtifactPath)));
        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(
                fixture.Root,
                "winapp",
                ["inspect", "--window", "Main", "--json"],
                [intent]));
        StringAssert.Contains(exception.Message, "same state intent and command signature");
        Assert.AreEqual(4, process.CallCount);
        var progress = await fixture.LoadAsync<WorkflowProgress>(
            EnforcementStore.ProgressPath);
        var uiOperations = progress.Operations
            .Where(operation => operation.Kind == WorkflowOperation.UiAction)
            .ToArray();
        Assert.AreEqual(2, uiOperations.Length);
        Assert.AreEqual(
            uiOperations[0].EvidenceFingerprint,
            uiOperations[1].EvidenceFingerprint);
        Assert.AreNotEqual(
            uiOperations[0].Signature,
            uiOperations[1].Signature);

        const string replayEvidence = ".migration-evidence/replay-evidence.json";
        await File.WriteAllTextAsync(
            fixture.Path(replayEvidence),
            """{"reason":"Revalidate the completed state sequence."}""");
        var replayed = await runner.RunAsync(
            fixture.Root,
            new UwpEnforcementOptions
            {
                ForceReplay = true,
                DiscriminatingEvidence = [replayEvidence]
            });
        Assert.AreNotEqual(
            first.ReplayAttestationId,
            replayed.Replay!.Id);
        var staleIntent = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(
                fixture.Root,
                "winapp",
                ["list-windows", "--json"],
                [intent]));
        StringAssert.Contains(staleIntent.Message, "must exist and postdate");
        Assert.AreEqual(5, process.CallCount);
    }

    [TestMethod]
    public async Task Cli_UiRawOutputEscapeHatchForwardsPersistedPayload()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            new EnforcementProcessResult(0, """{"AUMID":"Contoso.App"}""", ""),
            new EnforcementProcessResult(0, """{"ProcessId":4242}""", ""),
            new EnforcementProcessResult(3, "focused output", "focused error"));
        await new UwpEnforcementRunner(process).RunAsync(
            fixture.Root,
            new UwpEnforcementOptions());
        var parseResult = new UwpEnforceCommand(process).Parse(
            ["capture", fixture.Root.FullName, "--raw-output", "--", "list-windows"]);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        int exitCode;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            exitCode = await parseResult.InvokeAsync(
                parseResult.InvocationConfiguration,
                CancellationToken.None);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.AreEqual(3, exitCode);
        Assert.AreEqual("focused output", stdout.ToString());
        Assert.AreEqual("focused error", stderr.ToString());
    }

    [TestMethod]
    public async Task Cli_AssessWritesBoundedReceiptUnlessFullOutputIsRequested()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            new EnforcementProcessResult(0, """{"AUMID":"Contoso.App"}""", ""),
            new EnforcementProcessResult(0, """{"ProcessId":4242}""", ""));
        await new UwpEnforcementRunner(process).RunAsync(
            fixture.Root,
            new UwpEnforcementOptions());
        var command = new UwpEnforceCommand(process);

        var receipt = await InvokeWithCapturedOutputAsync(
            command.Parse(["assess", fixture.Root.FullName]));
        var compact = JsonNode.Parse(receipt)!.AsObject();
        Assert.IsNull(compact["workflow"]);
        Assert.IsTrue(receipt.Length < 20_000);
        Assert.AreEqual(
            EnforcementStore.StatePath,
            compact["artifacts"]!["state"]!.GetValue<string>());
        Assert.IsNotNull(compact["latest"]!["replay"]);

        var fullOutput = await InvokeWithCapturedOutputAsync(
            command.Parse(["assess", fixture.Root.FullName, "--full"]));
        var full = JsonNode.Parse(fullOutput)!.AsObject();
        Assert.IsNotNull(full["workflow"]);
        Assert.IsTrue(fullOutput.Length > receipt.Length);
    }

    [TestMethod]
    public async Task Ui_RequiresFreshCanonicalReplay()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            new EnforcementProcessResult(0, "unused", ""));
        var runner = new UwpEnforcementRunner(process);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(
                fixture.Root,
                "winapp",
                ["list-windows", "--json"]));

        Assert.AreEqual(0, process.CallCount);
    }

    [TestMethod]
    public async Task Run_RepeatedSameFailureIsBlockedWithoutInvocation()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var failure = new EnforcementProcessResult(
            1,
            """{"Error":"same build failure"}""",
            "error CS1002: ; expected");
        var process = new FakeProcessRunner(failure, failure, failure);
        var runner = new UwpEnforcementRunner(process);

        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        var snapshot = await runner.RunAsync(
            fixture.Root,
            new UwpEnforcementOptions());

        Assert.AreEqual(1, process.CallCount);
        Assert.AreEqual("change-required", snapshot.Frontier?.Kind);
        Assert.AreEqual(0, snapshot.Frontier!.AttemptsRemaining);
    }

    [TestMethod]
    public async Task Run_TargetChangeResetsFailureBudget()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var failure = new EnforcementProcessResult(
            1,
            """{"Error":"same build failure"}""",
            "error CS1002: ; expected");
        var process = new FakeProcessRunner(failure, failure, failure);
        var runner = new UwpEnforcementRunner(process);

        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        await File.AppendAllTextAsync(fixture.ProjectPath, Environment.NewLine);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());

        Assert.AreEqual(2, process.CallCount);
    }

    [TestMethod]
    public async Task SyncReport_ClosedFixturePasses()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            new EnforcementProcessResult(0, """{"AUMID":"Contoso.App"}""", ""),
            new EnforcementProcessResult(0, """{"ProcessId":4242}""", ""));
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        await fixture.VerifyStateAsync();

        var snapshot = await UwpEnforcementRunner.SynchronizeReportAsync(
            fixture.Root);

        Assert.AreEqual("passed", snapshot.Status);
        Assert.IsNull(snapshot.Frontier);
        var report = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(fixture.Path("migration-report.json")),
            MigrateJsonContext.Default.MigrationReport)!;
        Assert.AreEqual("state-1", report.Validation.SourceBaseline.States.Single().Id);
        Assert.AreEqual("captured", report.Validation.SourceBaseline.States.Single().Status);
        Assert.AreEqual("passed", report.Validation.TargetReplay.States.Single().Status);
        CollectionAssert.AreEqual(
            await File.ReadAllBytesAsync(fixture.Path(".migration-evidence/states/state-1/source.png")),
            await File.ReadAllBytesAsync(fixture.Path(".migration-evidence/source/state-1.png")));
        Assert.AreEqual(
            await File.ReadAllTextAsync(fixture.Path(".migration-evidence/states/state-1/source-ui.json")),
            await File.ReadAllTextAsync(fixture.Path(".migration-evidence/source/state-1-ui.json")));
        CollectionAssert.AreEqual(
            await File.ReadAllBytesAsync(fixture.Path(".migration-evidence/states/state-1/target.png")),
            await File.ReadAllBytesAsync(fixture.Path(".migration-evidence/target/state-1.png")));
        Assert.AreEqual(
            await File.ReadAllTextAsync(fixture.Path(".migration-evidence/states/state-1/stdout.log")),
            await File.ReadAllTextAsync(fixture.Path(".migration-evidence/target/state-1-ui.json")));
    }

    [TestMethod]
    public async Task SyncReport_VerifiesPendingRequiredTodoOnlyAfterMappedStatesClose()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(new MigrationTodo
        {
            Id = "UWMIG001",
            Category = "app-resources",
            Priority = "required",
            Summary = "Finish app resources.",
            Reason = "The app resource migration requires semantic verification."
        });
        await fixture.SaveReportAsync(report);
        var plan = await fixture.LoadAsync<StatePlanDocument>(
            ".migration-evidence/state-plan.json");
        plan.States[0].TodoIds = ["UWMIG001"];
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root, ".migration-evidence/state-plan.json", plan);
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(
            fixture.Root, new UwpEnforcementOptions());
        await fixture.WriteTargetEvidenceAsync();
        plan = await fixture.LoadAsync<StatePlanDocument>(
            ".migration-evidence/state-plan.json");
        plan.States[0].Target.Evidence =
        [
            ".migration-evidence/states/state-1/target.png",
            ".migration-evidence/states/state-1/stdout.log"
        ];
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root, ".migration-evidence/state-plan.json", plan);

        var open = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root);

        Assert.AreEqual("blocked", open.Status);
        Assert.AreEqual(
            "pending",
            (await fixture.LoadReportAsync()).Todos.Single().Status);

        await fixture.VerifyStateAsync();
        var closed = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root);

        Assert.AreEqual("passed", closed.Status);
        Assert.AreEqual(
            "verified",
            (await fixture.LoadReportAsync()).Todos.Single().Status);
    }

    [TestMethod]
    public async Task SyncReport_UnreferencedPendingRequiredTodoBlocksClosure()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(
            fixture.Root, new UwpEnforcementOptions());
        await fixture.VerifyStateAsync();
        var initiallyComplete =
            await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root);
        Assert.AreEqual("complete", initiallyComplete.Disposition?.Kind);

        var report = await fixture.LoadReportAsync();
        report.Todos.Add(new MigrationTodo
        {
            Id = "UWMIG002",
            Category = "activation",
            Priority = "required",
            Summary = "Finish activation behavior.",
            Reason = "The required activation work is not mapped to a state."
        });
        await fixture.SaveReportAsync(report);

        var reassessed = await UwpEnforcementRunner.AssessAsync(fixture.Root);
        Assert.AreEqual("blocked", reassessed.Status);
        Assert.AreEqual("report-closure", reassessed.Frontier?.Kind);

        var snapshot = await UwpEnforcementRunner.SynchronizeReportAsync(
            fixture.Root);

        Assert.AreEqual("blocked", snapshot.Status);
        Assert.AreNotEqual("complete", snapshot.Disposition?.Kind);
        var gate = snapshot.Gates.Single(item => item.Id == "report-closure");
        Assert.AreEqual("blocked", gate.Status);
        StringAssert.Contains(gate.Reason, "UWMIG002");
        StringAssert.Contains(gate.Reason, "not referenced by any state");
        Assert.AreEqual(
            "pending",
            (await fixture.LoadReportAsync()).Todos.Single().Status);
    }

    [TestMethod]
    public async Task SyncReport_PreservesExistingTerminalTodoStatuses()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos.AddRange(
        [
            new MigrationTodo
            {
                Id = "UWMIG001",
                Category = "app-resources",
                Priority = "required",
                Status = "accepted",
                Summary = "Accepted app resource decision.",
                Reason = "The decision was explicitly accepted."
            },
            new MigrationTodo
            {
                Id = "UWMIG003",
                Category = "code",
                Priority = "required",
                Status = "verified",
                Summary = "Verified code migration.",
                Reason = "The code behavior was already verified."
            }
        ]);
        await fixture.SaveReportAsync(report);
        var plan = await fixture.LoadAsync<StatePlanDocument>(
            ".migration-evidence/state-plan.json");
        plan.States[0].TodoIds = ["UWMIG001", "UWMIG003"];
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root, ".migration-evidence/state-plan.json", plan);
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(
            fixture.Root, new UwpEnforcementOptions());
        await fixture.VerifyStateAsync();

        var snapshot = await UwpEnforcementRunner.SynchronizeReportAsync(
            fixture.Root);

        Assert.AreEqual("passed", snapshot.Status);
        var statuses = (await fixture.LoadReportAsync()).Todos
            .ToDictionary(todo => todo.Id, todo => todo.Status);
        Assert.AreEqual("accepted", statuses["UWMIG001"]);
        Assert.AreEqual("verified", statuses["UWMIG003"]);
    }

    [TestMethod]
    [DataRow("source-screenshot")]
    [DataRow("target-tree")]
    public async Task SyncReport_RejectsMissingOrInvalidCanonicalEvidence(
        string invalidEvidence)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(
            fixture.Root, new UwpEnforcementOptions());
        await fixture.VerifyStateAsync();
        if (invalidEvidence == "source-screenshot")
        {
            File.Delete(fixture.Path(
                ".migration-evidence/states/state-1/source.png"));
        }
        else
        {
            await File.WriteAllTextAsync(
                fixture.Path(".migration-evidence/states/state-1/stdout.log"),
                """{"success":true,"message":"The action completed."}""");
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root));
    }

    [TestMethod]
    public async Task SyncReport_RejectsEvidencePathOutsideTargetRoot()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(
            fixture.Root, new UwpEnforcementOptions());
        await fixture.VerifyStateAsync();
        var plan = await fixture.LoadAsync<StatePlanDocument>(
            ".migration-evidence/state-plan.json");
        plan.States[0].Source.Evidence[0] = "../outside.png";
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root, ".migration-evidence/state-plan.json", plan);

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root));

        StringAssert.Contains(exception.Message, "outside.png");
    }

    [TestMethod]
    public async Task Assess_AcceptsObjectValidationStatesFromSemanticSnapshot()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var reportPath = fixture.Path("migration-report.json");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath))!.AsObject();
        report["validation"]!["sourceBaseline"]!["status"] = "captured";
        report["validation"]!["sourceBaseline"]!["states"] = new JsonArray(
            new JsonObject
            {
                ["id"] = "state-1",
                ["status"] = "captured"
            });
        report["validation"]!["targetReplay"]!["states"] = new JsonArray(
            new JsonObject
            {
                ["id"] = "state-1",
                ["status"] = "unverified",
                ["reason"] = "A fresh runner-controlled replay is required."
            });
        await File.WriteAllTextAsync(reportPath, report.ToJsonString());

        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root);

        Assert.AreEqual(WorkflowStage.MechanicalStabilization, snapshot.Stage);
        Assert.AreEqual("build", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public async Task RecordFailure_RejectsOutOfRootEvidence()
    {
        using var fixture = await RunnerFixture.CreateAsync();

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordFailureAsync(
                fixture.Root,
                "runtime",
                "crash",
                stateId: null,
                ["../outside.log"]));
    }

    [TestMethod]
    public async Task RecordFailure_RequiresEvidenceAndKnownState()
    {
        using var fixture = await RunnerFixture.CreateAsync();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            UwpEnforcementRunner.RecordFailureAsync(
                fixture.Root,
                "runtime",
                "crash",
                stateId: null,
                []));
        var evidence = fixture.Path(".migration-evidence/crash.log");
        await File.WriteAllTextAsync(evidence, "crash");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordFailureAsync(
                fixture.Root,
                "runtime",
                "crash",
                "unknown-state",
                [".migration-evidence/crash.log"]));
    }

    [TestMethod]
    public async Task SyncReport_RejectsParseableInvalidStatePlan()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var plan = await fixture.LoadAsync<StatePlanDocument>(
            ".migration-evidence/state-plan.json");
        plan.States[0].Actions.Clear();
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ".migration-evidence/state-plan.json",
            plan);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root));
    }

    [TestMethod]
    public async Task Assess_InvalidStatePlanJsonFailsClosed()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.Path(".migration-evidence/state-plan.json"),
            "{");

        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root);

        Assert.AreEqual("state-plan", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public async Task Assess_InvalidAttestationWritesBlockedSnapshot()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await EnforcementStore.WriteTextAtomicAsync(
            fixture.Root,
            EnforcementStore.BuildAttestationPath,
            "{");

        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root);

        Assert.AreEqual("evidence", snapshot.Frontier?.Kind);
        Assert.IsTrue(File.Exists(
            fixture.Path(EnforcementStore.StatePath)));
    }

    [TestMethod]
    public async Task Assess_MissingReportDoesNotMutateTarget()
    {
        var target = TestDirectories.Create(
            "enforcement-missing-report-");
        try
        {
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
                UwpEnforcementRunner.AssessAsync(target));

            Assert.IsFalse(Directory.Exists(System.IO.Path.Combine(
                target.FullName,
                ".migration-evidence")));
        }
        finally
        {
            target.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void WinAppParser_RejectsCamelCaseContract()
    {
        var parsed = WinAppRunResultParser.Parse(
            """{"processId":42,"error":"failed"}""");

        Assert.IsTrue(parsed.Structured);
        Assert.IsNull(parsed.ProcessId);
        Assert.IsNull(parsed.Error);
    }

    private static async Task<string> InvokeWithCapturedOutputAsync(
        ParseResult parseResult)
    {
        using var writer = new StringWriter();
        var original = Console.Out;
        try
        {
            Console.SetOut(writer);
            await parseResult.InvokeAsync(
                parseResult.InvocationConfiguration,
                CancellationToken.None);
        }
        finally
        {
            Console.SetOut(original);
        }
        return writer.ToString();
    }

    private sealed class FakeProcessRunner(
        params EnforcementProcessResult[] results)
        : IEnforcementProcessRunner
    {
        private readonly Queue<EnforcementProcessResult> _results =
            new(results);

        internal int CallCount { get; private set; }
        internal List<string[]> Calls { get; } = [];

        public Task<EnforcementProcessResult> RunAsync(
            string fileName,
            DirectoryInfo workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Calls.Add([.. arguments]);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class RunnerFixture : IDisposable
    {
        private RunnerFixture(DirectoryInfo root, string projectPath)
        {
            Root = root;
            ProjectPath = projectPath;
        }

        internal DirectoryInfo Root { get; }
        internal string ProjectPath { get; }

        internal static async Task<RunnerFixture> CreateAsync(
            bool analyzer = true)
        {
            var root = TestDirectories.Create("enforcement-runner-");
            var projectPath = System.IO.Path.Combine(
                root.FullName,
                "Target.csproj");
            var analyzerReference = analyzer
                ? """
                  <ItemGroup>
                    <PackageReference Include="Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer"
                                      Version="1.0.0"
                                      PrivateAssets="all" />
                  </ItemGroup>
                  """
                : "";
            await File.WriteAllTextAsync(
                projectPath,
                $"""
                 <Project Sdk="Microsoft.NET.Sdk">
                   <PropertyGroup><TargetFramework>net10.0-windows10.0.19041.0</TargetFramework></PropertyGroup>
                   {analyzerReference}
                 </Project>
                 """);
            var report = new MigrationReport
            {
                Source = new MigrationProject
                {
                    Root = root.FullName,
                    ProjectFile = "Source.csproj"
                },
                Target = new MigrationProject
                {
                    Root = root.FullName,
                    ProjectFile = "Target.csproj"
                }
            };
            report.MechanicalVerification.Status = "passed";
            await WritePngAsync(
                System.IO.Path.Combine(
                    root.FullName,
                    ".migration-evidence",
                    "states",
                    "state-1",
                    "source.png"));
            await EnforcementStore.WriteTextAtomicAsync(
                root,
                ".migration-evidence/states/state-1/source-ui.json",
                """{"windows":[{"elements":[{"type":"Window","name":"Source"}]}]}""");
            await EnforcementStore.WriteTextAtomicAsync(
                root,
                ".migration-evidence/states/state-1/source-action.json",
                """{"success":true,"message":"The action completed."}""");
            await File.WriteAllTextAsync(
                System.IO.Path.Combine(root.FullName, "migration-report.json"),
                JsonSerializer.Serialize(
                    report,
                    MigrateJsonContext.Default.MigrationReport));
            await EnforcementStore.WriteAtomicAsync(
                root,
                report.Validation.StatePlan,
                new StatePlanDocument
                {
                    States =
                    [
                        new()
                        {
                            Id = "state-1",
                            Actions = ["Attempt to open the known state."],
                            ExpectedOutcome = "Known state is accounted for.",
                            Source = new StateObservation
                            {
                                Status = "captured",
                                Evidence =
                                [
                                    ".migration-evidence/states/state-1/source.png",
                                    ".migration-evidence/states/state-1/source-action.json",
                                    ".migration-evidence/states/state-1/source-ui.json"
                                ]
                            },
                            Target = new StateObservation
                            {
                                Status = "unverified",
                                Reason = "Target behavior has not been observed."
                            },
                            Comparison = new StateComparison
                            {
                                Status = "unverified",
                                Reason = "No comparable source state exists."
                            }
                        }
                    ]
                });
            return new RunnerFixture(root, projectPath);
        }

        internal async Task VerifyStateAsync()
        {
            var plan = await LoadAsync<StatePlanDocument>(".migration-evidence/state-plan.json");
            await WriteTargetEvidenceAsync();
            plan.States[0].Target.Status = "passed";
            plan.States[0].Target.Evidence =
            [
                ".migration-evidence/states/state-1/target.png",
                ".migration-evidence/states/state-1/action.log",
                ".migration-evidence/states/state-1/stdout.log"
            ];
            plan.States[0].Comparison.Status = "verified";
            await EnforcementStore.WriteAtomicAsync(Root, ".migration-evidence/state-plan.json", plan);
        }

        internal async Task WriteTargetEvidenceAsync()
        {
            await WritePngAsync(Path(
                ".migration-evidence/states/state-1/target.png"));
            await File.WriteAllTextAsync(
                Path(".migration-evidence/states/state-1/action.log"),
                """{"success":true,"message":"The action completed."}""");
            await File.WriteAllTextAsync(
                Path(".migration-evidence/states/state-1/stdout.log"),
                """{"windows":[{"elements":[{"type":"Window","name":"Target"}]}]}""");
        }

        internal async Task<MigrationReport> LoadReportAsync() =>
            JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(Path("migration-report.json")),
                MigrateJsonContext.Default.MigrationReport)!;

        internal async Task SaveReportAsync(MigrationReport report) =>
            await File.WriteAllTextAsync(
                Path("migration-report.json"),
                JsonSerializer.Serialize(
                    report,
                    MigrateJsonContext.Default.MigrationReport));

        internal string Path(string relativePath) =>
            System.IO.Path.Combine(
                Root.FullName,
                relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        internal async Task<T> LoadAsync<T>(string relativePath)
            where T : class
        {
            var (value, error) = await EnforcementStore.TryLoadAsync<T>(
                Root,
                relativePath);
            Assert.IsNull(error);
            return value!;
        }

        private static async Task WritePngAsync(string path)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(
                path,
                Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        }

        public void Dispose() => TestDirectories.Delete(Root);
    }
}
