// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace UwpMigration.Mechanical.Tests;

public partial class MigrateCommandTests
{
    [TestMethod]
    public async Task Migrate_TodosIncludeStructuredExecutionContract()
    {
        var source = await CreateUwpSourceAsync("TodoContractApp");
        var shared = _tempDirectory.CreateSubdirectory("TodoContractShared");
        await WriteAsync(shared, "TodoContractShared.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        await WriteAsync(source, "TodoContractApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>uap10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Legacy.Todo" Version="2.4.6" />
                <ProjectReference Include="..\TodoContractShared\TodoContractShared.csproj" />
              </ItemGroup>
            </Project>
            """);
        await WriteAsync(source, "App.xaml", "<Application />");
        await WriteAsync(
            source,
            "App.xaml.cs",
            "namespace TodoContractApp; public sealed partial class App { }");
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "todo-contract-output"));
        ArrangeTemplateCreation(target, "TodoContractApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "TODO contract:");
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        Assert.AreEqual(
            "1.6",
            report.RootElement.GetProperty("schemaVersion").GetString());
        Assert.IsFalse(report.RootElement.TryGetProperty("todoGraph", out _));
        var todos = report.RootElement.GetProperty("todos")
            .EnumerateArray()
            .ToDictionary(todo => todo.GetProperty("id").GetString()!);
        foreach (var id in new[] { "UWMIG001", "UWMIG002", "UWMIG003" })
        {
            var todo = todos[id];
            Assert.IsFalse(todo.TryGetProperty("workSlices", out _));
            Assert.IsTrue(todo.GetProperty("touchSet")
                .GetProperty("conflictKeys").GetArrayLength() > 0);
            Assert.IsTrue(todo.GetProperty("affectedFeatures")
                .GetArrayLength() > 0);
            var guidance = todo.GetProperty("guidance");
            Assert.IsTrue(guidance.GetProperty("facts").GetArrayLength() > 0);
            Assert.IsTrue(guidance.GetProperty("completionChecks")
                .GetArrayLength() > 0);
            Assert.IsTrue(guidance.GetProperty("candidateStrategies")
                .EnumerateArray()
                .All(strategy =>
                    strategy.GetProperty("preconditions").GetArrayLength() > 0
                    && strategy.GetProperty("risks").GetArrayLength() > 0));
        }

        var dependencyFacts = todos["UWMIG003"]
            .GetProperty("guidance")
            .GetProperty("facts")
            .EnumerateArray()
            .Select(fact => fact.GetProperty("summary").GetString()!)
            .ToArray();
        Assert.IsTrue(dependencyFacts.Any(summary =>
            summary.Contains("Legacy.Todo", StringComparison.Ordinal)
            && summary.Contains("review-required", StringComparison.Ordinal)));
        Assert.IsTrue(dependencyFacts.Any(summary =>
            summary.Contains(
                @"..\TodoContractShared\TodoContractShared.csproj",
                StringComparison.Ordinal)
            && summary.Contains("resolved", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Verify_LegacyDagFieldsAreIgnoredAndRemoved()
    {
        var source = await CreateUwpSourceAsync("LegacyTodoContractApp");
        await WriteAsync(source, "App.xaml", "<Application />");
        await WriteAsync(
            source,
            "App.xaml.cs",
            "namespace LegacyTodoContractApp; public sealed partial class App { }");
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "legacy-todo-contract-output"));
        ArrangeTemplateCreation(target, "LegacyTodoContractApp");
        var (migrateExit, migrateOutput) = await InvokeAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);

        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken))!;
        var todos = report["todos"]!.AsArray()
            .ToDictionary(todo => todo!["id"]!.GetValue<string>());
        todos["UWMIG001"]!["status"] = "accepted";
        todos["UWMIG001"]!["dependsOn"] = new JsonArray("UWMIG003");
        todos["UWMIG001"]!["workSlices"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "UWMIG001:legacy-work",
                ["stage"] = "preparation"
            }
        };
        report["todoGraph"] = new JsonObject
        {
            ["status"] = "valid",
            ["workItems"] = new JsonArray(),
            ["edges"] = new JsonArray(),
            ["executionWaves"] = new JsonArray(),
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
        StringAssert.Contains(verifyOutput, "TODO contract:");
        using var verified = JsonDocument.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken));
        Assert.IsFalse(verified.RootElement.TryGetProperty("todoGraph", out _));
        var startup = verified.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Single(todo =>
                todo.GetProperty("id").GetString() == "UWMIG001");
        Assert.IsFalse(startup.TryGetProperty("workSlices", out _));
        Assert.AreEqual("accepted", startup.GetProperty("status").GetString());
        CollectionAssert.AreEqual(
            new[] { "UWMIG003" },
            startup.GetProperty("dependsOn")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray());
    }

    [TestMethod]
    public async Task Verify_InvalidTodoDependencyFailsClosed()
    {
        var source = await CreateUwpSourceAsync("InvalidTodoContractApp");
        await WriteAsync(source, "App.xaml", "<Application />");
        await WriteAsync(
            source,
            "App.xaml.cs",
            "namespace InvalidTodoContractApp; public sealed partial class App { }");
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "invalid-todo-contract-output"));
        ArrangeTemplateCreation(target, "InvalidTodoContractApp");
        var (migrateExit, migrateOutput) = await InvokeAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);

        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken))!;
        var startup = report["todos"]!.AsArray()
            .Single(todo =>
                todo!["id"]!.GetValue<string>() == "UWMIG001")!;
        startup["dependsOn"] = new JsonArray("UWMIG999");
        await File.WriteAllTextAsync(
            reportPath,
            report.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }),
            TestContext.CancellationToken);

        var (verifyExit, verifyOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(1, verifyExit, verifyOutput);
        StringAssert.Contains(
            verifyOutput,
            "TODO contract is invalid: [unknown-dependency]");
        StringAssert.Contains(verifyOutput, "UWMIG999");
    }
}
