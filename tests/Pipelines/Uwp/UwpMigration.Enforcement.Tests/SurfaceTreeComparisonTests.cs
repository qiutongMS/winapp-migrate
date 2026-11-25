// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;
using System.Text.Json.Nodes;

namespace UwpMigration.Enforcement.Tests;

[TestClass]
public sealed class SurfaceTreeComparisonTests
{
    [TestMethod]
    [DataRow("interactive")]
    [DataRow("hideDisabled")]
    [DataRow("hideOffscreen")]
    [DataRow("truncated-leaf")]
    [DataRow("truncated-branch")]
    public async Task Compare_RejectsIncompleteEvidenceEvenWhenBothTreesAreIdentical(string change)
    {
        var tree = NativeTrees().Source;
        if (change.StartsWith("truncated-", StringComparison.Ordinal))
        {
            var node = change == "truncated-leaf"
                ? tree["windows"]![0]!["elements"]![0]!["children"]![1]!["children"]![0]!
                : tree["windows"]![0]!["elements"]![0]!;
            node["hasMoreChildren"] = true;
        }
        else
            tree[change] = true;

        var result = await CompareAsync(tree, tree.DeepClone());

        Assert.IsFalse(result.Comparable);
        Assert.IsFalse(result.Matches);
        Assert.AreEqual("unsupported-tree-evidence", result.Diagnostics.Single().Kind);
        Assert.AreEqual("capture", result.Diagnostics.Single().RepairOwner);
    }

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
    [DataRow("popup-host")]
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
            case "popup-host": after["children"]!.AsArray().Insert(0, PopupHost()); break;
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
    [DataRow("same", true)]
    [DataRow("popup-text", false)]
    [DataRow("popup-state", false)]
    [DataRow("missing-popup", false)]
    [DataRow("content", false)]
    [DataRow("caption", false)]
    public async Task Compare_SamePlatformStateChecksPreserveAllNativeAndPopupNodes(string change, bool matches)
    {
        var source = NativeTrees().Target;
        source["windows"]![0]!["elements"]![0]!["children"]!.AsArray().Insert(0, PopupHost());
        var target = source.DeepClone();
        var window = target["windows"]![0]!["elements"]![0]!;
        switch (change)
        {
            case "popup-text": window["children"]![0]!["children"]![0]!["children"]![0]!["children"]![0]!["name"] = "Different tip"; break;
            case "popup-state": window["children"]![0]!["children"]![0]!["children"]![0]!["isOffscreen"] = false; break;
            case "missing-popup": window["children"]!.AsArray().RemoveAt(0); break;
            case "content": window["children"]![1]!["children"]![0]!["children"]![0]!["name"] = "Discard"; break;
            case "caption": window["children"]![2]!["children"]![1]!["name"] = "Changed native caption"; break;
        }

        var comparison = await CompareAsync(source, target);

        Assert.IsTrue(comparison.Comparable);
        Assert.AreEqual(matches, comparison.Matches);
        Assert.IsFalse(comparison.Diagnostics.Any(item => item.Kind is "platform-host-normalized" or "unsupported-platform-host"));
    }

    private static JsonNode PopupHost() => JsonNode.Parse("""
        {"type":"Pane","name":"PopupHost","className":"Microsoft.UI.Content.PopupWindowSiteBridge","children":[
          {"type":"Pane","className":"InputSiteWindowClass","children":[
            {"type":"Window","name":"Popup","className":"Popup","isOffscreen":true,"children":[
              {"type":"ToolTip","name":"Shop"}]}]}]}
        """)!;

