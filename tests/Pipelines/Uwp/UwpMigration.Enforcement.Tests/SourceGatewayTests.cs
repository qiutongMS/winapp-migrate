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
        var initial = await UwpEnforcementRunner.AssessAsync(
            fixture.Target, TestWindowOwnerResolver.Shared);
        Assert.AreEqual(WorkflowStage.MechanicalStabilization, initial.Stage);
        Assert.AreEqual("blocked", initial.Gates.Single(gate => gate.Id == "source-oracle").Status);
        var process = new FakeProcessRunner(
            Windows((10, 100, "Existing")),
            new EnforcementProcessResult(
                0,
                """{"AUMID":"Contoso.Source_abc!App","ProcessId":4242}""",
                ""),
            Windows((10, 100, "Existing"), (20, 200, "Source")));

        var attestation = await CreateGateway(process, fixture).RunAsync(
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
        Assert.AreEqual("run", process.Calls[1][0]);
        Assert.AreEqual(fixture.SourceLayout.FullName, process.Calls[1][1]);
        Assert.AreEqual("--manifest", process.Calls[1][2]);
        Assert.AreEqual(
            Path.Combine(fixture.SourceLayout.FullName, "AppxManifest.xml"),
            process.Calls[1][3]);
        Assert.AreEqual("--output-appx-directory", process.Calls[1][4]);
        StringAssert.StartsWith(
            process.Calls[1][5],
            Path.Combine(Path.GetTempPath(), "wam-src"));
        CollectionAssert.AreEqual(
            new[] { "--detach", "--json" },
            process.Calls[1][6..]);
        Assert.AreEqual("10.0.22621.0", attestation.TargetPlatformVersion);
        Assert.AreEqual(
            fixture.SourceLayout.FullName,
            attestation.SourceLayout);
        Assert.HasCount(2, attestation.SourceBuildCommand);
        Assert.IsTrue(File.Exists(
            fixture.Resolve(attestation.WindowInventoryBeforePath!)));
        Assert.IsTrue(File.Exists(
            fixture.Resolve(attestation.WindowInventoryAfterPath!)));
        Assert.IsTrue(File.Exists(fixture.Resolve(
            ".migration-evidence/source/gateway/current-run.json")));
    }

    [TestMethod]
    public async Task Run_PersistsBuildFailureWithoutInvokingWinApp()
    {
        using var fixture = await SourceFixture.CreateAsync();
        var process = new FakeProcessRunner();

        var attestation = await CreateGateway(
            process,
            fixture,
            buildExitCode: 7).RunAsync(
                fixture.Target,
                TestOptions());

        Assert.AreEqual(7, attestation.ExitCode);
        Assert.AreEqual(7, attestation.SourceBuildExitCode);
        Assert.HasCount(0, process.Calls);
        Assert.AreEqual(
            "build stdout",
            await File.ReadAllTextAsync(
                fixture.Resolve(attestation.StandardOutputPath)));
        Assert.IsTrue(File.Exists(fixture.Resolve(
            ".migration-evidence/source/gateway/current-run.json")));
    }

    [TestMethod]
    public async Task Capture_RejectsWindowNotAuthorizedByCurrentSourceRun()
    {
        using var fixture = await SourceFixture.CreateAsync();
        var process = LaunchedProcess();
        var gateway = CreateGateway(process, fixture);
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
        process.SaveComparisonScreenshots = true;
        var gateway = CreateGateway(process, fixture,
            windowOwnerResolver: new SourcePickerWindowOwnerResolver());
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
        Assert.HasCount(1, process.ScreenshotCalls);
        var image = process.ScreenshotCalls.Single()[^1];
        Assert.IsTrue(File.Exists(image));
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(Path.GetDirectoryName(fixture.Resolve(probe.ArtifactPath))!, "comparison-capture.json")));
        Assert.AreEqual("saved", receipt.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(run.Id, receipt.RootElement.GetProperty("runId").GetString());
    }

    [TestMethod]
    public async Task Capture_AuthorizesOwnerBoundSystemPickerWindow()
    {
        using var fixture = await SourceFixture.CreateAsync();
        var process = LaunchedProcess(
            new EnforcementProcessResult(
                0,
                """{"nodes":[{"type":"Window","name":"Open"}]}""",
                ""));
        var gateway = CreateGateway(
            process,
            fixture,
            windowOwnerResolver:
                new SourcePickerWindowOwnerResolver());
        var run = await gateway.RunAsync(
            fixture.Target,
            TestOptions());

        var probe = await gateway.CaptureAsync(
            fixture.Target,
            "winapp",
            ["inspect", "-w", "84", "--json"]);
        var persisted = JsonSerializer.Deserialize<SourceRunAttestation>(
            await File.ReadAllTextAsync(fixture.Resolve(
                ".migration-evidence/source/gateway/current-run.json")),
            EnforcementStore.JsonOptions)!;
        var picker = persisted.AuthorizedWindows.Single(
            window => window.Hwnd == 84);

        Assert.AreEqual(run.Id, probe.SourceRunAttestationId);
        Assert.AreEqual("system-picker", picker.BindingKind);
        Assert.AreEqual("PickerHost", picker.ProcessName);
        Assert.AreEqual(20, picker.AuthorizedOwnerHwnd);
    }

    [TestMethod]
    public async Task Capture_RejectsChangedSourceTree()
    {
        using var fixture = await SourceFixture.CreateAsync();
        var process = LaunchedProcess();
        var gateway = CreateGateway(process, fixture);
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
        var gateway = CreateGateway(process, fixture);
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

        await CreateGateway(process, fixture).RunAsync(
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
            CreateGateway(process, fixture).RunAsync(
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

    private static UwpSourceGateway CreateGateway(
        IEnforcementProcessRunner process,
        SourceFixture fixture,
        int buildExitCode = 0,
        IWindowOwnerResolver? windowOwnerResolver = null) =>
        new(
            process,
            new FakeSourceBuilder(
                new UwpSourceBuildResult(
                    ["C:\\MSBuild.exe", fixture.SourceProject],
                    buildExitCode,
                    "build stdout",
                    "build stderr",
                    "10.0.22621.0",
                    buildExitCode == 0 ? fixture.SourceLayout : null)),
            windowOwnerResolver: windowOwnerResolver);

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

    private sealed class SourcePickerWindowOwnerResolver
        : IWindowOwnerResolver, IWindowRelationshipResolver
    {
        private static readonly ProcessIdentity Source =
            new(200, TestWindowOwnerResolver.ProcessStartedAt);
        private static readonly ProcessIdentity Picker =
            new(
                5050,
                TestWindowOwnerResolver.ProcessStartedAt.AddSeconds(1));

        public bool TryGetProcessId(
            long windowHandle,
            out int processId)
        {
            processId = windowHandle switch
            {
                20 => Source.ProcessId,
                84 => Picker.ProcessId,
                _ => 0
            };
            return processId > 0;
        }

        public bool TryGetProcessIdentity(
            int processId,
            out ProcessIdentity identity)
        {
            identity = processId == Picker.ProcessId
                ? Picker
                : Source;
            return processId is 200 or 5050;
        }

        public bool TryGetWindowRelationship(
            long windowHandle,
            out WindowRelationship relationship)
        {
            relationship = windowHandle switch
            {
                84 => new WindowRelationship(
                    Picker,
                    20,
                    "PickerHost",
                    Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.Windows),
                        "SystemApps",
                        "MicrosoftWindows.Client.CBS_cw5n1h2txyewy",
                        "PickerHost.exe")),
                20 => new WindowRelationship(
                    Source,
                    0,
                    "ApplicationFrameHost",
                    null),
                _ => default
            };
            return windowHandle is 20 or 84;
        }
    }

    private sealed class FakeProcessRunner(
        params EnforcementProcessResult[] results)
        : IEnforcementProcessRunner
    {
        private readonly Queue<EnforcementProcessResult> _results =
            new(results);

        internal List<string[]> Calls { get; } = [];
        internal List<string[]> ScreenshotCalls { get; } = [];
        internal bool SaveComparisonScreenshots { get; set; }

        public Task<EnforcementProcessResult> RunAsync(
            string executable,
            DirectoryInfo workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            if (arguments.Count > 1 && arguments[1] == "screenshot" && arguments[^2] == "-o" &&
                arguments[^1].EndsWith("comparison.png", StringComparison.Ordinal))
            {
                ScreenshotCalls.Add([.. arguments]);
                if (SaveComparisonScreenshots)
                    File.WriteAllBytes(arguments[^1], PngTestData.Create());
                return Task.FromResult(new EnforcementProcessResult(
                    SaveComparisonScreenshots ? 0 : 1, "", "Optional screenshot unavailable."));
            }
            Calls.Add([.. arguments]);
            return Task.FromResult(_results.Dequeue());
        }

    }

    private sealed class FakeSourceBuilder(UwpSourceBuildResult result)
        : IUwpSourceBuilder
    {
        public Task<UwpSourceBuildResult> BuildAsync(
            FileInfo sourceProject,
            string? configuration,
            string? architecture,
            CancellationToken cancellationToken)
        {
            Assert.AreEqual("x64", architecture);
            Assert.IsNull(configuration);
            return Task.FromResult(result);
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
            string sourceProject,
            DirectoryInfo sourceLayout)
        {
            Target = target;
            Source = source;
            SourceProject = sourceProject;
            SourceLayout = sourceLayout;
        }

        internal DirectoryInfo Target { get; }
        internal DirectoryInfo Source { get; }
        internal string SourceProject { get; }
        internal DirectoryInfo SourceLayout { get; }

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
            var sourceLayout = Directory.CreateDirectory(
                Path.Combine(source.FullName, "bin", "x64", "Debug"));
            await File.WriteAllTextAsync(
                Path.Combine(sourceLayout.FullName, "AppxManifest.xml"),
                "<Package />");
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
                            Kind = "window",
                            SourceDefinitions = ["MainPage.xaml"],
                            Verification = new StateVerificationContract
                            {
                                TreeProbeIndex = 0
                            },
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
            return new SourceFixture(
                target,
                source,
                sourceProject,
                sourceLayout);
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
            plan.States[0].Source.TreeEvidence = tree;
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
