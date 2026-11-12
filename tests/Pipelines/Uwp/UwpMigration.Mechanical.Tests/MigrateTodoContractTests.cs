// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using UwpMigration.Mechanical;

namespace UwpMigration.Mechanical.Tests;

public partial class MigrateCommandTests
{
    [TestMethod]
    [DataRow("var window = Window.Current;", "UWPC003")]
    [DataRow("using Window = Microsoft.UI.Xaml.Window; var window = Window.Current;", "UWPC003")]
    [DataRow("using W = Microsoft.UI.Xaml.Window; var window = W.Current;", "UWPC003")]
    [DataRow("var window = Windows.UI.Core.CoreWindow.GetForCurrentThread();", "UWPC002")]
    [DataRow("using Windows.UI.Core; CoreDispatcher dispatcher;", "UWPC002")]
    [DataRow("var view = Windows.UI.ViewManagement.ApplicationView.GetForCurrentView();", "UWPC004")]
    [DataRow("var text = $\"Window: {Window.Current}\";", "UWPC003")]
    public async Task Verify_RejectsCurrentUwpResidueRegardlessOfTodoLabel(string code, string rule)
    {
        var source = await CreateUwpSourceAsync("CompatibilityApp");
        var target = NewTarget("compatibility-output");
        ArrangeTemplateCreation(target, "CompatibilityApp");
        var (created, output) = await InvokeAsync(source, target);
        Assert.AreEqual(0, created, output);
        await WriteAsync(target, "Compatibility.cs", code);

        var (failed, diagnostic) = await InvokeVerifyAsync(target);

        Assert.AreEqual(1, failed, diagnostic);
        StringAssert.Contains(diagnostic, $"Compatibility.cs:1: [{rule}]");
        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath))!;
        var todo = report["todos"]!.AsArray()
            .Single(item => item!["id"]!.GetValue<string>() == "UWMIG017")!;
        todo["status"] = "verified";
        await File.WriteAllTextAsync(reportPath, report.ToJsonString());
        var (stillFailed, _) = await InvokeVerifyAsync(target);
        Assert.AreEqual(1, stillFailed);
        using (var current = await ReadReportAsync(target))
            Assert.AreEqual("pending", current.RootElement.GetProperty("todos").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == "UWMIG017")
                .GetProperty("status").GetString());

        await WriteAsync(target, "Compatibility.cs", "var window = App.MainWindow;");
        var (repaired, repairedOutput) = await InvokeVerifyAsync(target);
        Assert.AreEqual(0, repaired, repairedOutput);
        using var repairedReport = await ReadReportAsync(target);
        Assert.AreEqual(0, repairedReport.RootElement.GetProperty("mechanicalVerification")
            .GetProperty("incompatibleUwpResiduals").GetArrayLength());
        Assert.IsFalse(repairedReport.RootElement.GetProperty("todos").EnumerateArray()
            .Any(item => item.GetProperty("id").GetString() == "UWMIG017"));
    }

    [TestMethod]
    public async Task Compatibility_AllowsDesktopWinRtAndNonCodeExamples()
    {
        var root = _tempDirectory.CreateSubdirectory("allowed-compatibility");
        await WriteAsync(root, "Allowed.cs", """
            using Windows.Storage;
            using Windows.Foundation;
            using Windows.UI;
            using Windows.UI.Composition;
            // Window.Current; Windows.UI.Xaml.Controls.Button; CoreDispatcher
            const string example = "Window.Current; CoreDispatcher";
            var dialog = new Windows.UI.Popups.MessageDialog("Ready");
            WinRT.Interop.InitializeWithWindow.Initialize(dialog, hwnd);
            class Window { public static object Current => new(); }
            var custom = Window.Current;
            """);
        await WriteAsync(root, "Allowed.xaml", """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:local="using:Example">
              <!-- <MediaElement /> -->
              <local:MediaElement />
            </Page>
            """);
        await WriteAsync(root, ".uwp-source\\Original.cs", "var window = Window.Current;");
        await WriteAsync(root, ".migration-evidence\\Example.cs", "var window = Window.Current;");
        await WriteAsync(root, "obj\\Generated.cs", "var window = Window.Current;");
        Assert.IsEmpty(MechanicalMigration.Engine.FindIncompatibleUwpResiduals(root.FullName));
    }

    [TestMethod]
    [DataRow("Screen.xaml", "<Page><MediaElement /></Page>", "UWPC005")]
    [DataRow("Screen.xaml", "<Page><CaptureElement /></Page>", "UWPC005")]
    [DataRow("Target.csproj", "<Project><PropertyGroup><TargetPlatformIdentifier>UAP</TargetPlatformIdentifier></PropertyGroup></Project>", "UWPC006")]
    [DataRow("Target.csproj", "<Project><PropertyGroup><OutputType>AppContainerExe</OutputType></PropertyGroup></Project>", "UWPC006")]
    [DataRow("Target.csproj", "<Project><ItemGroup><PackageReference Include=\"Microsoft.NETCore.UniversalWindowsPlatform\" /></ItemGroup></Project>", "UWPC006")]
    [DataRow("Target.csproj", "<Project><ItemGroup><Content Include=\"$(SharedContentDir)\\Assets\\logo.png\" /></ItemGroup></Project>", "UWPC006")]
    [DataRow("Target.csproj", "<Project>", "UWPC000")]
    public async Task Compatibility_ReportsXamlAndProjectResidue(string path, string content, string rule)
    {
        var root = _tempDirectory.CreateSubdirectory("xml-compatibility");
        await WriteAsync(root, path, content);
        var issue = MechanicalMigration.Engine.FindIncompatibleUwpResiduals(root.FullName).Single();
        Assert.AreEqual(rule, issue.RuleId);
        Assert.AreEqual(path, issue.Location.Path);
        Assert.AreEqual(1, issue.Location.Line);
        Assert.IsFalse(string.IsNullOrWhiteSpace(issue.RequiredResolution));
    }

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
            "1.7",
            report.RootElement.GetProperty("schemaVersion").GetString());
        Assert.IsFalse(report.RootElement.TryGetProperty("todoGraph", out _));
        var todos = report.RootElement.GetProperty("todos")
            .EnumerateArray()
            .ToDictionary(todo => todo.GetProperty("id").GetString()!);
        foreach (var id in new[]
        {
            "UWMIG001",
            "UWMIG002",
            "UWMIG003",
            "UWMIG004"
        })
        {
            var todo = todos[id];
            Assert.IsFalse(todo.TryGetProperty("workSlices", out _));
            Assert.AreEqual(
                "mechanical",
                todo.GetProperty("origin").GetString());
            Assert.IsFalse(todo.TryGetProperty("dependsOn", out _));
            Assert.IsTrue(todo.GetProperty("touchSet")
                .GetProperty("conflictKeys").GetArrayLength() > 0);
            Assert.IsTrue(todo.GetProperty("affectedFeatures")
                .GetArrayLength() > 0);
            var guidance = todo.GetProperty("guidance");
            Assert.IsTrue(guidance.GetProperty("facts").GetArrayLength() > 0);
            Assert.IsTrue(guidance.GetProperty("completionChecks")
                .GetArrayLength() > 0);
            Assert.IsFalse(guidance.GetProperty("completionChecks")
                .EnumerateArray()
                .Any(check =>
                    check.GetProperty("verificationKind").GetString() ==
                    "state-set"));
            Assert.IsTrue(guidance.GetProperty("candidateStrategies")
                .EnumerateArray()
                .All(strategy =>
                    strategy.GetProperty("preconditions").GetArrayLength() > 0
                    && strategy.GetProperty("risks").GetArrayLength() > 0));
        }
        Assert.AreEqual(
            "app-resources",
            todos["UWMIG001"].GetProperty("category").GetString());
        Assert.AreEqual(
            "startup-lifecycle",
            todos["UWMIG004"].GetProperty("category").GetString());
        Assert.AreEqual(
            "App.xaml",
            todos["UWMIG001"].GetProperty("touchSet")
                .GetProperty("paths")[0].GetString());
        CollectionAssert.Contains(
            todos["UWMIG004"].GetProperty("touchSet")
                .GetProperty("paths")
                .EnumerateArray()
                .Select(path => path.GetString())
                .ToArray(),
            "App.xaml.cs");

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
    [DataRow("todoGraph")]
    [DataRow("dependsOn")]
    [DataRow("workSlices")]
    public async Task Verify_RetiredTodoProtocolsAreRejectedWithoutRewritingReport(string field)
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
        if (field == "dependsOn")
            todos["UWMIG001"]![field] = new JsonArray("UWMIG003");
        if (field == "workSlices")
            todos["UWMIG001"]![field] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "UWMIG001:legacy-work",
                ["stage"] = "preparation"
            }
        };
        if (field == "todoGraph")
            report[field] = new JsonObject
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
        var original = await File.ReadAllTextAsync(reportPath, TestContext.CancellationToken);

        var (verifyExit, verifyOutput) = await InvokeVerifyAsync(target);

        Assert.AreNotEqual(0, verifyExit, verifyOutput);
        StringAssert.Contains(verifyOutput, field);
        Assert.AreEqual(original, await File.ReadAllTextAsync(reportPath, TestContext.CancellationToken));
    }
}
