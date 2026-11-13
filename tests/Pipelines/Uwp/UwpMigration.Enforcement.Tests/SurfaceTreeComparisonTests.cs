// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;
using System.Text.Json.Nodes;

namespace UwpMigration.Enforcement.Tests;

[TestClass]
public sealed class SurfaceTreeComparisonTests
{
    [TestMethod]
    [DataRow("same", true)]
    [DataRow("text", false)]
    [DataRow("missing-control", false)]
    [DataRow("hierarchy", false)]
    [DataRow("order", false)]
    [DataRow("window-title", false)]
    public async Task Compare_NormalizesNativeHostsButPreservesApplicationSemantics(string change, bool matches)
    {
        var (source, target) = NativeTrees();
        var content = TargetContent(target);
        switch (change)
        {
            case "text": content[0]!["name"] = "Discard"; break;
            case "missing-control": content.RemoveAt(0); break;
            case "hierarchy":
                var button = content[0]!.DeepClone();
                content[0] = new JsonObject { ["type"] = "Pane", ["children"] = new JsonArray(button) };
                break;
            case "order":
                var first = content[0]!.DeepClone();
                content[0] = content[1]!.DeepClone();
                content[1] = first;
                break;
            case "window-title": target["windows"]![0]!["elements"]![0]!["name"] = "Different"; break;
        }

        var comparison = await CompareAsync(source, target);

        Assert.IsTrue(comparison.Comparable);
        Assert.AreEqual(matches, comparison.Matches);
        Assert.IsTrue(comparison.Diagnostics.Any(item => item.Kind == "platform-host-normalized"));
        if (!matches)
        {
            var diagnostic = comparison.Diagnostics.First(item => item.Kind != "platform-host-normalized");
            Assert.AreEqual("application", diagnostic.RepairOwner);
            Assert.AreEqual("source.json", diagnostic.SourceEvidence);
            Assert.AreEqual("target.json", diagnostic.TargetEvidence);
            Assert.IsNotNull(diagnostic.SourcePath);
            Assert.IsNotNull(diagnostic.TargetPath);
        }
    }

    [TestMethod]
    [DataRow("caption-control")]
    [DataRow("frame-sibling")]
    [DataRow("bridge-sibling")]
    [DataRow("unknown-host")]
    [DataRow("disabled-host")]
    public async Task Compare_UnsupportedHostBoundariesRequireToolSupport(string change)
    {
        var (source, target) = NativeTrees();
        var before = source["windows"]![0]!["elements"]![0]!;
        var after = target["windows"]![0]!["elements"]![0]!;
        var extra = new JsonObject { ["type"] = "Button", ["automationId"] = "custom-command" };
        switch (change)
        {
            case "caption-control": before["children"]![0]!["children"]!.AsArray().Add(extra); break;
            case "frame-sibling": before["children"]!.AsArray().Add(extra); break;
            case "bridge-sibling": after["children"]![0]!["children"]!.AsArray().Add(extra); break;
            case "unknown-host": after["className"] = "UnsupportedDesktopHost"; break;
            case "disabled-host": after["children"]![0]!["isEnabled"] = false; break;
        }

        var comparison = await CompareAsync(source, target);

        Assert.IsFalse(comparison.Comparable);
        Assert.IsFalse(comparison.Matches);
        var diagnostic = comparison.Diagnostics.Single();
        Assert.AreEqual("unsupported-platform-host", diagnostic.Kind);
        Assert.AreEqual("tool", diagnostic.RepairOwner);
        StringAssert.Contains(diagnostic.SuggestedAction, "Do not remove application nodes");
    }

    [TestMethod]
    public async Task Compare_TemporalTextIsNotIgnoredOrDeclaredIncomparable()
    {
        var (source, target) = NativeTrees();
        source["windows"]![0]!["elements"]![0]!["children"]![1]!["children"]![1]!["name"] = "11/12/2026 3:54 PM";
        TargetContent(target)[1]!["name"] = "11/12/2026 4:21 PM";

        var comparison = await CompareAsync(source, target);

        Assert.IsTrue(comparison.Comparable);
        Assert.IsFalse(comparison.Matches);
        var diagnostic = comparison.Diagnostics.Single(item => item.Kind == "input-comparability-review");
        Assert.AreEqual("capture", diagnostic.RepairOwner);
        Assert.AreEqual("11/12/2026 3:54 PM", diagnostic.Expected);
        Assert.AreEqual("11/12/2026 4:21 PM", diagnostic.Actual);
        StringAssert.Contains(diagnostic.SuggestedAction, "not proof of dynamic data");
        StringAssert.Contains(diagnostic.SourcePath!, "/children[1]");
        StringAssert.Contains(diagnostic.TargetPath!, "/children[0]/children[0]/children[1]");
    }

    [TestMethod]
    public async Task Compare_BoundsDifferencesWithoutChangingRawCaptures()
    {
        var (source, target) = NativeTrees();
        var before = source["windows"]![0]!["elements"]![0]!["children"]![1]!["children"]!.AsArray();
        var after = TargetContent(target);
        before.Clear();
        after.Clear();
        for (var index = 0; index < 12; index++)
        {
            before.Add(new JsonObject { ["type"] = "Text", ["name"] = $"Original {index}" });
            after.Add(new JsonObject { ["type"] = "Text", ["name"] = $"Changed {index}" });
        }
        var sourceBytes = source.ToJsonString();
        var targetBytes = target.ToJsonString();

        var comparison = await CompareAsync(source, target);

        Assert.IsFalse(comparison.Matches);
        Assert.HasCount(8, comparison.Diagnostics.Where(item => item.Kind != "platform-host-normalized"));
        Assert.AreEqual(sourceBytes, source.ToJsonString());
        Assert.AreEqual(targetBytes, target.ToJsonString());
    }

