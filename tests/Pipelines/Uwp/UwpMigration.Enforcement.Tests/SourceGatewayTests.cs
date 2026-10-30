// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Globalization;
using UwpMigration.Contracts;
using UwpMigration.Enforcement;
using WinAppMigration.Cli.Pipelines.Uwp;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

[TestClass]
public sealed class SourceGatewayTests
{
    [TestMethod]
    public async Task Run_UsesRecordedProjectAndAuthorizesOnlyNewWindows()
    {
        using var fixture = await SourceFixture.CreateAsync();
        var process = new FakeProcessRunner(
            Windows((10, 100, "Existing")),
            new EnforcementProcessResult(
                0,
                """{"AUMID":"Contoso.Source_abc!App","ProcessId":4242}""",
                ""),
            Windows((10, 100, "Existing"), (20, 200, "Source")));

        var attestation = await new UwpSourceGateway(process).RunAsync(
            fixture.Target,
            TestOptions());

        Assert.AreEqual(0, attestation.ExitCode);
        Assert.AreEqual(4242, attestation.ProcessId);
        Assert.AreEqual(fixture.SourceProject, attestation.SourceProject);
        Assert.HasCount(1, attestation.AuthorizedWindows);
        Assert.AreEqual(20, attestation.AuthorizedWindows[0].Hwnd);
        Assert.HasCount(3, process.Calls);
        CollectionAssert.AreEqual(
            new[] { "ui", "list-windows", "--json" },
            process.Calls[0]);
        CollectionAssert.AreEqual(
            new[]
            {
                "run", fixture.SourceProject, "--arch", "x64",
                "--detach", "--json"
            },
            process.Calls[1]);
        Assert.IsTrue(File.Exists(
            fixture.Resolve(attestation.WindowInventoryBeforePath!)));
        Assert.IsTrue(File.Exists(
            fixture.Resolve(attestation.WindowInventoryAfterPath!)));
        Assert.IsTrue(File.Exists(fixture.Resolve(
            ".migration-evidence/source/gateway/current-run.json")));
    }

    [TestMethod]
    public async Task Capture_RejectsWindowNotAuthorizedByCurrentSourceRun()
    {
        using var fixture = await SourceFixture.CreateAsync();
        var process = LaunchedProcess();
        var gateway = new UwpSourceGateway(process);
        await gateway.RunAsync(fixture.Target, TestOptions());

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            gateway.CaptureAsync(
                fixture.Target,
                "winapp",
                ["inspect", "-w", "999", "--json"]));

