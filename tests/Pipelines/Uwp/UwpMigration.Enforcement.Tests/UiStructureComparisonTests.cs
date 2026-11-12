// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

[TestClass]
public sealed class UiStructureComparisonTests
{
    [TestMethod]
    public void Compare_ExplicitHostWrappersPreserveSemanticParentage()
    {
        var source = Tree("""{"type":"Window","name":"","isEnabled":true,"isOffscreen":false,"children":[{"type":"Button","name":"Open","isEnabled":true,"isOffscreen":false}]}""");
        var target = Tree("""{"type":"Window","name":"","isEnabled":true,"isOffscreen":false,"children":[{"type":"Pane","name":"Desktop host","isEnabled":true,"isOffscreen":false,"children":[{"type":"Button","name":"Open","isEnabled":true,"isOffscreen":false}]}]}""");

        var result = UiStructureComparison.Compare(source, target,
            [new("target", "0.0", "wrapper", "WinUI desktop host only; children remain compared.")]);

        Assert.AreEqual("passed", result.Status);
        Assert.HasCount(0, result.Differences);
    }

    [TestMethod]
    public void Compare_SameFlatNamesDoNotHideDifferentHierarchy()
    {
        var source = Tree("""{"type":"Pane","name":"","isEnabled":true,"isOffscreen":false,"children":[{"type":"Group","name":"","isEnabled":true,"isOffscreen":false,"children":[{"type":"Button","name":"Open","isEnabled":true,"isOffscreen":false}]},{"type":"Button","name":"Close","isEnabled":true,"isOffscreen":false}]}""");
        var target = Tree("""{"type":"Pane","name":"","isEnabled":true,"isOffscreen":false,"children":[{"type":"Group","name":"","isEnabled":true,"isOffscreen":false},{"type":"Button","name":"Open","isEnabled":true,"isOffscreen":false},{"type":"Button","name":"Close","isEnabled":true,"isOffscreen":false}]}""");

        var result = UiStructureComparison.Compare(source, target, []);

        Assert.AreEqual("failed", result.Status);
        Assert.IsTrue(result.Differences.Any(issue => issue.Contains("parent", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("type", "\"TextBox\"")]
    [DataRow("name", "\"Save\"")]
    [DataRow("isEnabled", "false")]
    [DataRow("isOffscreen", "true")]
    [DataRow("value", "\"changed\"")]
    public void Compare_DetectsObservablePropertyMismatch(string property, string replacement)
    {
        const string node = """{"type":"Button","name":"Open","isEnabled":true,"isOffscreen":false,"value":"original"}""";
        var json = System.Text.Json.Nodes.JsonNode.Parse(node)!;
        json[property] = System.Text.Json.Nodes.JsonNode.Parse(replacement);
        var result = UiStructureComparison.Compare(Tree(node), Tree(json.ToJsonString()), []);
        Assert.AreEqual("failed", result.Status);
    }

    [TestMethod]
    public void Compare_MissingPropertiesAreUnknownNotEqual()
    {
        var tree = Tree("""{"type":"Button","name":"Open"}""");
        Assert.AreEqual("unknown", UiStructureComparison.Compare(tree, tree, []).Status);
    }

    [TestMethod]
    public void Read_MissingNameCannotMasqueradeAsAnObservedEmptyName()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Tree("""{"type":"Button","isEnabled":true,"isOffscreen":false}"""));
    }

    [TestMethod]
    public void Compare_NameNormalizationIsScopedAndCannotHideOtherProperties()
    {
        var left = Tree("""{"type":"Text","name":"12:00","isEnabled":true,"isOffscreen":false}""");
        var right = Tree("""{"type":"Text","name":"12:01","isEnabled":false,"isOffscreen":false}""");
        var result = UiStructureComparison.Compare(left, right,
            [new("source", "0", "name", "Sample timestamp is generated at launch.")]);
        Assert.AreEqual("failed", result.Status);
        Assert.HasCount(1, result.Differences);
        StringAssert.Contains(result.Differences[0], "isEnabled");
    }

    [TestMethod]
    public void Read_TruncatedAndCallerVerdictTreesAreRejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Tree("""{"type":"Pane","hasMoreChildren":true}"""));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            UiStructureComparison.Read(JsonSerializer.Deserialize<JsonElement>("""{"passed":true}""")));
    }

    [TestMethod]
    public void Compare_ControlsCannotBeRemovedAsHostWrappers()
    {
        var tree = Tree("""{"type":"Button","name":"Open","isEnabled":true,"isOffscreen":false}""");
        Assert.ThrowsExactly<InvalidDataException>(() => UiStructureComparison.Compare(tree, tree,
            [new("target", "0", "wrapper", "Hide an application difference.")]));
    }

    [TestMethod]
    public void Compare_ExplicitNativeCaptionChromeDoesNotHideApplicationContent()
    {
        var left = Tree("""{"type":"Window","name":"","isEnabled":true,"isOffscreen":false,"children":[{"type":"Button","name":"Open","isEnabled":true,"isOffscreen":false}]}""");
        var right = Tree("""{"type":"Window","name":"","isEnabled":true,"isOffscreen":false,"children":[{"type":"Pane","name":"","className":"ReunionWindowingCaptionControls","children":[{"type":"Button","name":"Minimize"}]},{"type":"Button","name":"Open","isEnabled":true,"isOffscreen":false}]}""");
        var result = UiStructureComparison.Compare(left, right,
            [new("target", "0.0", "host-chrome", "Native caption buttons are not application content.")]);
        Assert.AreEqual("passed", result.Status);
    }

    [TestMethod]
    [DataRow("--hide-disabled")]
    [DataRow("--interactive")]
    [DataRow("only-one-button")]
    public void Evidence_FilteredInspectCannotClaimWholeApplicationStructure(string filter)
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            UiStructureComparison.ReadCapture(JsonSerializer.Deserialize<JsonElement>(
                """{"windows":[{"hwnd":42,"elements":[]}]}"""),
                ["winapp", "ui", "inspect", filter, "-w", "42", "--json"], 42));
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("""{"windows":[{"hwnd":43,"elements":[]}]}""")]
    public void ReadCapture_MalformedOrWrongWindowOutputCannotBecomeStructureEvidence(string json)
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            UiStructureComparison.ReadCapture(JsonSerializer.Deserialize<JsonElement>(json),
                ["winapp", "ui", "inspect", "-w", "42", "--json"], 42));
    }

    private static IReadOnlyList<UiStructureNode> Tree(string node) =>
        UiStructureComparison.Read(JsonSerializer.Deserialize<JsonElement>(
            $$"""{"windows":[{"elements":[{{node}}]}]}"""));
}
