// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    public async Task NeutralContracts_IndependentWorkspaceAnchorsRemainIndependent()
    {
        using var first = await RunnerFixture.CreateAsync(unstarted: true);
        using var second = await RunnerFixture.CreateAsync(unstarted: true);
        var firstPlan = await NewRequiredPlanAsync(first);
        var secondPlan = await NewRequiredPlanAsync(second);
        secondPlan.States[0].ExpectedOutcome = "A separate generic requirement.";
        await EnforcementStore.WriteAtomicAsync(second.Root, ClosurePlan, secondPlan);
        var a = await UwpEnforcementRunner.SealContractAsync(first.Root);
        var b = await UwpEnforcementRunner.SealContractAsync(second.Root);
        Assert.AreNotEqual(a.RequiredContractHash, b.RequiredContractHash);

        Assert.AreEqual(a.RequiredContractHash, RequiredStateContracts.Freeze(first.Root, firstPlan));
        Assert.AreEqual(b.RequiredContractHash, RequiredStateContracts.Freeze(second.Root, secondPlan));
        Assert.AreEqual(a.RequiredContractHash, RequiredStateContracts.Require(first.Root, firstPlan.States[0]));
        Assert.AreEqual(b.RequiredContractHash, RequiredStateContracts.Require(second.Root, secondPlan.States[0]));
    }

    [TestMethod]
    public async Task NeutralContracts_InterleavedWorkspaceOperationsKeepFreshAnchors()
    {
        using var first = await RunnerFixture.CreateAsync(unstarted: true);
        using var second = await RunnerFixture.CreateAsync(unstarted: true);
        var firstPlan = await NewRequiredPlanAsync(first);
        var secondPlan = await NewRequiredPlanAsync(second);
        secondPlan.States[0].ExpectedOutcome = "A separate generic observation remains stable.";
        await EnforcementStore.WriteAtomicAsync(second.Root, ClosurePlan, secondPlan);
        var firstContract = await UwpEnforcementRunner.SealContractAsync(first.Root);
        var secondContract = await UwpEnforcementRunner.SealContractAsync(second.Root);
        await ObserveSourceAsync(first, firstPlan);
        await ObserveSourceAsync(second, secondPlan);
        await RunNeutralTargetAsync(first);
        await RunNeutralTargetAsync(second);

        var latestProbes = new Dictionary<RunnerFixture, UiDevelopmentProbe>();
        for (var round = 1; round <= 4; round++)
        {
            foreach (var (current, other) in new[] { (first, second), (second, first) })
            {
                var previousHead = WorkflowOperationAuthentication.ReadHead(current.Root);
                var otherHead = WorkflowOperationAuthentication.ReadHead(other.Root);
                latestProbes[current] = await ContractProbeAsync(current, ["inspect", "-w", "42", "--json"], ClosureTree);

                Assert.AreNotEqual(previousHead, WorkflowOperationAuthentication.ReadHead(current.Root),
                    $"Round {round}: the completed operation must advance its persisted head.");
                Assert.AreEqual(otherHead, WorkflowOperationAuthentication.ReadHead(other.Root),
                    $"Round {round}: advancing one workspace must not overwrite the other's head.");
                var progress = await AssertNeutralAnchorsAsync(current);
                Assert.HasCount(2 + round, progress.Operations);
                await AssertNeutralAnchorsAsync(other);
                Assert.AreEqual(firstContract.RequiredContractHash, RequiredStateContracts.Freeze(first.Root, firstPlan));
                Assert.AreEqual(secondContract.RequiredContractHash, RequiredStateContracts.Freeze(second.Root, secondPlan));
            }
        }

        foreach (var fixture in new[] { first, second })
        {
            await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1",
                [latestProbes[fixture].ArtifactPath]);
            var complete = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
            Assert.AreEqual("passed", complete.Status);
            await AssertNeutralAnchorsAsync(fixture);
        }
    }

    [TestMethod]
    [DataRow(120, 80, 240, 160, true, false)]
    [DataRow(120, 80, 240, 160, true, true)]
    [DataRow(96, 144, 144, 216, true, false)]
    [DataRow(96, 144, 144, 216, true, true)]
    [DataRow(200, 100, 150, 75, false, false)]
    [DataRow(200, 100, 150, 75, false, true)]
    public async Task NeutralGrid_MultipleSemanticRegionsAreSizeAndDpiIndependent(
        int width, int height, int imageWidth, int imageHeight, bool bounds, bool blankSecond)
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await PlanNeutralGridAsync(fixture, bounds, screenshotIndex: 1);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        var sourceTree = GridTree(width, height, 40, 70, 7, 1100);
        var targetTree = GridTree(width, height, 320, 180, 42, 4242);
        await ObserveSourceAsync(fixture, plan, sourceTree.ToJsonString(), GridImage(width, height, false));
        if (bounds) await AddSourceCaptureAsync(fixture, plan, CoordinateCapture(width, height, 7, 1100, 40, 70, width, height));
        await RunNeutralTargetAsync(fixture);
        var inspect = await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], targetTree.ToJsonString());
        var image = GridImage(imageWidth, imageHeight, blankSecond);
        Assert.AreEqual(PngInformation.Informative, PngEvidence.Classify(await WriteSceneAsync(fixture, image)));
        var capture = CoordinateCapture(imageWidth, imageHeight, 42, 4242, 320, 180, width, height);
        var screenshot = await new UwpEnforcementRunner(new ScreenshotCliProcessRunner(png: image, capture: capture),
            windowOwnerResolver: TestWindowOwnerResolver.Shared)
            .InvokeUiAsync(fixture.Root, "winapp", ["screenshot", "-w", "42", "--json"], [], "state-1");

        var evidence = new List<string> { inspect.ArtifactPath, screenshot.ArtifactPath };
        if (bounds)
            evidence.Add((await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], targetTree.ToJsonString())).ArtifactPath);
        var recorded = await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", evidence);

        Assert.AreEqual(blankSecond ? StateVerificationLevel.EvidenceValid : StateVerificationLevel.ParityVerified,
            recorded.StateVerifications.Single().Level);
        if (!blankSecond)
            Assert.AreEqual("passed", (await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared)).Status);
        else
            Assert.AreNotEqual("passed", recorded.Status, "A good first ROI and informative overall image cannot rescue the blank second ROI.");
    }

    [TestMethod]
    [DataRow("wrong-window")]
    [DataRow("wrong-process")]
    [DataRow("wrong-capture-owner")]
    [DataRow("moved-window")]
    [DataRow("resized-window")]
    [DataRow("out-of-bounds")]
    [DataRow("ambiguous-element")]
    [DataRow("intervening-action")]
    [DataRow("no-coordinates")]
    [DataRow("invalid-content-rect")]
    [DataRow("wrong-coordinate-space")]
    [DataRow("element-moved-after")]
    [DataRow("missing-post-bounds")]
    public async Task NeutralGrid_StaleOrUnownedBoundsFailClosed(string mutation)
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await PlanNeutralGridAsync(fixture, bounds: true, screenshotIndex: mutation == "intervening-action" ? 2 : 1);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        await ObserveSourceAsync(fixture, plan, GridTree(120, 80, 40, 70, 7, 1100).ToJsonString(), GridImage(120, 80, false));
        await AddSourceCaptureAsync(fixture, plan, CoordinateCapture(120, 80, 7, 1100, 40, 70, 120, 80));
        await RunNeutralTargetAsync(fixture);
        var tree = GridTree(120, 80, 320, 180, 42, 4242);
        var owner = tree["windows"]![0]!;
        var zones = owner["elements"]![0]!["children"]![0]!["children"]!.AsArray();
        var capture = CoordinateCapture(240, 160, 42, 4242, 320, 180, 120, 80);
        switch (mutation)
        {
            case "wrong-window": owner["hwnd"] = 84; break;
            case "wrong-process": owner["processId"] = 1111; break;
            case "wrong-capture-owner": capture["hwnd"] = 84; break;
            case "moved-window":
                capture["coordinates"]!["sourceBounds"]!["left"] = 340;
                capture["coordinates"]!["sourceBounds"]!["right"] = 460;
                break;
            case "resized-window": capture["coordinates"]!["sourceBounds"]!["right"] = 460; break;
            case "out-of-bounds": zones[0]!["x"] = 1000; break;
            case "ambiguous-element": zones.Add(zones[0]!.DeepClone()); break;
            case "no-coordinates": capture.Remove("coordinates"); break;
            case "invalid-content-rect": capture["coordinates"]!["contentRect"]!["right"] = 500; break;
            case "wrong-coordinate-space": owner["coordinateSpace"] = "unknown-space"; break;
        }
        var probes = new List<string>
        {
            (await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], tree.ToJsonString())).ArtifactPath
        };
        if (mutation == "intervening-action")
            probes.Add((await ContractProbeAsync(fixture, ["invoke", "Modify", "-w", "42", "--json"],
                """{"elementId":"Modify","pattern":"Invoke","hwnd":42}""")).ArtifactPath);
        var screenshot = await new UwpEnforcementRunner(
            new ScreenshotCliProcessRunner(png: GridImage(240, 160, false), capture: capture),
            windowOwnerResolver: TestWindowOwnerResolver.Shared)
            .InvokeUiAsync(fixture.Root, "winapp", ["screenshot", "-w", "42", "--json"], [], "state-1");
        probes.Add(screenshot.ArtifactPath);
        if (mutation != "missing-post-bounds")
        {
            if (mutation == "element-moved-after") zones[0]!["x"] = 338;
            probes.Add((await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], tree.ToJsonString())).ArtifactPath);
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", probes));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NeutralGrid_OmittedMovementReceiptsCannotHideStaleGeometry(bool deleteHistoryEntries)
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await PlanNeutralGridAsync(fixture, bounds: true, screenshotIndex: 1);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        await ObserveSourceAsync(fixture, plan, GridTree(120, 80, 40, 70, 7, 1100).ToJsonString(), GridImage(120, 80, false));
        await AddSourceCaptureAsync(fixture, plan, CoordinateCapture(120, 80, 7, 1100, 40, 70, 120, 80));
        await RunNeutralTargetAsync(fixture);
        var tree = GridTree(120, 80, 320, 180, 42, 4242).ToJsonString();
        var before = await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], tree);
        var move = await ContractProbeAsync(fixture, ["invoke", "Move", "-w", "42", "--json"], """{"pattern":"Invoke","hwnd":42}""");
        var image = await new UwpEnforcementRunner(new ScreenshotCliProcessRunner(png: GridImage(240, 160, false),
            capture: CoordinateCapture(240, 160, 42, 4242, 320, 180, 120, 80)),
            windowOwnerResolver: TestWindowOwnerResolver.Shared)
            .InvokeUiAsync(fixture.Root, "winapp", ["screenshot", "-w", "42", "--json"], [], "state-1");
        var undo = await ContractProbeAsync(fixture, ["invoke", "Undo", "-w", "42", "--json"], """{"pattern":"Invoke","hwnd":42}""");
        var after = await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], tree);
        if (deleteHistoryEntries)
        {
            var history = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
            history.Operations.RemoveAll(operation => operation.Id == move.OperationId || operation.Id == undo.OperationId);
            await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, history);
        }

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1",
                [before.ArtifactPath, image.ArtifactPath, after.ArtifactPath]));

        if (!deleteHistoryEntries) StringAssert.Contains(error.Message, "omitted receipts");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NeutralGrid_RestoringAValidHistoryPrefixCannotGetASuccessorSigned(bool afterImage)
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await PlanNeutralGridAsync(fixture, bounds: true, screenshotIndex: 1);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        await ObserveSourceAsync(fixture, plan, GridTree(120, 80, 40, 70, 7, 1100).ToJsonString(), GridImage(120, 80, false));
        await AddSourceCaptureAsync(fixture, plan, CoordinateCapture(120, 80, 7, 1100, 40, 70, 120, 80));
        await RunNeutralTargetAsync(fixture);
        var tree = GridTree(120, 80, 320, 180, 42, 4242).ToJsonString();
        await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], tree);
        if (afterImage)
            await new UwpEnforcementRunner(new ScreenshotCliProcessRunner(png: GridImage(240, 160, false),
                capture: CoordinateCapture(240, 160, 42, 4242, 320, 180, 120, 80)),
                windowOwnerResolver: TestWindowOwnerResolver.Shared)
                .InvokeUiAsync(fixture.Root, "winapp", ["screenshot", "-w", "42", "--json"], [], "state-1");
        var prefix = await File.ReadAllBytesAsync(fixture.Path(EnforcementStore.ProgressPath));
        var snapshot = await File.ReadAllBytesAsync(fixture.Path(EnforcementStore.StatePath));
        var prefixHead = WorkflowOperationAuthentication.ReadHead(fixture.Root);
        await ContractProbeAsync(fixture, ["invoke", "Move", "-w", "42", "--json"], """{"pattern":"Invoke","hwnd":42}""");
        Assert.AreNotEqual(prefixHead, WorkflowOperationAuthentication.ReadHead(fixture.Root));
        await File.WriteAllBytesAsync(fixture.Path(EnforcementStore.ProgressPath), prefix);
        await File.WriteAllBytesAsync(fixture.Path(EnforcementStore.StatePath), snapshot);
        var process = new FakeProcessRunner(new EnforcementProcessResult(0, tree, ""));
        var runner = new UwpEnforcementRunner(process, windowOwnerResolver: TestWindowOwnerResolver.Shared);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(fixture.Root, "winapp", [afterImage ? "inspect" : "screenshot", "-w", "42", "--json"], [], "state-1"));

        Assert.AreEqual(0, process.CallCount, "A genuine signing API must refuse a restored, once-valid history head.");
    }

    [TestMethod]
    [DataRow("complete", StateVerificationLevel.ParityVerified)]
    [DataRow("missing-return", StateVerificationLevel.EvidenceValid)]
    [DataRow("out-of-order", StateVerificationLevel.EvidenceValid)]
    public async Task NeutralToggle_OrderedAToBToARestorationIsRequired(string trace, string expected)
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        var state = plan.States[0];
        state.Actions = ["Apply option B.", "Restore option A."];
        state.Assertions =
        [
            new() { Id = "initial-a", Kind = "ui-property-equals-source", ProbeIndex = 0, AutomationId = "main", Property = "name", SourceEvidence = ".migration-evidence/states/state-1/source-ui.json" },
            new() { Id = "observed-b", Kind = "ui-property-equals-source", ProbeIndex = 2, AutomationId = "main", Property = "name", SourceEvidence = ".migration-evidence/states/state-1/option-b.json" },
            new() { Id = "restored-a", Kind = "ui-property-equals-source", ProbeIndex = 4, AutomationId = "main", Property = "name", SourceEvidence = ".migration-evidence/states/state-1/source-ui.json" }
        ];
        state.Verification = new StateVerificationContract
        {
            RequiredActions = [new("apply-b", ["invoke", "Option-B"]), new("restore-a", ["invoke", "Option-A"])],
            Steps = [new("assertion", "initial-a"), new("action", "apply-b"), new("assertion", "observed-b"),
                new("action", "restore-a"), new("assertion", "restored-a")],
            Restorations = [new("a-b-a", "initial-a", "restore-a", "restored-a")]
        };
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        var a = ClosureTree.Replace("Ready", "Option-A", StringComparison.Ordinal);
        var b = ClosureTree.Replace("Ready", "Option-B", StringComparison.Ordinal);
        await ObserveSourceAsync(fixture, plan, a);
        await File.WriteAllTextAsync(fixture.Path(".migration-evidence/states/state-1/option-b.json"), b);
        state.Source.Evidence.Add(".migration-evidence/states/state-1/option-b.json");
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await RunNeutralTargetAsync(fixture);
        var firstAction = trace == "out-of-order" ? "Option-A" : "Option-B";
        var secondAction = trace == "out-of-order" ? "Option-B" : "Option-A";
        var probes = new List<string>
        {
            (await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], a)).ArtifactPath,
            (await ContractProbeAsync(fixture, ["invoke", firstAction, "-w", "42", "--json"], """{"pattern":"Invoke","hwnd":42}""")).ArtifactPath,
            (await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], b)).ArtifactPath
        };
        if (trace != "missing-return")
        {
            probes.Add((await ContractProbeAsync(fixture, ["invoke", secondAction, "-w", "42", "--json"], """{"pattern":"Invoke","hwnd":42}""")).ArtifactPath);
            probes.Add((await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], a)).ArtifactPath);
        }

        var result = await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", probes);

        Assert.AreEqual(expected, result.StateVerifications.Single().Level);
    }

    [TestMethod]
    public async Task NeutralStructured_StableValueNeedsNoVisualRegionOrPng()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        var state = plan.States[0];
        state.Assertions[0].Property = "value";
        state.Verification!.RequiredActions[0] = new RequiredStateAction("observe-main", ["get-property", "main", "--property", "Value"]);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        Directory.CreateDirectory(fixture.Path(".migration-evidence/states/state-1"));
        var source = PropertyObservation("Value", "stable-value", "edit-source-a1b2");
        await File.WriteAllTextAsync(fixture.Path(".migration-evidence/states/state-1/source-ui.json"), source);
        state.Source.Status = "captured";
        state.Source.Evidence = [".migration-evidence/states/state-1/source-ui.json"];
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await RunNeutralTargetAsync(fixture);
        var probe = await ContractProbeAsync(fixture, ["get-property", "main", "-p", "Value", "-w", "42", "--json"],
            PropertyObservation("Value", "stable-value", "edit-target-c3d4"));

        await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        var complete = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreEqual("passed", complete.Status);
        Assert.IsFalse(File.Exists(fixture.Path(".migration-evidence/source/state-1.png")));
        Assert.IsFalse(File.Exists(fixture.Path(".migration-evidence/target/state-1.png")));
        Assert.IsNull(state.Assertions[0].Region);
    }

    private static async Task RunNeutralTargetAsync(RunnerFixture fixture)
    {
        var run = await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        await AssertNeutralAnchorsAsync(fixture);
        Assert.IsNotNull(run.Disposition, "The target run must expose its workflow disposition.");
        Assert.IsFalse(run.Disposition.Terminal, run.Disposition.ReasonCode);
    }

    private static async Task<WorkflowProgress> AssertNeutralAnchorsAsync(RunnerFixture fixture)
    {
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        var contract = await fixture.LoadAsync<RequiredStateContractDocument>(RequiredStateContracts.Path);
        foreach (var state in plan.States)
            Assert.AreEqual(contract.RequiredContractHash, RequiredStateContracts.Require(fixture.Root, state),
                $"Workspace {fixture.Root.Name} lost its independently established contract association.");
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        Assert.IsTrue(WorkflowOperationAuthentication.Verify(progress.Operations),
            $"Workspace {fixture.Root.Name} has invalid operation signatures or hash chain.");
        Assert.IsTrue(WorkflowOperationAuthentication.VerifyAnchored(fixture.Root, progress.Operations),
            $"Workspace {fixture.Root.Name}: the {progress.Operations.Count}-operation head does not match its independent anchor: {WorkflowOperationAuthentication.ReadHead(fixture.Root)}");
        return progress;
    }

    private static async Task<StatePlanDocument> PlanNeutralGridAsync(RunnerFixture fixture, bool bounds, int screenshotIndex)
    {
        var plan = await NewRequiredPlanAsync(fixture);
        var state = plan.States[0];
        state.Verification!.Kind = "visual";
        foreach (var (id, x) in new[] { ("zone-one", 0.1), ("zone-two", 0.6) })
        {
            state.Assertions.Add(new StateAssertion
            {
                Id = id, Kind = "visual-region-equals-source", ProbeIndex = screenshotIndex,
                AutomationId = id, SourceEvidence = ".migration-evidence/states/state-1/source.png",
                Region = bounds ? null : new VisualRegion(x, 0.35, 0.3, 0.5),
                BoundsEvidence = bounds ? ".migration-evidence/states/state-1/source-ui.json" : null,
                BoundsProbeIndex = bounds ? 0 : null,
                BoundsAfterProbeIndex = bounds ? screenshotIndex + 1 : null,
                SourceCaptureEvidence = bounds ? ".migration-evidence/states/state-1/source-capture.json" : null,
                Comparison = "sampled-mae", MaximumMeanError = 0.02
            });
            state.Verification.Steps.Add(new RequiredStateStep("assertion", id));
        }
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        return plan;
    }

    private static JsonObject GridTree(double width, double height, double left, double top, long hwnd, int pid) =>
        new()
        {
            ["windows"] = new JsonArray(new JsonObject
            {
                ["hwnd"] = hwnd, ["processId"] = pid, ["coordinateSpace"] = "screen-physical-pixels",
                ["elements"] = new JsonArray(new JsonObject
                {
                    ["type"] = "Window", ["x"] = left, ["y"] = top, ["width"] = width, ["height"] = height,
                    ["children"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "List", ["automationId"] = "main", ["name"] = "Ready",
                        ["children"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "Pane", ["automationId"] = "zone-one", ["x"] = left + width * 0.1,
                            ["y"] = top + height * 0.35, ["width"] = width * 0.3, ["height"] = height * 0.5
                        }, new JsonObject
                        {
                            ["type"] = "Pane", ["automationId"] = "zone-two", ["x"] = left + width * 0.6,
                            ["y"] = top + height * 0.35, ["width"] = width * 0.3, ["height"] = height * 0.5
                        })
                    })
                })
            })
        };

    private static byte[] GridImage(int width, int height, bool blankSecond) => PngTestData.Image(width, height, (x, y) =>
    {
        var nx = (x + 0.5) / width; var ny = (y + 0.5) / height;
        if (ny < 0.2) return ((byte)(x % 2 == 0 ? 20 : 200), (byte)80, (byte)150);
        if (ny >= 0.35 && ny < 0.85)
        {
            if (nx >= 0.1 && nx < 0.4) return ((byte)(nx < 0.25 ? 30 : 210), (byte)110, (byte)60);
            if (nx >= 0.6 && nx < 0.9 && !blankSecond) return ((byte)90, (byte)(nx < 0.75 ? 40 : 200), (byte)70);
        }
        return ((byte)255, (byte)255, (byte)255);
    });

    private static async Task AddSourceCaptureAsync(RunnerFixture fixture, StatePlanDocument plan, JsonObject metadata)
    {
        const string path = ".migration-evidence/states/state-1/source-capture.json";
        await File.WriteAllTextAsync(fixture.Path(path), metadata.ToJsonString());
        plan.States[0].Source.Evidence.Add(path);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
    }

    private static JsonObject CoordinateCapture(int width, int height, long hwnd, int pid,
        double x, double y, double physicalWidth, double physicalHeight) => new()
    {
        ["hwnd"] = hwnd, ["processId"] = pid, ["width"] = width, ["height"] = height,
        ["coordinates"] = new JsonObject
        {
            ["space"] = "screen-physical-pixels",
            ["sourceBounds"] = new JsonObject { ["left"] = x, ["top"] = y, ["right"] = x + physicalWidth, ["bottom"] = y + physicalHeight },
            ["contentRect"] = new JsonObject { ["left"] = 0, ["top"] = 0, ["right"] = width, ["bottom"] = height }
        }
    };
}