    [TestMethod]
    [DataRow("same", true)]
    [DataRow("text", false)]
    [DataRow("state", false)]
    [DataRow("id", false)]
    [DataRow("order", false)]
    [DataRow("hierarchy", false)]
    [DataRow("missing-popup", false)]
    [DataRow("extra-popup", false)]
    [DataRow("host-metadata", false)]
    [DataRow("host-state", false)]
    [DataRow("input-metadata", false)]
    [DataRow("host-sibling", false)]
    public async Task Compare_PairedNativePopupHostsPreserveCompletePopupSemantics(string change, bool matches)
    {
        var (source, target) = NativeTrees();
        var modernHost = PopupHost();
        var popup = modernHost["children"]![0]!["children"]![0]!;
        popup["children"] = JsonNode.Parse("""
            [{"type":"Menu","children":[
              {"type":"MenuItem","automationId":"rename","name":"Rename"},
              {"type":"MenuItem","automationId":"remove","name":"Remove"}]}]
            """);
        var legacyHost = new JsonObject
        {
            ["type"] = "Window",
            ["name"] = "PopupHost",
            ["className"] = "Xaml_WindowedPopupClass",
            ["children"] = new JsonArray(popup.DeepClone())
        };
        var sourceChildren = source["windows"]![0]!["elements"]![0]!["children"]!.AsArray();
        var targetChildren = target["windows"]![0]!["elements"]![0]!["children"]!.AsArray();
        sourceChildren.Insert(0, legacyHost);
        targetChildren.Insert(0, modernHost);
        var items = popup["children"]![0]!["children"]!.AsArray();
        switch (change)
        {
            case "text": items[0]!["name"] = "Different"; break;
            case "state": popup["isOffscreen"] = false; break;
            case "id": items[0]!["automationId"] = "different-command"; break;
            case "order":
                var first = items[0]!.DeepClone();
                items[0] = items[1]!.DeepClone();
                items[1] = first;
                break;
            case "hierarchy":
                popup["children"] = new JsonArray(new JsonObject
                {
                    ["type"] = "Pane",
                    ["children"] = popup["children"]!.DeepClone()
                });
                break;
            case "missing-popup": targetChildren.RemoveAt(0); break;
            case "extra-popup": targetChildren.Insert(0, modernHost.DeepClone()); break;
            case "host-metadata": modernHost["automationId"] = "ApplicationHost"; break;
            case "host-state": modernHost["isEnabled"] = false; break;
            case "input-metadata": modernHost["children"]![0]!["name"] = "Application input"; break;
            case "host-sibling":
                modernHost["children"]!.AsArray().Add(new JsonObject { ["type"] = "Button", ["name"] = "Extra" });
                break;
        }
        var sourceBytes = source.ToJsonString();
        var targetBytes = target.ToJsonString();

        foreach (var result in new[] { await CompareAsync(source, target), await CompareAsync(target, source) })
            Assert.AreEqual(matches, result.Matches, change);

        Assert.AreEqual(sourceBytes, source.ToJsonString());
        Assert.AreEqual(targetBytes, target.ToJsonString());
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

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Compare_NormalizesRecognizedCrossPlatformNavigationViewProviders(bool reverse, bool scrollWrapper)
    {
        var (source, target) = NavigationViewTrees();
        if (!scrollWrapper)
        {
            var navigation = TargetContent(target)[0]!;
            navigation["children"]![1] = navigation["children"]![1]!["children"]![0]!.DeepClone();
        }
        if (reverse)
            (source, target) = (target, source);
        var sourceBytes = source.ToJsonString();
        var targetBytes = target.ToJsonString();

        var comparison = await CompareAsync(source, target);

        Assert.IsTrue(comparison.Comparable);
        Assert.IsTrue(comparison.Matches);
        var diagnostic = comparison.Diagnostics.Single(item => item.Kind != "platform-host-normalized");
        Assert.AreEqual("platform-control-normalized", diagnostic.Kind);
        Assert.AreEqual("tool", diagnostic.RepairOwner);
        Assert.AreEqual("children", diagnostic.Property);
        Assert.AreEqual("source.json", diagnostic.SourceEvidence);
        Assert.AreEqual("target.json", diagnostic.TargetEvidence);
        Assert.AreEqual(sourceBytes, source.ToJsonString());
        Assert.AreEqual(targetBytes, target.ToJsonString());
    }

    [TestMethod]
    [DataRow("same", true)]
    [DataRow("popup-text", false)]
    [DataRow("popup-state", false)]
    [DataRow("dismiss-name", false)]
    [DataRow("dismiss-state", false)]
    [DataRow("missing-popup", false)]
    [DataRow("extra-control", false)]
    [DataRow("background-text", false)]
    public async Task Compare_NavigationViewWithInlinePopupPreservesOverlayAndBackground(string change, bool matches)
    {
        var (source, target) = NavigationViewTrees();
        var before = source["windows"]![0]!["elements"]![0]!["children"]![1]!["children"]!.AsArray();
        var after = TargetContent(target);
        var popup = JsonNode.Parse("""
            {"type":"Window","name":"Popup","className":"Popup","isOffscreen":true,
             "children":[{"type":"Edit","automationId":"InputBox","name":"Enter name","value":"Original"}]}
            """)!;
        var dismiss = JsonNode.Parse("""
            {"type":"Button","automationId":"Light Dismiss","name":"Close","isEnabled":true}
            """)!;
        before.Insert(0, popup.DeepClone());
        before.Insert(1, dismiss.DeepClone());
        after.Insert(0, popup);
        after.Insert(1, dismiss);
        switch (change)
        {
            case "popup-text": popup["children"]![0]!["value"] = "Changed"; break;
            case "popup-state": popup["isOffscreen"] = false; break;
            case "dismiss-name": dismiss["name"] = "Different action"; break;
            case "dismiss-state": dismiss["isEnabled"] = false; break;
            case "missing-popup": after.RemoveAt(0); break;
            case "extra-control": after.Insert(0, new JsonObject { ["type"] = "Button", ["name"] = "Extra" }); break;
            case "background-text": after[2]!["children"]![2]!["name"] = "Changed room action"; break;
        }
        var sourceBytes = source.ToJsonString();
        var targetBytes = target.ToJsonString();

        foreach (var result in new[] { await CompareAsync(source, target), await CompareAsync(target, source) })
        {
            Assert.AreEqual(matches, result.Matches, change);
            if (matches)
                Assert.IsTrue(result.Diagnostics.Any(item => item.Kind == "platform-control-normalized"));
        }
        Assert.AreEqual(sourceBytes, source.ToJsonString());
        Assert.AreEqual(targetBytes, target.ToJsonString());
    }

    [TestMethod]
    [DataRow("same", true)]
    [DataRow("menu-text", false)]
    [DataRow("dismiss-name", false)]
    [DataRow("dismiss-state", false)]
    [DataRow("dismiss-metadata", false)]
    [DataRow("dismiss-child", false)]
    [DataRow("unknown-menu", false)]
    public async Task Compare_MenuFlyoutOnlyAlignsTheKnownGeneratedDismissalPeer(string change, bool matches)
    {
        var (source, target) = NavigationViewTrees();
        var before = source["windows"]![0]!["elements"]![0]!["children"]![1]!["children"]!.AsArray();
        var after = TargetContent(target);
        var popup = JsonNode.Parse("""
            {"type":"Window","name":"Popup","className":"Popup","children":[
              {"type":"Menu","className":"MenuFlyout","children":[{"type":"MenuItem","name":"Rename"}]}]}
            """)!;
        before.Insert(0, popup.DeepClone());
        after.Insert(0, popup);
        var dismiss = JsonNode.Parse("""
            {"type":"Button","automationId":"Light Dismiss","name":"Close","className":"PopupRoot",
             "isEnabled":true,"isOffscreen":false}
            """)!;
        after.Insert(1, dismiss);
        switch (change)
        {
            case "menu-text": popup["children"]![0]!["children"]![0]!["name"] = "Different action"; break;
            case "dismiss-name": dismiss["name"] = "Custom action"; break;
            case "dismiss-state": dismiss["isEnabled"] = false; break;
            case "dismiss-metadata": dismiss["value"] = "Custom"; break;
            case "dismiss-child": dismiss["children"] = JsonNode.Parse("""[{"type":"Button","name":"Extra"}]"""); break;
            case "unknown-menu": popup["children"]![0]!["className"] = "ApplicationMenu"; break;
        }

        foreach (var result in new[] { await CompareAsync(source, target), await CompareAsync(target, source) })
            Assert.AreEqual(matches, result.Matches, change);
    }

    [TestMethod]
    [DataRow("className")]
    [DataRow("automationId")]
    public async Task Compare_OrdinaryApplicationWrappersAreNotAttributedToNavigationView(string missingMarker)
    {
        var (source, target) = NavigationViewTrees();
        var menu = TargetContent(target)[0]!["children"]![1]!["children"]![0]!;
        if (missingMarker == "className")
            menu["children"]![0]!["className"] = "Application.CustomNavigationItem";
        else
            menu["automationId"] = "ApplicationMenu";

        var comparison = await CompareAsync(source, target);

        Assert.IsTrue(comparison.Comparable);
        Assert.IsFalse(comparison.Matches);
        var diagnostic = comparison.Diagnostics.Single(item => item.Kind != "platform-host-normalized");
        Assert.AreEqual("app-structure-mismatch", diagnostic.Kind);
        Assert.AreEqual("application", diagnostic.RepairOwner);
    }

    [TestMethod]
    public async Task Compare_NavigationViewNormalizationDoesNotHideOtherApplicationDifferences()
    {
        var (source, target) = NavigationViewTrees();
        target["windows"]![0]!["elements"]![0]!["name"] = "Wrong application";

        var comparison = await CompareAsync(source, target);

        Assert.IsFalse(comparison.Matches);
        Assert.IsTrue(comparison.Diagnostics.Any(item =>
            item.Kind == "app-property-mismatch" && item.Property == "name" && item.RepairOwner == "application"));
        Assert.IsTrue(comparison.Diagnostics.Any(item => item.Kind == "platform-control-normalized"));
    }

    [TestMethod]
    [DataRow("text")]
    [DataRow("enabled")]
    [DataRow("visibility")]
    [DataRow("hierarchy")]
    [DataRow("order")]
    [DataRow("missing-control")]
    [DataRow("menu-label")]
    [DataRow("menu-order")]
    [DataRow("extra-navigation-item")]
    [DataRow("menu-item-content")]
    [DataRow("footer-content")]
    [DataRow("footer-state")]
    [DataRow("back-state")]
    [DataRow("back-name")]
    [DataRow("generated-label-metadata")]
    [DataRow("generated-label-duplicate")]
    [DataRow("generated-label-state")]
    [DataRow("menu-id")]
    [DataRow("menu-selection")]
    [DataRow("back-label-direction")]
    public async Task Compare_NavigationViewAlignmentExposesContentAndStateDifferences(string change)
    {
        var (source, target) = NavigationViewTrees();
        var before = source["windows"]![0]!["elements"]![0]!["children"]![1]!["children"]!.AsArray();
        var navigation = TargetContent(target)[0]!;
        var after = navigation["children"]!.AsArray();
        var beforeItems = before[1]!["children"]!.AsArray();
        var afterItems = after[1]!["children"]![0]!["children"]!.AsArray();
        var button = after[2]!;
        switch (change)
        {
            case "text": button["name"] = "Discard room"; break;
            case "enabled": button["isEnabled"] = false; break;
            case "visibility": button["isOffscreen"] = true; break;
            case "hierarchy":
                after[2] = new JsonObject { ["type"] = "Pane", ["children"] = new JsonArray(button.DeepClone()) };
                break;
            case "order":
                var first = button.DeepClone();
                after[2] = after[3]!.DeepClone();
                after[3] = first;
                break;
            case "missing-control": after.RemoveAt(2); break;
            case "menu-label": afterItems[0]!["name"] = "Checkout"; break;
            case "menu-order":
                var beforeItem = beforeItems[0]!.DeepClone();
                beforeItem["name"] = "Settings";
                beforeItems.Add(beforeItem);
                var afterItem = afterItems[0]!.DeepClone();
                afterItem["name"] = "Settings";
                afterItem["children"]![0]!["name"] = "Settings";
                afterItems.Insert(0, afterItem);
                break;
            case "extra-navigation-item":
                afterItems.Add(JsonNode.Parse("""
                    {"type":"TabItem","name":"NavigationViewItem","className":"Microsoft.UI.Xaml.Controls.NavigationViewItem"}
                    """));
                break;
            case "menu-item-content":
                beforeItems[0]!["children"] = JsonNode.Parse("""
                    [{"type":"Group","name":"Shop","children":[{"type":"Button","name":"Remove"}]}]
                    """);
                afterItems[0]!["children"] = JsonNode.Parse("""[{"type":"Group","name":"Shop"}]""");
                break;
            case "footer-content":
                after.Insert(2, JsonNode.Parse("""
                    {"type":"Group","automationId":"TopFooterMenuItemsHost","isOffscreen":true,
                     "children":[{"type":"Button","name":"Unexpected command"}]}
                    """));
                break;
            case "footer-state":
                after.Insert(2, JsonNode.Parse("""
                    {"type":"Group","automationId":"TopFooterMenuItemsHost","isEnabled":false,"isOffscreen":true}
                    """));
                break;
            case "back-state": after[0]!["isEnabled"] = true; break;
            case "back-name": after[0]!["name"] = "Return"; break;
            case "generated-label-metadata": afterItems[0]!["children"]![0]!["automationId"] = "AppLabel"; break;
            case "generated-label-duplicate":
                afterItems[0]!["children"]!.AsArray().Add(afterItems[0]!["children"]![0]!.DeepClone());
                break;
            case "generated-label-state": afterItems[0]!["children"]![0]!["isSelected"] = true; break;
            case "menu-id": afterItems[0]!["automationId"] = "DifferentItem"; break;
            case "menu-selection": afterItems[0]!["isSelected"] = true; break;
            case "back-label-direction":
                before[0]!["name"] = "Back";
                after[0]!["name"] = "Back Button";
                break;
        }
        var sourceBytes = source.ToJsonString();
        var targetBytes = target.ToJsonString();

        var comparison = await CompareAsync(source, target);
        var reverse = await CompareAsync(target, source);

        foreach (var result in new[] { comparison, reverse })
        {
            Assert.IsTrue(result.Comparable);
            Assert.IsFalse(result.Matches);
            Assert.IsTrue(result.Diagnostics.Any(item => item.Kind == "platform-control-normalized"));
            var difference = result.Diagnostics.FirstOrDefault(item =>
                item.RepairOwner == (change == "visibility" ? "capture" : "application"));
            Assert.IsNotNull(difference, $"The framework boundary hid the {change} difference.");
            Assert.IsNotNull(difference.SourcePath);
            Assert.IsNotNull(difference.TargetPath);
            Assert.AreEqual("source.json", difference.SourceEvidence);
            Assert.AreEqual("target.json", difference.TargetEvidence);
            Assert.IsFalse(WorkflowCompletion.IsToolBlocked(new("surface", StateVerificationLevel.SurfaceReachable, result.Reason)
            {
                Result = SurfaceVerificationResult.Mismatch,
                ComparisonDiagnostics = result.Diagnostics
            }));
            if (change == "visibility")
            {
                Assert.AreEqual("input-comparability-review", difference.Kind);
                StringAssert.Contains(difference.SuggestedAction, "client viewport");
            }
        }
        Assert.AreEqual(sourceBytes, source.ToJsonString());
        Assert.AreEqual(targetBytes, target.ToJsonString());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Compare_NavigationViewOnlyNormalizesEmptyUnannotatedFrameworkFooter(bool reverse)
    {
        var (source, target) = NavigationViewTrees();
        TargetContent(target)[0]!["children"]!.AsArray().Insert(2, JsonNode.Parse("""
            {"type":"Group","automationId":"TopFooterMenuItemsHost","isOffscreen":true}
            """));
        if (reverse)
            (source, target) = (target, source);

        var comparison = await CompareAsync(source, target);

        Assert.IsTrue(comparison.Matches);
        Assert.HasCount(1, comparison.Diagnostics.Where(item => item.Kind != "platform-host-normalized"));
        Assert.AreEqual("platform-control-normalized", comparison.Diagnostics[^1].Kind);
    }

    [TestMethod]
    [DataRow("navigation-name")]
    [DataRow("navigation-class")]
    [DataRow("navigation-state")]
    [DataRow("scroll-id")]
    [DataRow("scroll-name")]
    [DataRow("scroll-state")]
    [DataRow("menu-custom-peer")]
    [DataRow("legacy-order")]
    public async Task Compare_UnknownNavigationViewShapesRemainBlocked(string change)
    {
        var (source, target) = NavigationViewTrees();
        var navigation = TargetContent(target)[0]!;
        var scroll = navigation["children"]![1]!;
        switch (change)
        {
            case "navigation-name": navigation["name"] = "Application navigation"; break;
            case "navigation-class": navigation["className"] = "Application.Navigation"; break;
            case "navigation-state": navigation["isEnabled"] = false; break;
            case "scroll-id": scroll["automationId"] = "ApplicationScroll"; break;
            case "scroll-name": scroll["name"] = "Application scroll"; break;
            case "scroll-state": scroll["isOffscreen"] = true; break;
            case "menu-custom-peer":
                scroll["children"]![0]!["children"]!.AsArray().Add(JsonNode.Parse("""
                    {"type":"TabItem","name":"Custom command","className":"Application.CustomNavigationItem"}
                    """));
                break;
            case "legacy-order":
                var before = source["windows"]![0]!["elements"]![0]!["children"]![1]!["children"]!.AsArray();
                var back = before[0]!.DeepClone();
                before[0] = before[1]!.DeepClone();
                before[1] = back;
                break;
        }

        foreach (var result in new[] { await CompareAsync(source, target), await CompareAsync(target, source) })
        {
            Assert.IsFalse(result.Comparable);
            Assert.IsFalse(result.Matches);
            Assert.IsFalse(result.Diagnostics.Any(item => item.Kind == "platform-control-normalized"));
            var diagnostic = result.Diagnostics.Single(item => item.Kind == "platform-control-review");
            StringAssert.Contains(diagnostic.SuggestedAction, "descendant comparison is incomplete");
        }
    }

    [TestMethod]
    public async Task Compare_UnsupportedDescendantsDoNotEraseConfirmedApplicationDifferences()
    {
        var (source, target) = NavigationViewTrees();
        TargetContent(target)[0]!["children"]![1]!["automationId"] = "UnrecognizedScroll";
        target["windows"]![0]!["elements"]![0]!["name"] = "Changed application name";

        var result = await CompareAsync(source, target);

        Assert.IsFalse(result.Comparable);
        Assert.IsFalse(result.Matches);
        Assert.IsTrue(result.HasApplicationDifferences);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Kind == "platform-control-review"));
        Assert.IsTrue(result.Diagnostics.Any(item => item.Kind == "app-property-mismatch"));
    }

