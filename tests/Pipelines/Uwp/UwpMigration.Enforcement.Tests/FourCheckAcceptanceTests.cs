// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json;
using UwpMigration.Enforcement;
using WinAppMigration.Cli.Pipelines.Uwp;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    private const string FourCheckTree = """
        {"windows":[{"hwnd":42,"elements":[{"type":"Window","automationId":"main","name":"Ready","isEnabled":true,"isOffscreen":false,
          "children":[{"type":"Button","name":"Open","isEnabled":true,"isOffscreen":false}]}]}]}
        """;

    [TestMethod]
    public async Task FourChecks_PassesWithoutVisualOrLifecycleAssertionsAndDoesNotCloseStrictWorkflow()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var service = await PrepareFourChecksAsync(fixture);
        var before = await File.ReadAllTextAsync(fixture.Path("migration-report.json"));
        var strictBefore = await File.ReadAllTextAsync(fixture.Path(ClosurePlan));

        var result = await service.AssessAsync(fixture.Root);

        Assert.AreEqual("passed", result.Status);
        Assert.HasCount(4, result.Checks);
        Assert.IsTrue(result.Checks.All(check => check.Status == "passed"));
        Assert.AreEqual(before, await File.ReadAllTextAsync(fixture.Path("migration-report.json")));
        Assert.AreEqual(strictBefore, await File.ReadAllTextAsync(fixture.Path(ClosurePlan)));
        CollectionAssert.Contains(result.ExcludedClaims.ToArray(), "lazy-object-lifecycle");
        CollectionAssert.Contains(result.ExcludedClaims.ToArray(), "visual-parity");
    }

    [TestMethod]
    public async Task FourChecks_OmittedStaticSurfaceCannotPassEvenWhenAllDeclaredStatesPass()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var service = await PrepareFourChecksAsync(fixture);
        var plan = FourCheckJson.Read<FourCheckSurfaceMap>(fixture.Root, FourCheckAcceptance.SurfaceMapPath);
        plan.Mappings.Clear();
        await FourCheckJson.WriteAsync(fixture.Root, FourCheckAcceptance.SurfaceMapPath, plan, CancellationToken.None);

        var result = await service.AssessAsync(fixture.Root);

        Assert.AreEqual("passed", result.States[0].Reachability);
        Assert.AreEqual("passed", result.States[0].Structure?.Status);
        Assert.AreEqual("unknown", result.Checks.Single(check => check.Id == "surface-accounting").Status);
        Assert.AreEqual("partial", result.Status);
    }

    [TestMethod]
    public async Task FourChecks_ChangedTargetCannotReuseOldSuccessfulReplay()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var service = await PrepareFourChecksAsync(fixture);
        await File.AppendAllTextAsync(fixture.ProjectPath, "\n");

        var result = await service.AssessAsync(fixture.Root);

        Assert.AreEqual("unknown", result.Checks.Single(check => check.Id == "startup").Status);
        Assert.AreEqual("partial", result.Status);
    }

    [TestMethod]
    public async Task FourChecks_TamperedTargetOutputCannotBecomeStructureEvidence()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var service = await PrepareFourChecksAsync(fixture);
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        var probe = await fixture.LoadAsync<UiDevelopmentProbe>(plan.States[0].Target.Evidence[0]);
        await File.AppendAllTextAsync(fixture.Path(probe.StandardOutputPath), " ");

        var result = await service.AssessAsync(fixture.Root);

        Assert.AreEqual("unknown", result.States[0].Reachability);
        Assert.AreNotEqual("passed", result.Status);
    }

    [TestMethod]
    public async Task FourChecks_EndpointMustBeObservedNotMerelyDeclared()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var service = await PrepareFourChecksAsync(fixture,
            targetTree: FourCheckTree.Replace("Ready", "Different page", StringComparison.Ordinal));

        var result = await service.AssessAsync(fixture.Root);

        Assert.AreEqual("unknown", result.States[0].Reachability);
        Assert.AreEqual("unknown", result.Checks.Single(check => check.Id == "reachability").Status);
    }

    [TestMethod]
    public async Task FourChecks_UnobservedMutuallyExclusiveModesCannotReuseOnePassingState()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var service = await PrepareFourChecksAsync(fixture, withModes: true);

        var result = await service.AssessAsync(fixture.Root);

        Assert.AreEqual("passed", result.States[0].Reachability);
        var coverage = result.Checks.Single(check => check.Id == "reachability");
        Assert.AreEqual("unknown", coverage.Status);
        Assert.IsTrue(coverage.Issues.Any(issue => issue.Contains("mutually exclusive", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task FourChecks_TamperedSourceOutputCannotBecomeAConvenientBaseline()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var service = await PrepareFourChecksAsync(fixture);
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        var probe = await fixture.LoadAsync<SourceUiProbe>(plan.States[0].Source.Evidence[^1]);
        await File.AppendAllTextAsync(fixture.Path(probe.StandardOutputPath), " ");

        var result = await service.AssessAsync(fixture.Root);

        Assert.AreEqual("unknown", result.States[0].Reachability);
        Assert.AreNotEqual("passed", result.Status);
    }

    [TestMethod]
    public async Task FourChecks_RemovingObservedSourceStateLeavesAnInventoryGap()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var service = await PrepareFourChecksAsync(fixture);
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        plan.States.Clear();
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);

        var result = await service.AssessAsync(fixture.Root);

        var accounting = result.Checks.Single(check => check.Id == "surface-accounting");
        Assert.AreEqual("unknown", accounting.Status);
        Assert.IsTrue(accounting.Issues.Any(issue =>
            issue.Contains("Unassigned source inspect observation", StringComparison.Ordinal)));
        Assert.AreNotEqual("passed", result.Status);
    }

    [TestMethod]
    public async Task FourChecks_CliRequiresExplicitProfile()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var command = new UwpAcceptanceCommand(new FakeProcessRunner());
        var parsed = command.Parse(["inventory", fixture.Root.FullName]);
        Assert.IsTrue(parsed.Errors.Count > 0);
    }

    [TestMethod]
    public async Task FourChecks_DeclaredSurfaceCannotImpersonateGatewayDiscoveredRuntimeNodes()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var service = await PrepareFourChecksAsync(fixture);
        var plan = FourCheckJson.Read<FourCheckSurfaceMap>(fixture.Root, FourCheckAcceptance.SurfaceMapPath);
        plan.AdditionalSurfaces.Add(new("dialog", "runtime-uia", "Requires a real major-state mapping."));
        await FourCheckJson.WriteAsync(fixture.Root, FourCheckAcceptance.SurfaceMapPath, plan, CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.AssessAsync(fixture.Root));
    }

    [TestMethod]
    public async Task FourChecks_ReusesTheExistingImmutableStateContract()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var service = await PrepareFourChecksAsync(fixture);
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        plan.States[0].Actions[0] = "Replace the already frozen requirement.";
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);

        var result = await service.AssessAsync(fixture.Root);

        Assert.AreEqual("unknown", result.States[0].Reachability);
        StringAssert.Contains(result.States[0].Issue!, "immutable required contract");
    }

    private static async Task<FourCheckAcceptance> PrepareFourChecksAsync(
        RunnerFixture fixture, bool withModes = false, string? targetTree = null)
    {
        await File.WriteAllTextAsync(fixture.Path("Source.csproj"), "<Project />");
        var modes = withModes
            ? "<VisualStateManager.VisualStateGroups><VisualStateGroup><VisualState /><VisualState /></VisualStateGroup></VisualStateManager.VisualStateGroups>"
            : "";
        await File.WriteAllTextAsync(fixture.Path("MainPage.xaml"),
            $"""<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Button Content="Open" />{modes}</Page>""");
        var sourceProbe = new SourceUiProbe
        {
            Id = "source-inspect",
            SourceRunAttestationId = "source-run",
            SourceProjectSha256 = Convert.ToHexString(SHA256.HashData(
                await File.ReadAllBytesAsync(fixture.Path("Source.csproj")))),
            SourceFingerprint = TargetFingerprint.Compute(fixture.Root),
            WindowHandle = 10,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Command = ["winapp", "ui", "inspect", "-w", "10", "--json"],
            ArtifactPath = ".migration-evidence/source/gateway/probes/source-inspect/probe.json",
            StandardOutputPath = ".migration-evidence/source/gateway/probes/source-inspect/stdout.log",
            StandardErrorPath = ".migration-evidence/source/gateway/probes/source-inspect/stderr.log"
        };
        var run = new SourceRunAttestation
        {
            Id = "source-run",
            Mode = "run",
            SourceRoot = fixture.Root.FullName,
            SourceProject = fixture.Path("Source.csproj"),
            SourceProjectSha256 = sourceProbe.SourceProjectSha256,
            SourceFingerprint = sourceProbe.SourceFingerprint,
            StartedAt = sourceProbe.StartedAt.AddMinutes(-2),
            CompletedAt = sourceProbe.StartedAt.AddMinutes(-1),
            AuthorizedWindows = [new() { Hwnd = 10, ProcessId = 100 }],
            StandardOutputPath = ".migration-evidence/source/gateway/runs/source-run/stdout.log",
            StandardErrorPath = ".migration-evidence/source/gateway/runs/source-run/stderr.log"
        };
        SourceAcceptanceSeal.Sign(run);
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, sourceProbe.StandardOutputPath,
            FourCheckTree.Replace("\"hwnd\":42", "\"hwnd\":10", StringComparison.Ordinal));
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, sourceProbe.StandardErrorPath, "");
        sourceProbe.StandardOutputSha256 = StateClosureProtocol.FileHash(fixture.Root, sourceProbe.StandardOutputPath);
        sourceProbe.StandardErrorSha256 = StateClosureProtocol.FileHash(fixture.Root, sourceProbe.StandardErrorPath);
        SourceAcceptanceSeal.Sign(sourceProbe);
        await EnforcementStore.WriteAtomicAsync(fixture.Root,
            ".migration-evidence/source/gateway/runs/source-run/attestation.json", run);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, sourceProbe.ArtifactPath, sourceProbe);
        var service = new FourCheckAcceptance((_, _) =>
            Task.FromResult<IReadOnlyList<(string Kind, string Path)>>(
                [("Page", fixture.Path("MainPage.xaml"))]), TestWindowOwnerResolver.Shared);
        await service.InventoryAsync(fixture.Root);
        var plan = FourCheckJson.Read<FourCheckSurfaceMap>(fixture.Root, FourCheckAcceptance.SurfaceMapPath);
        run.AcceptanceSourceInputFingerprint = plan.SourceInputFingerprint;
        SourceAcceptanceSeal.Sign(run);
        await EnforcementStore.WriteAtomicAsync(fixture.Root,
            ".migration-evidence/source/gateway/runs/source-run/attestation.json", run);
        foreach (var mapping in plan.Mappings)
        {
            mapping.Disposition = "mapped";
            mapping.TargetFile = "MainPage.xaml";
            mapping.TargetNode = mapping.SurfaceId[(mapping.SurfaceId.LastIndexOf(':') + 1)..];
            mapping.Reason = "Source XAML declaration is present in the target.";
            mapping.StateIds = ["state-1"];
        }
        await FourCheckJson.WriteAsync(fixture.Root, FourCheckAcceptance.SurfaceMapPath, plan, CancellationToken.None);
        var statePlan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        var state = statePlan.States[0];
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, state.Assertions[0].SourceEvidence,
            await File.ReadAllTextAsync(fixture.Path(sourceProbe.StandardOutputPath)));
        state.Source.Evidence = [state.Assertions[0].SourceEvidence, sourceProbe.ArtifactPath];
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, statePlan);
        var targetProbe = await CaptureClosureProbeAsync(fixture,
            new EnforcementProcessResult(0, targetTree ?? FourCheckTree, ""));
        statePlan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        statePlan.States[0].Target.Evidence = [targetProbe.ArtifactPath];
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, statePlan);
        return service;
    }
}