    private static JsonArray TargetContent(JsonNode target) =>
        target["windows"]![0]!["elements"]![0]!["children"]![0]!["children"]![0]!["children"]!.AsArray();

    private static (JsonNode Source, JsonNode Target) NativeTrees() =>
        (JsonNode.Parse("""
         {"windows":[{"elements":[{"type":"Window","name":"Sample","className":"ApplicationFrameWindow","isEnabled":true,"isOffscreen":false,"children":[
           {"type":"Window","automationId":"TitleBar","name":"Sample","className":"ApplicationFrameTitleBarWindow","value":"Sample","children":[
             {"type":"MenuBar","automationId":"SystemMenuBar","children":[{"type":"MenuItem","name":"System"}]},
             {"type":"Button","automationId":"Minimize"},{"type":"Button","automationId":"Maximize"},{"type":"Button","automationId":"Close"}]},
           {"type":"Window","name":"Sample","className":"Windows.UI.Core.CoreWindow","isEnabled":true,"isOffscreen":false,"children":[
             {"type":"Button","automationId":"save","name":"Save"},{"type":"Text","name":"Ready"}]},
           {"type":"Pane","className":"ApplicationFrameInputSinkWindow","isEnabled":true,"isOffscreen":false}
         ]}]}]}
         """)!, JsonNode.Parse("""
         {"windows":[{"elements":[{"type":"Window","name":"Sample","className":"WinUIDesktopWin32WindowClass","isEnabled":true,"isOffscreen":false,"children":[
           {"type":"Pane","className":"Microsoft.UI.Content.DesktopChildSiteBridge","isEnabled":true,"isOffscreen":false,"children":[
             {"type":"Pane","className":"InputSiteWindowClass","isEnabled":true,"isOffscreen":false,"children":[
               {"type":"Button","automationId":"save","name":"Save"},{"type":"Text","name":"Ready"}]}]},
           {"type":"TitleBar","automationId":"TitleBar","children":[
             {"type":"MenuBar","automationId":"SystemMenuBar","children":[{"type":"MenuItem","name":"System"}]},
             {"type":"Button","automationId":"Minimize-Restore"},{"type":"Button","automationId":"Maximize-Restore"},{"type":"Button","automationId":"Close"}]}
         ]}]}]}
         """)!);

    private static async Task<SurfaceTreeComparisonResult> CompareAsync(JsonNode source, JsonNode target)
    {
        var root = TestDirectories.Create("surface-host-comparison-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "source.json"), source.ToJsonString());
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "target.json"), target.ToJsonString());
            return SurfaceTreeComparison.Compare(root, "source.json", "target.json");
        }
        finally
        {
            TestDirectories.Delete(root);
        }
    }

    [TestMethod]
    public async Task Compare_PreservesWindowBoundaries()
    {
        var root = TestDirectories.Create(
            "surface-tree-windows-");
        try
        {
            const string source = "source.json";
            const string target = "target.json";
            await File.WriteAllTextAsync(
                Path.Combine(root.FullName, source),
                """
                {"windows":[
                  {"elements":[{"type":"Window","automationId":"first"}]},
                  {"elements":[{"type":"Window","automationId":"second"}]}
                ]}
                """);
            await File.WriteAllTextAsync(
                Path.Combine(root.FullName, target),
                """
                {"windows":[
                  {"elements":[
                    {"type":"Window","automationId":"first"},
                    {"type":"Window","automationId":"second"}
                  ]}
                ]}
                """);

            var comparison =
                SurfaceTreeComparison.Compare(
                    root,
                    source,
                    target);

            Assert.IsTrue(comparison.Comparable);
            Assert.IsFalse(comparison.Matches);
            StringAssert.Contains(
                comparison.Reason,
                "target window(s)");
        }
        finally
        {
            TestDirectories.Delete(root);
        }
    }

    [TestMethod]
    public async Task Compare_IgnoresGeometryAndRuntimeIdentity()
    {
        var root = TestDirectories.Create(
            "surface-tree-geometry-");
        try
        {
            const string source = "source.json";
            const string target = "target.json";
            await File.WriteAllTextAsync(
                Path.Combine(root.FullName, source),
                """
                {"elements":[{"type":"Button","automationId":"save","name":"Save","x":1,"y":2,"width":30,"height":10,"selector":"source-runtime"}]}
                """);
            await File.WriteAllTextAsync(
                Path.Combine(root.FullName, target),
                """
                {"elements":[{"type":"Button","automationId":"save","name":"Save","x":100,"y":200,"width":300,"height":100,"selector":"target-runtime"}]}
                """);

            var comparison =
                SurfaceTreeComparison.Compare(
                    root,
                    source,
                    target);

            Assert.IsTrue(comparison.Comparable);
            Assert.IsTrue(comparison.Matches);
        }
        finally
        {
            TestDirectories.Delete(root);
        }
    }
}
