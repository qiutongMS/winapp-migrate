// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace UwpMigration.Mechanical.Tests;

public partial class MigrateCommandTests
{
    [TestMethod]
    public async Task Migrate_TodoDagIncludesStructuredPriorityGuidanceAndWaves()
    {
        var source = await CreateUwpSourceAsync("TodoDagApp");
        await WriteAsync(source, "App.xaml", """
            <Application>
              <Application.Resources>
                <ResourceDictionary>
                  <ResourceDictionary.MergedDictionaries>
                    <ResourceDictionary Source="Styles.xaml" />
                  </ResourceDictionary.MergedDictionaries>
                </ResourceDictionary>
              </Application.Resources>
            </Application>
            """);
        await WriteAsync(source, "App.xaml.cs", """
            namespace TodoDagApp;
            public sealed partial class App
            {
                void OnLaunched() { }
            }
            """);
        await WriteAsync(source, "Styles.xaml", "<ResourceDictionary />");
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "todo-dag-output"));
        ArrangeTemplateCreation(target, "TodoDagApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "TODO graph: valid");
        StringAssert.Contains(output, "Wave 1: UWMIG001");
        StringAssert.Contains(output, "Wave 2: UWMIG002");
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        Assert.AreEqual(
            "1.6",
            report.RootElement.GetProperty("schemaVersion").GetString());
        var todos = report.RootElement.GetProperty("todos")
            .EnumerateArray()
            .ToDictionary(todo => todo.GetProperty("id").GetString()!);
        foreach (var id in new[] { "UWMIG001", "UWMIG002", "UWMIG003" })
        {
            var todo = todos[id];
            var guidance = todo.GetProperty("guidance");
            Assert.IsFalse(
                guidance.GetProperty("candidateStrategiesExhaustive")
                    .GetBoolean());
            Assert.IsTrue(
                guidance.GetProperty("facts").GetArrayLength() > 0);
            Assert.IsTrue(
                guidance.GetProperty("candidateStrategies")
                    .EnumerateArray()
                    .All(strategy =>
                        strategy.GetProperty("preconditions")
                            .GetArrayLength() > 0
                        && strategy.GetProperty("risks")
                            .GetArrayLength() > 0));
            Assert.IsTrue(
                guidance.GetProperty("completionChecks")
                    .GetArrayLength() > 0);
            Assert.IsTrue(
                todo.GetProperty("touchSet")
                    .GetProperty("conflictKeys")
                    .GetArrayLength() > 0);
            Assert.IsTrue(
                todo.GetProperty("affectedFeatures")
                    .GetArrayLength() > 0);
        }
        CollectionAssert.AreEqual(
            new[] { "UWMIG003" },
            todos["UWMIG001"].GetProperty("dependsOn")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { "UWMIG001" },
            todos["UWMIG002"].GetProperty("dependsOn")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray());
        var graph = report.RootElement.GetProperty("todoGraph");
        Assert.AreEqual("valid", graph.GetProperty("status").GetString());
        Assert.IsTrue(graph.GetProperty("executionWaves")
            .EnumerateArray()
            .Any(wave =>
                wave.GetProperty("index").GetInt32() == 0
                && wave.GetProperty("todoIds")
                    .EnumerateArray()
                    .Any(id => id.GetString() == "UWMIG003")));
    }

    [TestMethod]
    public async Task Verify_TodoDagOutputAndStatusesRemainStable()
    {
        var source = await CreateUwpSourceAsync("VerifyTodoDagApp");
        await WriteAsync(source, "App.xaml", "<Application />");
        await WriteAsync(
            source,
            "App.xaml.cs",
            "namespace VerifyTodoDagApp; public sealed partial class App { }");
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "verify-todo-dag-output"));
        ArrangeTemplateCreation(target, "VerifyTodoDagApp");
        var (migrateExit, migrateOutput) = await InvokeAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);

        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var report = await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken);
        report = report.Replace(
            "\"status\": \"pending\"",
            "\"status\": \"accepted\"",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            reportPath,
            report,
            TestContext.CancellationToken);

        var (verifyExit, verifyOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(0, verifyExit, verifyOutput);
        StringAssert.Contains(verifyOutput, "TODO graph: valid");
        using var verified = JsonDocument.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken));
        Assert.AreEqual(
            "accepted",
            verified.RootElement.GetProperty("todos")
                .EnumerateArray()
                .Single(todo =>
                    todo.GetProperty("id").GetString() == "UWMIG001")
                .GetProperty("status")
                .GetString());
    }
}
