// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using UwpMigration.Contracts;
using UwpMigration.Enforcement;
using WinAppMigration.Cli.Pipelines.Uwp;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    private const string SemanticTree = """
        {"windows":[{"elements":[{"type":"Window","automationId":"main","name":"Ready","isEnabled":true,"isOffscreen":false,
          "children":[{"type":"Group","name":"","isEnabled":true,"isOffscreen":false,
            "children":[{"type":"Button","name":"Open","isEnabled":true,"isOffscreen":false}]}]}]}]}
        """;

    [TestMethod]
    [DataRow("hierarchy")]
    [DataRow("role")]
    [DataRow("enabled")]
    [DataRow("name")]
    public void StructureAssertions_CompareSemanticTreeNotFlatNames(string change)
    {
        var target = JsonNode.Parse(SemanticTree)!;
        var window = target["windows"]![0]!["elements"]![0]!;
        var group = window["children"]![0]!;
        var button = group["children"]![0]!;
        switch (change)
        {
            case "hierarchy":
                var moved = button.DeepClone();
                group["children"]!.AsArray().Clear();
                window["children"]!.AsArray().Add(moved);
                break;
            case "role": button["type"] = "TextBox"; break;
            case "enabled": button["isEnabled"] = false; break;
            case "name": button["name"] = "Save"; break;
        }
        var assertion = new StateAssertion { Id = "tree", Kind = "ui-structure-equals-source", SourceEvidence = "source.json" };
        Assert.AreNotEqual(
            UiAssertionProtocol.Read(JsonSerializer.Deserialize<JsonElement>(SemanticTree), assertion),
            UiAssertionProtocol.Read(JsonSerializer.Deserialize<JsonElement>(target.ToJsonString()), assertion));
    }

    [TestMethod]
    [DataRow("""{"windows":[{"elements":[{"type":"Pane","name":"","isEnabled":true,"isOffscreen":false,"hasMoreChildren":true}]}]}""")]
    [DataRow("""{"windows":[{"elements":[{"type":"Pane","name":""}]}]}""")]
    [DataRow("""{"windows":[null]}""")]
    [DataRow("""{"passed":true}""")]
    public void StructureAssertions_IncompleteTreesCannotPassAsEqual(string tree)
    {
        var assertion = new StateAssertion { Id = "tree", Kind = "ui-structure-equals-source", SourceEvidence = "source.json" };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            UiAssertionProtocol.Read(JsonSerializer.Deserialize<JsonElement>(tree), assertion));
    }

    [TestMethod]
    public void StructureAssertions_IgnoreRuntimeIdentityButRejectFilteredCaptures()
    {
        var assertion = new StateAssertion { Id = "tree", Kind = "ui-structure-equals-source", SourceEvidence = "source.json" };
        var source = JsonSerializer.Deserialize<JsonElement>(SemanticTree);
        var target = JsonSerializer.Deserialize<JsonElement>(SemanticTree.Replace("\"main\"", "\"generated-id\"", StringComparison.Ordinal));
        Assert.AreEqual(UiAssertionProtocol.Read(source, assertion), UiAssertionProtocol.Read(target, assertion));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            UiAssertionProtocol.Read(source, assertion, ["inspect", "--hide-offscreen", "--json", "-w", "42"]));
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    public async Task FourChecks_ReuseStateClosureAndKeepStrictSnapshotUnchanged(bool missingSurface, bool mismatch, bool exclusiveModes)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.Path("Source.csproj"), "<Project />");
        await File.WriteAllTextAsync(fixture.Path("MainPage.xaml"), exclusiveModes
            ? "<Page><VisualStateGroup><VisualState /><VisualState /></VisualStateGroup></Page>" : "<Page><Button /></Page>");
        var report = await fixture.LoadReportAsync();
        report.MechanicalVerification.ProjectItems.AccountedItems =
            [new MigrationProjectItem { Kind = "Page", Path = "MainPage.xaml" }];
        await fixture.SaveReportAsync(report);
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        plan.UiReviewedFiles = ["MainPage.xaml"];
        plan.States[0].UiSurfaces = missingSurface ? [] : exclusiveModes
            ? ["MainPage.xaml#0", "MainPage.xaml#2", "MainPage.xaml#3"] : ["MainPage.xaml#0"];
        plan.States[0].Assertions[0].Kind = "ui-structure-equals-source";
        plan.States[0].Assertions[0].Property = "";
        plan.States[0].Assertions[0].AutomationId = "";
        await File.WriteAllTextAsync(fixture.Path(plan.States[0].Assertions[0].SourceEvidence), SemanticTree);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(0,
            mismatch ? SemanticTree.Replace("Open", "Wrong", StringComparison.Ordinal) : SemanticTree, ""));
        await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ".migration-evidence/source/gateway/current-run.json",
            new SourceRunAttestation
            {
                Id = "source-run",
                Mode = "detached",
                StandardOutputPath = "stdout.log",
                StandardErrorPath = "stderr.log",
                SourceRoot = fixture.Root.FullName,
                SourceProject = fixture.Path("Source.csproj"),
                SourceFingerprint = TargetFingerprint.Compute(fixture.Root),
                SourceProjectSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture.Path("Source.csproj"))))
            });
        var before = await File.ReadAllTextAsync(fixture.Path(EnforcementStore.StatePath));
        var stateBefore = await File.ReadAllTextAsync(fixture.Path(ClosurePlan));
        var reportBefore = await File.ReadAllTextAsync(fixture.Path("migration-report.json"));

        var gates = await UwpEnforcementRunner.AssessFourChecksAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.HasCount(4, gates);
        Assert.AreEqual(missingSurface || exclusiveModes ? "unknown" : "passed", gates.Single(gate => gate.Id == "surface-accounting").Status);
        Assert.AreEqual(mismatch ? "failed" : "passed", gates.Single(gate => gate.Id == "semantic-structure").Status);
        Assert.AreEqual(before, await File.ReadAllTextAsync(fixture.Path(EnforcementStore.StatePath)));
        Assert.AreEqual(stateBefore, await File.ReadAllTextAsync(fixture.Path(ClosurePlan)));
        Assert.AreEqual(reportBefore, await File.ReadAllTextAsync(fixture.Path("migration-report.json")));
        Assert.IsFalse(Directory.Exists(fixture.Path(".migration-evidence/acceptance")));
        var command = new UwpEnforceCommand(new FakeProcessRunner(), TestWindowOwnerResolver.Shared);
        var parsed = command.Parse(["assess", fixture.Root.FullName, "--acceptance", "four-checks", "--strict"]);
        using var output = new StringWriter();
        var original = Console.Out;
        try
        {
            Console.SetOut(output);
            Assert.AreEqual(missingSurface || mismatch || exclusiveModes ? 1 : 0, await parsed.InvokeAsync());
        }
        finally { Console.SetOut(original); }
        if (!missingSurface && !mismatch && !exclusiveModes)
        {
            plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
            plan.States[0].UiSurfaces!.Add("dynamic:changed-after-capture");
            await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
            var changed = await UwpEnforcementRunner.AssessFourChecksAsync(fixture.Root, TestWindowOwnerResolver.Shared);
            Assert.AreEqual("unknown", changed.Single(gate => gate.Id == "reachability").Status);
        }
    }

    [TestMethod]
    [DataRow("isSelected", "IsSelected", "True")]
    [DataRow("hasKeyboardFocus", "HasKeyboardFocus", "False")]
    [DataRow("isEnabled", "IsEnabled", "True")]
    [DataRow("isOffscreen", "IsOffscreen", "False")]
    [DataRow("name", "Name", "Ready")]
    [DataRow("value", "Value", "42")]
    [DataRow("value", "Value", "")]
    [DataRow("expandState", "ExpandCollapseState", "Expanded")]
    [DataRow("expandState", "ExpandCollapseState", "Collapsed")]
    [DataRow("expandState", "ExpandCollapseState", "PartiallyExpanded")]
    [DataRow("expandState", "ExpandCollapseState", "LeafNode")]
    [DataRow("toggleState", "ToggleState", "Indeterminate")]
    public async Task PropertyAssertions_GenuinePinnedPropertyStringsRecordAndFinalize(string property, string native, string value)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var observation = PropertyObservation(native, value);
        await PlanPropertyAsync(fixture, property, observation);
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(0, observation, ""),
            arguments: ["get-property", "main", "--property:" + native, "--window=42", "--json"]);

        var recorded = await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        var finalized = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreEqual("passed", recorded.Gates.Single(gate => gate.Id == "state-closure").Status);
        Assert.AreEqual("passed", finalized.Status);
        Assert.AreEqual(observation, await File.ReadAllTextAsync(fixture.Path(".migration-evidence/target/state-1-ui.json")));
    }

    [TestMethod]
    [DataRow("isEnabled", "IsEnabled", "true", "True", false)]
    [DataRow("isOffscreen", "IsOffscreen", "false", "False", true)]
    [DataRow("expandState", "ExpandCollapseState", "\"expanded\"", "Expanded", false)]
    [DataRow("expandState", "ExpandCollapseState", "\"collapsed\"", "Collapsed", true)]
    [DataRow("toggleState", "ToggleState", "\"on\"", "On", false)]
    public async Task PropertyAssertions_RealInspectAndQueryFormatsNormalizeConsistently(
        string property, string native, string inspectValue, string queryValue, bool reverse)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var inspect = InspectObservation(property, inspectValue);
        var query = PropertyObservation(native, queryValue);
        await PlanPropertyAsync(fixture, property, reverse ? query : inspect);
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(0, reverse ? inspect : query, ""),
            arguments: reverse ? ["inspect", "main", "-w", "42", "--json"] :
                ["get-property", "main", "-p", native, "-w", "42", "--json"]);

        var recorded = await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);

        Assert.AreEqual("passed", recorded.Gates.Single(gate => gate.Id == "state-closure").Status);
    }

    [TestMethod]
    [DataRow("name-inspect-query", false)]
    [DataRow("name-inspect-query", true)]
    [DataRow("value-query-query", false)]
    [DataRow("value-query-query", true)]
    [DataRow("value-inspect-query", false)]
    [DataRow("value-inspect-query", true)]
    public async Task PropertyAssertions_EmptyAndNullRepresentationsAreEquivalent(
        string scenario,
        bool reverse)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var (property, commandProperty, left, right, inspectCommand) = scenario switch
        {
            "name-inspect-query" => (
                "name",
                "Name",
                InspectObservationWithoutName(),
                NullPropertyObservation("Name"),
                true),
            "value-query-query" => (
                "value",
                "Value",
                PropertyObservation("Value", ""),
                NullPropertyObservation("Value"),
                false),
            "value-inspect-query" => (
                "value",
                "Value",
                InspectObservation("value", "null"),
                PropertyObservation("Value", ""),
                true),
            _ => throw new AssertFailedException(
                $"Unknown property assertion scenario '{scenario}'.")
        };
        await PlanPropertyAsync(fixture, property, reverse ? right : left);
        var probe = await CaptureClosureProbeAsync(fixture,
            new EnforcementProcessResult(0, reverse ? left : right, ""),
            arguments: inspectCommand && reverse
                ? ["inspect", "main", "-w", "42", "--json"]
                : ["get-property", "main", "-p", commandProperty, "-w", "42", "--json"]);

        var recorded = await UwpEnforcementRunner.RecordStateAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);

        Assert.AreEqual("passed", recorded.Gates.Single(gate => gate.Id == "state-closure").Status);
    }

    [TestMethod]
    public async Task PropertyAssertions_ExplicitSelectorIsBoundToCommandNotRuntimeElementId()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        const string selector = "selected-item";
        var source = PropertyObservation("IsSelected", "True", "tab-source-a1b2");
        var target = PropertyObservation("IsSelected", "True", "tab-target-c3d4");
        await PlanPropertyAsync(fixture, "isSelected", source, selector);
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(0, target, ""),
            arguments: ["get-property", selector, "-p=IsSelected", "-w:42", "--json"]);

        var recorded = await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);

        Assert.AreEqual("passed", recorded.Gates.Single(gate => gate.Id == "state-closure").Status);
    }

    [TestMethod]
    public async Task PropertyAssertions_AutomationIdCommandAcceptsGeneratedRuntimeElementIds()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var source = PropertyObservation("IsSelected", "True", "tab-main-a1b2");
        var target = PropertyObservation("IsSelected", "True", "tab-main-c3d4");
        await PlanPropertyAsync(fixture, "isSelected", source);
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(0, target, ""),
            arguments: ["get-property", "main", "-p", "IsSelected", "-w", "42", "--json"]);

        var recorded = await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        var finalized = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreEqual("passed", recorded.Gates.Single(gate => gate.Id == "state-closure").Status);
        Assert.AreEqual("passed", finalized.Status);
        Assert.AreEqual(TestWindowOwnerResolver.WindowHandle, probe.WindowHandle);
        Assert.AreEqual(4242, probe.WindowProcessId);
        Assert.AreEqual(recorded.Replay!.Id, probe.ReplayAttestationId);
        Assert.AreEqual(target, await File.ReadAllTextAsync(fixture.Path(".migration-evidence/target/state-1-ui.json")));
    }

    [TestMethod]
    [DataRow("selector")]
    [DataRow("property")]
    public async Task PropertyAssertions_GeneratedRuntimeIdCannotMaskCommandContractMismatch(string mismatch)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var source = PropertyObservation("IsSelected", "True", "tab-main-a1b2");
        var target = PropertyObservation("IsSelected", "True", "tab-main-c3d4");
        await PlanPropertyAsync(fixture, "isSelected", source);
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(0, target, ""),
            arguments: ["get-property", mismatch == "selector" ? "other-item" : "main",
                "-p", mismatch == "property" ? "HasKeyboardFocus" : "IsSelected", "-w", "42", "--json"]);

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));

        StringAssert.Contains(error.Message, "selector/property arguments must match");
    }

    [TestMethod]
    public async Task PropertyAssertions_GenuineOutputCannotBypassReplayWindowOwnership()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var observation = PropertyObservation("IsSelected", "True");
        await PlanPropertyAsync(fixture, "isSelected", observation);
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var process = new FakeProcessRunner(new EnforcementProcessResult(0, observation, ""));
        var runner = new UwpEnforcementRunner(process, windowOwnerResolver: new TestWindowOwnerResolver(1111));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => runner.InvokeUiAsync(fixture.Root, "winapp",
            ["get-property", "main", "-p", "IsSelected", "-w", "42", "--json"], [], "state-1"));

        Assert.AreEqual(0, process.CallCount);
    }

    [TestMethod]
    public async Task PropertyAssertions_GenuineOutputCannotBypassReplayGenerationBinding()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var observation = PropertyObservation("IsSelected", "True");
        await PlanPropertyAsync(fixture, "isSelected", observation);
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(0, observation, ""),
            arguments: ["get-property", "main", "-p", "IsSelected", "-w", "42", "--json"]);
        const string evidence = ".migration-evidence/property-replay-justification.txt";
        await File.WriteAllTextAsync(fixture.Path(evidence), "A new canonical generation must revalidate the selected property.");
        await new UwpEnforcementRunner(new FakeProcessRunner(ReplaySuccess())).RunAsync(fixture.Root,
            new UwpEnforcementOptions { ForceReplay = true, DiscriminatingEvidence = [evidence] });

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));
    }

    [TestMethod]
    [DataRow("missing-selector")]
    [DataRow("wrong-selector")]
    [DataRow("missing-property")]
    [DataRow("wrong-property")]
    [DataRow("wrong-case")]
    [DataRow("duplicate-property")]
    [DataRow("extra-selector")]
    [DataRow("unknown-option")]
    [DataRow("no-json")]
    public async Task PropertyAssertions_QueryArgumentsMustMatchTheContract(string invalid)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var observation = PropertyObservation("IsSelected", "True");
        await PlanPropertyAsync(fixture, "isSelected", observation);
        var args = new List<string> { "get-property", "main", "-p", "IsSelected", "-w", "42", "--json" };
        switch (invalid)
        {
            case "missing-selector": args.RemoveAt(1); break;
            case "wrong-selector": args[1] = "different-item"; break;
            case "missing-property": args.RemoveRange(2, 2); break;
            case "wrong-property": args[3] = "HasKeyboardFocus"; break;
            case "wrong-case": args[3] = "isSelected"; break;
            case "duplicate-property": args.AddRange(["--property:IsSelected"]); break;
            case "extra-selector": args.Add("other-item"); break;
            case "unknown-option": args.Add("--agent-verified"); break;
            case "no-json": args.Remove("--json"); break;
        }
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(0, observation, ""), arguments: args);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));
    }

    [TestMethod]
    [DataRow("boolean")]
    [DataRow("number")]
    [DataRow("null")]
    [DataRow("lowercase-bool")]
    [DataRow("verdict-text")]
    [DataRow("empty-element")]
    [DataRow("missing-element")]
    [DataRow("whitespace-element")]
    [DataRow("nonstring-element")]
    [DataRow("wrong-property")]
    [DataRow("root-verdict")]
    [DataRow("property-verdict")]
    [DataRow("duplicate-key")]
    public async Task PropertyAssertions_OnlyGenuinePropertyShapesAndValuesAreAccepted(string invalid)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var source = PropertyObservation("IsSelected", "True");
        await PlanPropertyAsync(fixture, "isSelected", source);
        var node = JsonNode.Parse(source)!;
        switch (invalid)
        {
            case "boolean": node["properties"]!["IsSelected"] = true; break;
            case "number": node["properties"]!["IsSelected"] = 1; break;
            case "null": node["properties"]!["IsSelected"] = null; break;
            case "lowercase-bool": node["properties"]!["IsSelected"] = "true"; break;
            case "verdict-text": node["properties"]!["IsSelected"] = "verified"; break;
            case "empty-element": node["elementId"] = ""; break;
            case "missing-element": node.AsObject().Remove("elementId"); break;
            case "whitespace-element": node["elementId"] = " \t"; break;
            case "nonstring-element": node["elementId"] = true; break;
            case "wrong-property": node["properties"] = new JsonObject { ["isSelected"] = "True" }; break;
            case "root-verdict": node["passed"] = true; break;
            case "property-verdict": node["properties"]!["Verified"] = "True"; break;
        }
        var output = invalid == "duplicate-key"
            ? """{"elementId":"main","properties":{"IsSelected":"False","IsSelected":"True"}}"""
            : node.ToJsonString();
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(0, output, ""),
            arguments: ["get-property", "main", "-p", "IsSelected", "-w", "42", "--json"]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));
    }

    [TestMethod]
    [DataRow("isSelected", "true")]
    [DataRow("hasKeyboardFocus", "true")]
    [DataRow("isExpanded", "true")]
    [DataRow("text", "\"Ready\"")]
    [DataRow("expandState", "\"Expanded\"")]
    public async Task PropertyAssertions_SyntheticInspectPropertiesOrEnumsCannotVerifyState(string property, string value)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var output = InspectObservation(property, value);
        await PlanPropertyAsync(fixture, property, output);
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(0, output, ""));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));
    }

    [TestMethod]
    [DataRow("expanded")]
    [DataRow("true")]
    [DataRow("ExpandCollapseState_Expanded")]
    public async Task PropertyAssertions_InvalidNativeExpansionEnumsFailClosed(string value)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await PlanPropertyAsync(fixture, "expandState", PropertyObservation("ExpandCollapseState", "Expanded"));
        var probe = await CaptureClosureProbeAsync(fixture,
            new EnforcementProcessResult(0, PropertyObservation("ExpandCollapseState", value), ""),
            arguments: ["get-property", "main", "-p", "ExpandCollapseState", "-w", "42", "--json"]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));
    }

    private static async Task PlanPropertyAsync(RunnerFixture fixture, string property, string source, string? selector = null)
    {
        await File.WriteAllTextAsync(fixture.Path(".migration-evidence/states/state-1/source-ui.json"), source);
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        plan.States[0].Assertions[0].Property = property;
        plan.States[0].Assertions[0].Selector = selector;
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
    }

    private static string PropertyObservation(string native, string value, string elementId = "tab-main-a1b2") =>
        JsonSerializer.Serialize(new
        {
            elementId,
            element = new
            {
                type = "TabItem", name = native == "Name" ? value : "Main", automationId = "main",
                selector = elementId, className = "TabViewItem", isEnabled = true, isOffscreen = false,
                isInvokable = true, x = 10, y = 20, width = 120, height = 32
            },
            properties = new Dictionary<string, string> { [native] = value }
        });

    private static string InspectObservation(string property, string rawValue) => new JsonObject
    {
        ["windows"] = new JsonArray(new JsonObject
        {
            ["elements"] = new JsonArray(new JsonObject
            {
                ["type"] = "TreeItem", ["automationId"] = "main", ["selector"] = "main",
                [property] = JsonNode.Parse(rawValue)
            })
        })
    }.ToJsonString();

    private static string InspectObservationWithoutName() => new JsonObject
    {
        ["windows"] = new JsonArray(new JsonObject
        {
            ["elements"] = new JsonArray(new JsonObject
            {
                ["type"] = "Text", ["automationId"] = "main", ["selector"] = "main"
            })
        })
    }.ToJsonString();

    private static string NullPropertyObservation(string native) => new JsonObject
    {
        ["elementId"] = "tab-main-a1b2",
        ["element"] = new JsonObject
        {
            ["type"] = "Text", ["name"] = null, ["automationId"] = "main",
            ["selector"] = "tab-main-a1b2", ["className"] = "TextBlock",
            ["isEnabled"] = true, ["isOffscreen"] = false, ["isInvokable"] = false,
            ["x"] = 10, ["y"] = 20, ["width"] = 120, ["height"] = 32
        },
        ["properties"] = new JsonObject { [native] = null }
    }.ToJsonString();
}