[TestClass]
public sealed class FourCheckInventoryTests
{
    [TestMethod]
    public async Task Inventory_FollowsEvaluatedLinkedXamlAndReferencedProjectsAndIgnoresBuildArtifacts()
    {
        var root = TestDirectories.Create("four-inventory");
        try
        {
            var entry = new FileInfo(Path.Combine(root.FullName, "Source.csproj"));
            var library = new FileInfo(Path.Combine(root.FullName, "Library.csproj"));
            var linked = Path.Combine(root.FullName, "Shared.xaml");
            await File.WriteAllTextAsync(entry.FullName, "<Project />");
            await File.WriteAllTextAsync(library.FullName, "<Project />");
            await File.WriteAllTextAsync(linked,
                """<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Style TargetType="Button" /></ResourceDictionary>""");
            var builder = new FourCheckInventoryBuilder((project, _) =>
                Task.FromResult<IReadOnlyList<(string Kind, string Path)>>(
                    project.FullName == entry.FullName ? [("ProjectReference", library.FullName)] :
                        [("Page", linked), ("ProjectReference", entry.FullName)]));

            var first = await builder.BuildAsync(entry, CancellationToken.None);
            Directory.CreateDirectory(Path.Combine(root.FullName, "obj"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "obj", "random.txt"), "not an input");
            var second = await builder.BuildAsync(entry, CancellationToken.None);

            Assert.HasCount(3, first.Inputs);
            Assert.HasCount(2, first.Surfaces);
            Assert.AreEqual(first.SourceInputFingerprint, second.SourceInputFingerprint);
            Assert.HasCount(0, first.DiscoveryIssues);
        }
        finally { TestDirectories.Delete(root); }
    }

