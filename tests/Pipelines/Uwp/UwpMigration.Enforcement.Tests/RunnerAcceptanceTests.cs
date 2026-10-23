// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
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
        var replay = await fixture.LoadAsync<ReplayAttestation>(
            EnforcementStore.ReplayAttestationPath);
        Assert.IsTrue(replay.Success);
        Assert.AreEqual(4242, replay.ProcessId);
        Assert.AreEqual("Contoso.App", replay.Aumid);
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

        var exitCode = await parseResult.InvokeAsync(
            parseResult.InvocationConfiguration,
            CancellationToken.None);

        Assert.AreEqual(7, exitCode);
        CollectionAssert.AreEqual(
            new[] { "ui", "list", "--json" },
            process.Calls[2]);
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
            await EnforcementStore.WriteTextAtomicAsync(root, ".migration-evidence/source.txt", "Source oracle captured.");
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
                                Evidence = [".migration-evidence/source.txt"]
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
            await EnforcementStore.WriteTextAtomicAsync(Root, ".migration-evidence/target.txt", "Target state observed.");
            plan.States[0].Target.Status = "passed";
            plan.States[0].Target.Evidence = [".migration-evidence/target.txt"];
            plan.States[0].Comparison.Status = "verified";
            await EnforcementStore.WriteAtomicAsync(Root, ".migration-evidence/state-plan.json", plan);
        }

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

        public void Dispose() => TestDirectories.Delete(Root);
    }
}