    [TestMethod]
    [DataRow("Windows.UI.Core.CoreWindow")]
    [DataRow("WinUIDesktopWin32WindowClass")]
    [DataRow("Application.CustomRoot")]
    public async Task Compare_NavigationViewRoleNormalizationCannotHideSamePlatformOrOrdinaryTreeChanges(string hostClass)
    {
        var (legacy, modern) = NavigationViewTrees();
        var sourceRoot = legacy["windows"]![0]!["elements"]![0]!["children"]![1]!.DeepClone();
        var targetRoot = sourceRoot.DeepClone();
        sourceRoot["className"] = hostClass;
        targetRoot["className"] = hostClass;
        targetRoot["children"] = TargetContent(modern).DeepClone();
        var source = new JsonObject { ["elements"] = new JsonArray(sourceRoot) };
        var target = new JsonObject { ["elements"] = new JsonArray(targetRoot) };

        foreach (var result in new[] { await CompareAsync(source, target), await CompareAsync(target, source) })
        {
            Assert.IsFalse(result.Matches);
            Assert.IsTrue(result.Diagnostics.Any(item => item.RepairOwner == "application"));
            Assert.IsFalse(result.Diagnostics.Any(item => item.Kind == "platform-control-normalized"));
        }
    }

    [TestMethod]
    public async Task Compare_NavigationViewNormalizationDoesNotConsumeTheDifferenceBudget()
    {
        var (source, target) = NavigationViewTrees();
        var before = source["windows"]![0]!["elements"]![0]!["children"]![1]!["children"]!.AsArray();
        var after = TargetContent(target)[0]!["children"]!.AsArray();
        for (var index = 0; index < 12; index++)
        {
            before.Add(new JsonObject { ["type"] = "Text", ["name"] = $"Original {index}" });
            after.Add(new JsonObject { ["type"] = "Text", ["name"] = $"Changed {index}" });
        }

        var comparison = await CompareAsync(source, target);

        Assert.IsFalse(comparison.Matches);
        Assert.HasCount(8, comparison.Diagnostics.Where(item => !item.IsNormalization));
    }