        StringAssert.Contains(exception.Message, "was not created");
        Assert.HasCount(3, process.Calls);
    }

    [TestMethod]
    public async Task Capture_PersistsProbeForAuthorizedWindow()
    {
        using var fixture = await SourceFixture.CreateAsync();
        var process = LaunchedProcess(
            new EnforcementProcessResult(
                0,
                """{"nodes":[{"type":"Window","name":"Source"}]}""",
                ""));
        var gateway = new UwpSourceGateway(process);
        var run = await gateway.RunAsync(fixture.Target, TestOptions());

        var probe = await gateway.CaptureAsync(
            fixture.Target,
            "winapp",
            [
                "inspect", "-w",
                run.AuthorizedWindows.Single().Hwnd.ToString(
                    CultureInfo.InvariantCulture),
                "--json"
            ]);

        Assert.AreEqual(run.Id, probe.SourceRunAttestationId);
        Assert.AreEqual(20, probe.WindowHandle);
        Assert.AreEqual(0, probe.ExitCode);
        Assert.IsTrue(File.Exists(fixture.Resolve(probe.ArtifactPath)));
        Assert.AreEqual(
            """{"nodes":[{"type":"Window","name":"Source"}]}""",
            await File.ReadAllTextAsync(
                fixture.Resolve(probe.StandardOutputPath)));
        CollectionAssert.AreEqual(
            new[] { "ui", "inspect", "-w", "20", "--json" },
            process.Calls[3]);
    }

    [TestMethod]
    public async Task Capture_RejectsChangedSourceTree()
    {
        using var fixture = await SourceFixture.CreateAsync();
        var process = LaunchedProcess();
        var gateway = new UwpSourceGateway(process);
        await gateway.RunAsync(fixture.Target, TestOptions());
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Source.FullName, "Changed.xaml"),
            "<Page />");

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            gateway.CaptureAsync(
                fixture.Target,
                "winapp",
                ["inspect", "-w", "20", "--json"]));

        StringAssert.Contains(exception.Message, "unchanged recorded source");
        Assert.HasCount(3, process.Calls);
    }

    [TestMethod]
    public async Task Capture_RequiresScreenshotOutputUnderSourceEvidenceRoot()
    {
        using var fixture = await SourceFixture.CreateAsync();
        var process = LaunchedProcess();
        var gateway = new UwpSourceGateway(process);
        await gateway.RunAsync(fixture.Target, TestOptions());

        var missing = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            gateway.CaptureAsync(
                fixture.Target,
                "winapp",
                ["screenshot", "-w", "20", "--json"]));
        StringAssert.Contains(missing.Message, "require -o");

        var outside = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            gateway.CaptureAsync(
                fixture.Target,
                "winapp",
                ["screenshot", "-w", "20", "--json", "-o", "outside.png"]));
        StringAssert.Contains(outside.Message, "source evidence root");
        Assert.HasCount(3, process.Calls);
    }

    [TestMethod]
    public async Task Run_HoldsClosureLockAcrossExternalInvocations()
    {
        using var fixture = await SourceFixture.CreateAsync();
        var process = new LockCheckingProcessRunner(
            Windows((10, 100, "Existing")),
            new EnforcementProcessResult(
                0,
                """{"AUMID":"Contoso.Source_abc!App","ProcessId":4242}""",
                ""),
            Windows((10, 100, "Existing"), (20, 200, "Source")));

        await new UwpSourceGateway(process).RunAsync(
            fixture.Target,
            TestOptions());

        Assert.HasCount(3, process.LockWasHeld);
        Assert.IsTrue(process.LockWasHeld.All(held => held));
    }

    [TestMethod]
    public async Task Operations_AreRejectedAfterSourceOracleCloses()
    {
        using var fixture = await SourceFixture.CreateAsync();
        await fixture.CompleteSourceStateAsync();
        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Target, TestWindowOwnerResolver.Shared);
        Assert.AreEqual(WorkflowStage.MechanicalStabilization, snapshot.Stage);
        var process = new FakeProcessRunner();

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new UwpSourceGateway(process).RunAsync(
                fixture.Target,
                TestOptions()));

        StringAssert.Contains(exception.Message, "source-oracle");
        Assert.HasCount(0, process.Calls);
    }

    [TestMethod]
    public void Cli_ExposesSourceRunDiagnoseAndCapture()
    {
        var command = new UwpSourceCommand(new FakeProcessRunner());

        Assert.AreEqual("source", command.Name);
        Assert.IsNotNull(command.Subcommands.Single(child => child.Name == "run"));
        Assert.IsNotNull(command.Subcommands.Single(child => child.Name == "diagnose"));
        var capture = command.Subcommands.Single(child => child.Name == "capture");
        StringAssert.Contains(capture.Description!, "authorized");
        var existing = Environment.CurrentDirectory;
        Assert.IsFalse(command.Parse(
            ["run", existing, "--arch", "x64"]).Errors.Any());
        Assert.IsFalse(command.Parse(
            [
                "capture", existing, "--", "inspect", "-w", "42",
                "--json"
            ]).Errors.Any());
    }

    private static UwpSourceRunOptions TestOptions() =>
        new()
        {
            Architecture = "x64",
            WindowPollAttempts = 1,
            WindowPollInterval = TimeSpan.Zero
        };

    private static FakeProcessRunner LaunchedProcess(
        params EnforcementProcessResult[] additional) =>
        new(
            [
                Windows((10, 100, "Existing")),
                new EnforcementProcessResult(
                    0,
                    """{"AUMID":"Contoso.Source_abc!App","ProcessId":4242}""",
                    ""),
                Windows((10, 100, "Existing"), (20, 200, "Source")),
                .. additional
            ]);

    private static EnforcementProcessResult Windows(
        params (long Hwnd, int ProcessId, string Title)[] windows) =>
        new(
            0,
            JsonSerializer.Serialize(windows.Select(window => new
            {
                hwnd = window.Hwnd,
                processId = window.ProcessId,
                title = window.Title,
                className = "ApplicationFrameWindow"
            })),
            "");

    private sealed class FakeProcessRunner(
        params EnforcementProcessResult[] results)
        : IEnforcementProcessRunner
    {
        private readonly Queue<EnforcementProcessResult> _results =
            new(results);

        internal List<string[]> Calls { get; } = [];

        public Task<EnforcementProcessResult> RunAsync(
            string executable,
            DirectoryInfo workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add([.. arguments]);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class LockCheckingProcessRunner(
        params EnforcementProcessResult[] results)
        : IEnforcementProcessRunner
    {
        private readonly Queue<EnforcementProcessResult> _results =
            new(results);

        internal List<bool> LockWasHeld { get; } = [];

        public async Task<EnforcementProcessResult> RunAsync(
            string executable,
            DirectoryInfo workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await using var unexpected =
                    await EnforcementStore.AcquireLockAsync(
                        workingDirectory,
                        TimeSpan.FromMilliseconds(25),
                        cancellationToken);
                LockWasHeld.Add(false);
            }
            catch (EnforcementLockTimeoutException)
            {
                LockWasHeld.Add(true);
            }
            return _results.Dequeue();
        }
    }

    private sealed class SourceFixture : IDisposable
    {
        private SourceFixture(
            DirectoryInfo target,
            DirectoryInfo source,
            string sourceProject)
        {
            Target = target;
            Source = source;
            SourceProject = sourceProject;
        }

        internal DirectoryInfo Target { get; }
        internal DirectoryInfo Source { get; }
        internal string SourceProject { get; }

        internal static async Task<SourceFixture> CreateAsync()
        {
            var target = TestDirectories.Create("source-gateway-target");
            var source = TestDirectories.Create("source-gateway-source");
            var sourceProject = Path.Combine(source.FullName, "Source.csproj");
            await File.WriteAllTextAsync(
                sourceProject,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>uap10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                Path.Combine(target.FullName, "Target.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0-windows10.0.19041.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer"
                                      Version="1.0.0"
                                      PrivateAssets="all" />
                  </ItemGroup>
                </Project>
                """);
            var report = new MigrationReport
            {
                Source = new MigrationProject
                {
                    Root = source.FullName,
                    ProjectFile = "Source.csproj"
                },
                Target = new MigrationProject
                {
                    Root = target.FullName,
                    ProjectFile = "Target.csproj"
                }
            };
            report.MechanicalVerification.Status = "passed";
            await File.WriteAllTextAsync(
                Path.Combine(target.FullName, "migration-report.json"),
                JsonSerializer.Serialize(
                    report,
                    MigrateJsonContext.Default.MigrationReport));
            await EnforcementStore.WriteAtomicAsync(
                target,
                report.Validation.StatePlan,
                new StatePlanDocument
                {
                    States =
                    [
                        new()
                        {
                            Id = "source-state",
                            Actions = ["Observe the source window."],
                            ExpectedOutcome = "The source window is observable.",
                            Source = new StateObservation
                            {
                                Status = "not-run"
                            },
                            Target = new StateObservation
                            {
                                Status = "not-run"
                            },
                            Comparison = new StateComparison
                            {
                                Status = "not-run"
                            }
                        }
                    ]
                });
            return new SourceFixture(target, source, sourceProject);
        }

        internal async Task CompleteSourceStateAsync()
        {
            const string image = ".migration-evidence/source/source.png";
            const string tree = ".migration-evidence/source/source-ui.json";
            const string action = ".migration-evidence/source/source-action.json";
            Directory.CreateDirectory(Path.GetDirectoryName(Resolve(image))!);
            await File.WriteAllBytesAsync(
                Resolve(image),
                Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            await File.WriteAllTextAsync(
                Resolve(tree),
                """{"windows":[{"elements":[{"type":"Window","name":"Source"}]}]}""");
            await File.WriteAllTextAsync(
                Resolve(action),
                """{"success":true,"message":"The action completed."}""");
            var plan = (await EnforcementStore.TryLoadAsync<StatePlanDocument>(
                Target,
                ".migration-evidence/state-plan.json")).Value!;
            plan.States[0].Source.Status = "captured";
            plan.States[0].Source.Evidence = [image, tree, action];
            await EnforcementStore.WriteAtomicAsync(
                Target,
                ".migration-evidence/state-plan.json",
                plan);
        }

        internal string Resolve(string relativePath) =>
            Path.Combine(
                Target.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

        public void Dispose()
        {
            TestDirectories.Delete(Target);
            TestDirectories.Delete(Source);
        }
    }
}