    [TestMethod]
    public void Inventory_AccountsDefinitionsAndEveryVisualStateWithoutCallingThemVisibleControls()
    {
        var inventory = new FourCheckInventory();
        FourCheckInventoryBuilder.AddXaml("source.xaml",
            """<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><VisualStateManager.VisualStateGroups><VisualStateGroup><VisualState /><VisualState /></VisualStateGroup></VisualStateManager.VisualStateGroups><DataTemplate><TextBlock /></DataTemplate></Page>""",
            inventory);

        Assert.HasCount(7, inventory.Surfaces);
        Assert.HasCount(3, inventory.Surfaces.Where(surface => surface.RequiresReachability).ToArray());
        Assert.AreEqual(1, inventory.Surfaces.Where(surface => surface.Kind == "VisualState")
            .Select(surface => surface.ExclusiveGroup).Distinct().Count());
        Assert.IsFalse(inventory.Surfaces.Single(surface => surface.Kind == "DataTemplate").RequiresReachability);
    }

    [TestMethod]
    public async Task Inventory_FailedEvaluationCannotBePresentedAsComplete()
    {
        var root = TestDirectories.Create("four-eval-failure");
        try
        {
            var project = new FileInfo(Path.Combine(root.FullName, "Source.csproj"));
            await File.WriteAllTextAsync(project.FullName, "<Project />");
            var builder = new FourCheckInventoryBuilder((_, _) =>
                throw new InvalidDataException("Missing UWP workload"));
            var result = await builder.BuildAsync(project, CancellationToken.None);
            Assert.HasCount(1, result.DiscoveryIssues);
        }
        finally { TestDirectories.Delete(root); }
    }

    [TestMethod]
    public void Inventory_TemplateInteractionStatesAreAccountedButAreNotMajorApplicationModes()
    {
        var inventory = new FourCheckInventory();
        FourCheckInventoryBuilder.AddXaml("source.xaml",
            """<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><ControlTemplate><VisualStateGroup><VisualState /></VisualStateGroup></ControlTemplate></ResourceDictionary>""",
            inventory);
        Assert.HasCount(4, inventory.Surfaces);
        Assert.IsFalse(inventory.Surfaces.Any(surface => surface.RequiresReachability));
    }
}
