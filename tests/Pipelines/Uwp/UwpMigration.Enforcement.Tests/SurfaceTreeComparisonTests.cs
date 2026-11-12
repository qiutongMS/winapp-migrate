// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

[TestClass]
public sealed class SurfaceTreeComparisonTests
{
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