    [TestMethod]
    [DataRow("same", true)]
    [DataRow("selection", false)]
    [DataRow("annotation", false)]
    [DataRow("visibility", false)]
    [DataRow("missing-field", false)]
    [DataRow("hex-value", false)]
    [DataRow("extra-peer", false)]
    [DataRow("label-state", false)]
    public async Task Compare_CollapsedColorModelRequiresCompleteVisibleRgbSelectionEvidence(string change, bool matches)
    {
        var (source, target) = NativeTrees();
        var picker = JsonNode.Parse("""
            {"type":"Pane","className":"Flyout","children":[
              {"type":"Slider","automationId":"ColorSpectrum","className":"Windows.UI.Xaml.Controls.Primitives.ColorSpectrum",
               "value":"Purple, Hue 300, Saturation 100, Brightness 50"},
              {"type":"Slider","automationId":"ThirdDimensionSlider","className":"Slider","name":"Brightness","value":"50, Purple"},
              {"type":"ComboBox","automationId":"ColorRepresentationComboBox","className":"ComboBox","expandState":"collapsed",
               "isEnabled":true,"isOffscreen":false,"children":[
                 {"type":"ListItem","automationId":"RGBComboBoxItem","name":"RGB","className":"ComboBoxItem",
                  "isEnabled":true,"isOffscreen":false,"children":[
                    {"type":"Text","name":"RGB","className":"TextBlock","isEnabled":true,"isOffscreen":false}]}]},
              {"type":"Edit","automationId":"RedTextBox","className":"TextBox","value":"128","isOffscreen":false},
              {"type":"Text","automationId":"RedLabel","className":"TextBlock","name":"Red","isOffscreen":false},
              {"type":"Edit","automationId":"GreenTextBox","className":"TextBox","value":"0","isOffscreen":false},
              {"type":"Text","automationId":"GreenLabel","className":"TextBlock","name":"Green","isOffscreen":false},
              {"type":"Edit","automationId":"BlueTextBox","className":"TextBox","value":"128","isOffscreen":false},
              {"type":"Text","automationId":"BlueLabel","className":"TextBlock","name":"Blue","isOffscreen":false},
              {"type":"Edit","automationId":"HexTextBox","className":"TextBox","value":"#800080","isOffscreen":false}]}
            """)!;
        var before = source["windows"]![0]!["elements"]![0]!["children"]![1]!["children"]!.AsArray();
        before.Clear();
        before.Add(picker.DeepClone());
        var selected = before[0]!["children"]![2]!["children"]![0]!;
        var after = picker["children"]!.AsArray();
        after[0]!["className"] = "Microsoft.UI.Xaml.Controls.Primitives.ColorSpectrum";
        after[0]!["value"] = "Purple, Hue 300, Saturation 100, Value 50";
        after[1]!["name"] = "Value (Brightness)";
        after[2]!.AsObject().Remove("children");
        TargetContent(target).Clear();
        TargetContent(target).Add(picker);
        switch (change)
        {
            case "selection": selected["name"] = "HSV"; break;
            case "annotation": selected["value"] = "Application annotation"; break;
            case "visibility": after[3]!["isOffscreen"] = true; break;
            case "missing-field": after.RemoveAt(3); break;
            case "hex-value": after[9]!["value"] = "#810080"; break;
            case "extra-peer": selected["children"]!.AsArray().Add(new JsonObject { ["type"] = "Button", ["name"] = "Extra" }); break;
            case "label-state": selected["children"]![0]!["isEnabled"] = false; break;
        }
        var sourceBytes = source.ToJsonString();
        var targetBytes = target.ToJsonString();

        foreach (var result in new[] { await CompareAsync(source, target), await CompareAsync(target, source) })
        {
            Assert.AreEqual(matches, result.Matches, change);
            if (change is "selection" or "annotation" or "extra-peer" or "label-state")
                Assert.IsTrue(result.Diagnostics.Any(item => item.Kind == "platform-control-review"));
        }
        Assert.AreEqual(sourceBytes, source.ToJsonString());
        Assert.AreEqual(targetBytes, target.ToJsonString());
    }

