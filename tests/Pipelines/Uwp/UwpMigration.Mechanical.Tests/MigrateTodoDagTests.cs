// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace UwpMigration.Mechanical.Tests;

public partial class MigrateCommandTests
{
    [TestMethod]
    public async Task Migrate_TodoDagIncludesStructuredPriorityGuidanceAndWaves()
    {
        var source = await CreateUwpSourceAsync("TodoDagApp");
        var shared = _tempDirectory.CreateSubdirectory("TodoDagShared");
        await WriteAsync(shared, "TodoDagShared.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        await WriteAsync(source, "TodoDagApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>uap10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Legacy.Todo" Version="2.4.6" />
                <ProjectReference Include="..\TodoDagShared\TodoDagShared.csproj" />
              </ItemGroup>
            </Project>
            """);
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
        StringAssert.Contains(
            output,
            "Preparation wave 0: UWMIG001:prepare-startup-resources, " +
            "UWMIG002:prepare-manifest-declarations, " +
            "UWMIG003:prepare-dependencies");
        StringAssert.Contains(
            output,
            "Integration wave 2: UWMIG002:integrate-activation-routing");
        StringAssert.Contains(
            output,
            "Verification wave 3: UWMIG003:verify-integrated-target");
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
        Assert.AreEqual(
            2,
            todos["UWMIG001"].GetProperty("workSlices").GetArrayLength());
        Assert.AreEqual(
            3,
            todos["UWMIG002"].GetProperty("workSlices").GetArrayLength());
        Assert.AreEqual(
            3,
            todos["UWMIG003"].GetProperty("workSlices").GetArrayLength());
        var dependencyFacts = todos["UWMIG003"]
            .GetProperty("guidance")
            .GetProperty("facts")
            .EnumerateArray()
            .Select(fact => fact.GetProperty("summary").GetString()!)
            .ToArray();
        Assert.IsTrue(dependencyFacts.Any(summary =>
            summary.Contains(
                "Legacy.Todo",
                StringComparison.Ordinal)
            && summary.Contains(
                "review-required",
                StringComparison.Ordinal)));
        Assert.IsTrue(dependencyFacts.Any(summary =>
            summary.Contains(
                @"..\TodoDagShared\TodoDagShared.csproj",
                StringComparison.Ordinal)
            && summary.Contains(
                "resolved",
                StringComparison.Ordinal)));
        var graph = report.RootElement.GetProperty("todoGraph");
        Assert.AreEqual("valid", graph.GetProperty("status").GetString());
        var waves = graph.GetProperty("executionWaves")
            .EnumerateArray()
            .ToArray();
        Assert.AreEqual(4, waves.Length);
        AssertWave(
            waves[0],
            "preparation",
            "UWMIG001:prepare-startup-resources",
            "UWMIG002:prepare-manifest-declarations",
            "UWMIG003:prepare-dependencies");
        AssertWave(
            waves[1],
            "integration",
            "UWMIG001:integrate-startup-resources",
            "UWMIG002:integrate-manifest-declarations",
            "UWMIG003:integrate-dependencies");
        AssertWave(
            waves[2],
            "integration",
            "UWMIG002:integrate-activation-routing");
        AssertWave(
            waves[3],
            "verification",
            "UWMIG003:verify-integrated-target");
        Assert.AreEqual(
            1,
            waves.Count(wave =>
                wave.GetProperty("stage").GetString() == "verification"));
        var workItems = graph.GetProperty("workItems")
            .EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetString()!);
        Assert.IsTrue(workItems[
            "UWMIG001:integrate-startup-resources"]
            .GetProperty("touchSet")
            .GetProperty("paths")
            .EnumerateArray()
            .Any(path => path.GetString() == "App.xaml.cs"));
        Assert.IsTrue(workItems[
            "UWMIG002:integrate-activation-routing"]
            .GetProperty("touchSet")
            .GetProperty("paths")
            .EnumerateArray()
            .Any(path => path.GetString() == "App.xaml.cs"));
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
        var report = JsonNode.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken))!;
        foreach (var todo in report["todos"]!.AsArray())
        {
            todo!["status"] = "accepted";
            if (todo["id"]!.GetValue<string>() is
                "UWMIG001" or "UWMIG002" or "UWMIG003")
            {
                todo["workSlices"] = new JsonArray();
            }
        }
        var todos = report["todos"]!.AsArray()
            .ToDictionary(todo => todo!["id"]!.GetValue<string>());
        todos["UWMIG001"]!["dependsOn"] =
            new JsonArray("UWMIG003");
        todos["UWMIG002"]!["dependsOn"] =
            new JsonArray("UWMIG001");
        report["todoGraph"] = new JsonObject
        {
            ["status"] = "valid",
            ["edges"] = new JsonArray
            {
                new JsonObject
                {
                    ["prerequisiteId"] = "UWMIG003",
                    ["dependentId"] = "UWMIG001"
                }
            },
            ["executionWaves"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["todoIds"] = new JsonArray("UWMIG003")
                }
            },
            ["issues"] = new JsonArray()
        };
        await File.WriteAllTextAsync(
            reportPath,
            report.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }),
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
        var graph = verified.RootElement.GetProperty("todoGraph");
        var refreshedPreparation = graph.GetProperty("executionWaves")[0];
        Assert.AreEqual(
            "preparation",
            refreshedPreparation.GetProperty("stage").GetString());
        CollectionAssert.IsSubsetOf(
            new[]
            {
                "UWMIG001:prepare-startup-resources",
                "UWMIG002:prepare-manifest-declarations",
                "UWMIG003:prepare-dependencies"
            },
            refreshedPreparation.GetProperty("workItemIds")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray());
    }

    [TestMethod]
    public async Task Migrate_ManifestOnlySourceStillGetsIntegratedVerification()
    {
        var source = _tempDirectory.CreateSubdirectory(
            "ManifestOnlyTodoDagApp");
        await WriteAsync(source, "Package.appxmanifest", """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Applications><Application /></Applications>
            </Package>
            """);
        await WriteAsync(source, "App.xaml", "<Application />");
        await WriteAsync(
            source,
            "App.xaml.cs",
            "public sealed partial class App { }");
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "manifest-only-todo-dag-output"));
        ArrangeTemplateCreation(target, "ManifestOnlyTodoDagApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        Assert.IsFalse(report.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Any(todo => todo.GetProperty("id").GetString() == "UWMIG003"));
        var verificationWave = report.RootElement
            .GetProperty("todoGraph")
            .GetProperty("executionWaves")
            .EnumerateArray()
            .Single(wave =>
                wave.GetProperty("stage").GetString() == "verification");
        AssertWave(
            verificationWave,
            "verification",
            "UWMIG002:verify-integrated-target");
    }

    [TestMethod]
    public async Task Verify_InvalidLegacySchema16DependencyEmitsNoUnsafeWaves()
    {
        var source = await CreateUwpSourceAsync(
            "InvalidLegacyTodoDagApp");
        await WriteAsync(source, "App.xaml", "<Application />");
        await WriteAsync(
            source,
            "App.xaml.cs",
            "public sealed partial class App { }");
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "invalid-legacy-todo-dag-output"));
        ArrangeTemplateCreation(target, "InvalidLegacyTodoDagApp");
        var (migrateExit, migrateOutput) = await InvokeAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);

        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken))!;
        var startupTodo = report["todos"]!.AsArray()
            .Single(todo =>
                todo!["id"]!.GetValue<string>() == "UWMIG001")!;
        startupTodo["workSlices"] = new JsonArray();
        startupTodo["dependsOn"] = new JsonArray("UWMIG999");
        report["todoGraph"] = new JsonObject
        {
            ["status"] = "valid",
            ["edges"] = new JsonArray(),
            ["executionWaves"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["todoIds"] = new JsonArray("UWMIG001")
                }
            },
            ["issues"] = new JsonArray()
        };
        await File.WriteAllTextAsync(
            reportPath,
            report.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }),
            TestContext.CancellationToken);

        var (verifyExit, verifyOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(1, verifyExit, verifyOutput);
        StringAssert.Contains(verifyOutput, "TODO graph: invalid");
        Assert.DoesNotContain("Preparation wave", verifyOutput);
        using var verified = JsonDocument.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken));
        var graph = verified.RootElement.GetProperty("todoGraph");
        Assert.AreEqual("invalid", graph.GetProperty("status").GetString());
        Assert.AreEqual(
            0,
            graph.GetProperty("executionWaves").GetArrayLength());
        Assert.IsTrue(graph.GetProperty("issues")
            .EnumerateArray()
            .Any(issue =>
                issue.GetProperty("code").GetString()
                    == "unknown-dependency"
                && issue.GetProperty("dependencyId").GetString()
                    == "UWMIG999"));
        CollectionAssert.AreEqual(
            new[] { "UWMIG999" },
            verified.RootElement.GetProperty("todos")
                .EnumerateArray()
                .Single(todo =>
                    todo.GetProperty("id").GetString() == "UWMIG001")
                .GetProperty("dependsOn")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray());
    }

    private static void AssertWave(
        JsonElement wave,
        string stage,
        params string[] expectedWorkItems)
    {
        Assert.AreEqual(stage, wave.GetProperty("stage").GetString());
        CollectionAssert.AreEqual(
            expectedWorkItems,
            wave.GetProperty("workItemIds")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray());
    }
}
