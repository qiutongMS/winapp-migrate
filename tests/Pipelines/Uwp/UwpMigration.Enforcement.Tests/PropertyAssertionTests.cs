// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
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
}