    internal static (JsonNode Source, JsonNode Target) NavigationViewTrees()
    {
        var (source, target) = NativeTrees();
        source["windows"]![0]!["elements"]![0]!["children"]![1]!["children"] = JsonNode.Parse("""
            [
              {"type":"Button","automationId":"NavigationViewBackButton","name":"Back Button","isEnabled":false},
              {"type":"List","automationId":"TopNavMenuItemsHost","className":"ListView","children":[
                {"type":"ListItem","name":"Shop","className":"ListViewItem"}]},
              {"type":"Button","automationId":"add-room","name":"Add room"},
              {"type":"Text","name":"Ready"}
            ]
            """);
        var content = TargetContent(target);
        content.Clear();
        content.Add(JsonNode.Parse("""
            {"type":"Custom","automationId":"ApplicationNavigation","children":[
              {"type":"Button","automationId":"NavigationViewBackButton","name":"Back","isEnabled":false},
              {"type":"Pane","className":"ScrollViewer","children":[
                {"type":"Group","automationId":"TopNavMenuItemsHost","children":[
                  {"type":"TabItem","name":"Shop","className":"Microsoft.UI.Xaml.Controls.NavigationViewItem","children":[
                    {"type":"Text","name":"Shop","className":"TextBlock"}]}]}]},
              {"type":"Button","automationId":"add-room","name":"Add room"},
              {"type":"Text","name":"Ready"}
            ]}
            """));
        return (source, target);
    }

    [TestMethod]
    [DataRow("same", true)]
    [DataRow("unchanged-resources", true)]
    [DataRow("hue", false)]
    [DataRow("saturation", false)]
    [DataRow("brightness", false)]
    [DataRow("description", false)]
    [DataRow("slider-value", false)]
    [DataRow("hex-value", false)]
    [DataRow("enabled", false)]
    [DataRow("extra-control", false)]
    [DataRow("unknown-provider", false)]
    [DataRow("unknown-parent", false)]
    [DataRow("same-platform", false)]
    public async Task Compare_ColorPickerOnlyAlignsKnownResourceWordsAndPreservesAllValues(string change, bool matches)
    {
        var (source, target) = NativeTrees();
        var picker = JsonNode.Parse("""
            {"type":"Pane","className":"Flyout","children":[
              {"type":"Slider","automationId":"ColorSpectrum","name":"Color picker",
               "className":"Windows.UI.Xaml.Controls.Primitives.ColorSpectrum",
               "value":"Dark purple, Hue 300, Saturation 100, Brightness 50","isEnabled":true},
              {"type":"Slider","automationId":"ThirdDimensionSlider","className":"Slider",
               "name":"Brightness","value":"50, Dark purple"},
              {"type":"Edit","automationId":"HexTextBox","className":"TextBox","value":"#800080"}]}
            """)!;
        var before = source["windows"]![0]!["elements"]![0]!["children"]![1]!["children"]!.AsArray();
        before.Clear();
        before.Add(picker.DeepClone());
        var content = TargetContent(target);
        content.Clear();
        content.Add(picker);
        var after = picker["children"]!.AsArray();
        after[0]!["className"] = "Microsoft.UI.Xaml.Controls.Primitives.ColorSpectrum";
        after[0]!["value"] = "Dark purple, Hue 300, Saturation 100, Value 50";
        after[1]!["name"] = "Value (Brightness)";
        switch (change)
        {
            case "unchanged-resources":
                after[0]!["value"] = "Dark purple, Hue 300, Saturation 100, Brightness 50";
                after[1]!["name"] = "Brightness";
                break;
            case "hue": after[0]!["value"] = "Dark purple, Hue 301, Saturation 100, Value 50"; break;
            case "saturation": after[0]!["value"] = "Dark purple, Hue 300, Saturation 99, Value 50"; break;
            case "brightness": after[0]!["value"] = "Dark purple, Hue 300, Saturation 100, Value 51"; break;
            case "description": after[0]!["value"] = "Purple, Hue 300, Saturation 100, Value 50"; break;
            case "slider-value": after[1]!["value"] = "51, Dark purple"; break;
            case "hex-value": after[2]!["value"] = "#810080"; break;
            case "enabled": after[0]!["isEnabled"] = false; break;
            case "extra-control": after.Add(new JsonObject { ["type"] = "Button", ["name"] = "Extra" }); break;
            case "unknown-provider": after[0]!["className"] = "Application.ColorSpectrum"; break;
            case "unknown-parent": picker["className"] = "Application.Flyout"; break;
            case "same-platform":
                var legacyPicker = before[0]!.DeepClone();
                source = target.DeepClone();
                TargetContent(source)[0] = legacyPicker;
                break;
        }
        var sourceBytes = source.ToJsonString();
        var targetBytes = target.ToJsonString();

        foreach (var result in new[] { await CompareAsync(source, target), await CompareAsync(target, source) })
            Assert.AreEqual(matches, result.Matches, change);

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
