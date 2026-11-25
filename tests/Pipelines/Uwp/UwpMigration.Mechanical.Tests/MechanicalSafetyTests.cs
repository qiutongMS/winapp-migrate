// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Xml.Linq;
using UwpMigration.Contracts;
using UwpMigration.Mechanical;

namespace UwpMigration.Mechanical.Tests;

[TestClass]
public sealed class MechanicalSafetyTests : MigrateCommandTestBase
{
    [TestMethod]
    public async Task NamespaceRewritePreservesLiteralCommentMarkupAndBusinessResourceKeys()
    {
        var root = _tempDirectory.CreateSubdirectory("namespace-safety");
        const string code = """using Windows.UI.Xaml; class C { string Value = "Windows.UI.Xaml.Customer"; /* Windows.UI.Xaml */ }""";
        await WriteAsync(root, "C.cs", code);
        await WriteAsync(root, "C.xaml", """<Page xmlns:p="using:Windows.UI.Xaml.Controls"><TextBlock Text="Windows.UI.Xaml.Customer"/><!-- xmlns:p="using:Windows.UI.Xaml.Controls" --></Page>""");
        await WriteAsync(root, "Resources.resw", """<root><data name="Windows.UI.Xaml.Customer"><value>Windows.UI.Xaml.Customer</value></data><data name="B.[using:Windows.UI.Xaml.Automation]AutomationProperties.Name"><value>Button</value></data></root>""");
        Assert.AreEqual(2, MechanicalMigration.Engine.RewriteNamespaces(root.FullName));
        Assert.AreEqual(1, MechanicalMigration.Engine.RewriteReswNamespaces(root.FullName));
        StringAssert.Contains(await ReadAsync(root, "C.cs"), """string Value = "Windows.UI.Xaml.Customer"; /* Windows.UI.Xaml */""");
        var xaml = await ReadAsync(root, "C.xaml");
        StringAssert.Contains(xaml, """xmlns:p="using:Microsoft.UI.Xaml.Controls" """.TrimEnd());
        StringAssert.Contains(xaml, """Text="Windows.UI.Xaml.Customer" """.TrimEnd());
        StringAssert.Contains(xaml, """<!-- xmlns:p="using:Windows.UI.Xaml.Controls" -->""");
        var resources = XDocument.Load(Path.Combine(root.FullName, "Resources.resw"));
        Assert.AreEqual("Windows.UI.Xaml.Customer", (string?)resources.Root!.Elements("data").First().Attribute("name"));
        Assert.AreEqual("B.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name", (string?)resources.Root.Elements("data").Last().Attribute("name"));
        Assert.AreEqual(0, MechanicalMigration.Engine.RewriteNamespaces(root.FullName));
        Assert.AreEqual(0, MechanicalMigration.Engine.RewriteReswNamespaces(root.FullName));
    }

    [TestMethod]
    [DataRow("service.Dispatcher.HasThreadAccess", false)]
    [DataRow("Dispatcher.HasThreadAccess", true)]
    [DataRow("this.Dispatcher.HasThreadAccess", true)]
    public async Task DispatcherRequiresActualPageReceiver(string receiver, bool converted)
    {
        var root = _tempDirectory.CreateSubdirectory("dispatcher-identity");
        await WriteAsync(root, "Page.xaml", "<Page />");
        await WriteAsync(root, "Page.xaml.cs",
            $"using Microsoft.UI.Xaml.Controls; class MainPage : Page {{ bool Access() => {receiver}; }}");
        Assert.AreEqual(converted ? 1 : 0, MechanicalMigration.Engine.RewriteDispatcherAccess(root.FullName));
        StringAssert.Contains(await ReadAsync(root, "Page.xaml.cs"), converted
            ? receiver.Replace("Dispatcher.", "DispatcherQueue.", StringComparison.Ordinal) : receiver);
    }

    [TestMethod]
    public async Task DispatcherRejectsCrossFilePageAndPartialMemberShadowsAndPreservesUndecodableBytes()
    {
        var root = _tempDirectory.CreateSubdirectory("dispatcher-shadow");
        await WriteAsync(root, "Page.xaml", "<Page />");
        await WriteAsync(root, "Page.xaml.cs", "using Microsoft.UI.Xaml.Controls; partial class MainPage : Page { bool Access()=>Dispatcher.HasThreadAccess; }");
        await WriteAsync(root, "Shadow.cs", "partial class MainPage { object Dispatcher { get; } }");
        Assert.AreEqual(0, MechanicalMigration.Engine.RewriteDispatcherAccess(root.FullName));
        await WriteAsync(root, "Shadow.cs", "class Page { }");
        Assert.AreEqual(0, MechanicalMigration.Engine.RewriteDispatcherAccess(root.FullName));
        File.Delete(Path.Combine(root.FullName, "Shadow.cs"));
        var bytes = Encoding.Latin1.GetBytes("using Microsoft.UI.Xaml.Controls; class MainPage:Page { /* café */ bool Access()=>Dispatcher.HasThreadAccess; }");
        await File.WriteAllBytesAsync(Path.Combine(root.FullName, "Page.xaml.cs"), bytes, TestContext.CancellationToken);
        Assert.AreEqual(0, MechanicalMigration.Engine.RewriteDispatcherAccess(root.FullName));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(root.FullName, "Page.xaml.cs"), TestContext.CancellationToken));
        Assert.IsTrue(MechanicalMigration.Engine.FindIncompatibleUwpResiduals(root.FullName).Any(issue => issue.RuleId == "UWPC000"));
    }

    [TestMethod]
    [DataRow("namespace Business { class ApplicationView { static object GetForCurrentView()=>null; object M()=>ApplicationView.GetForCurrentView(); } }", false)]
    [DataRow("class Window {} class C { object M()=>global::Microsoft.UI.Xaml.Window.Current; }", true)]
    [DataRow("using Windows.UI.ViewManagement; class C { object M()=>ApplicationView.GetForCurrentView(); }", true)]
    [DataRow("using W=global::Microsoft.UI.Xaml.Window; class C { object M()=>W.Current; }", true)]
    public async Task CompatibilityRequiresPlatformIdentity(string text, bool incompatible)
    {
        var root = _tempDirectory.CreateSubdirectory("compatibility-identity");
        await WriteAsync(root, "C.cs", text);
        Assert.AreEqual(incompatible, MechanicalMigration.Engine.FindIncompatibleUwpResiduals(root.FullName).Count > 0);
    }

    [TestMethod]
    public async Task NamespaceExpressionsAreRootedButBusinessReceiversAndUnrelatedTypesAreNotRebound()
    {
        var root = _tempDirectory.CreateSubdirectory("namespace-receivers");
        await WriteAsync(root, "C.cs", "class C { object M()=>Windows.UI.Xaml.Application.Current; object Windows; object N()=>Windows.UI.Xaml.Customer; }");
        Assert.AreEqual(0, MechanicalMigration.Engine.RewriteNamespaces(root.FullName));
        Assert.IsEmpty(MechanicalMigration.Engine.FindIncompatibleUwpResiduals(root.FullName));
        await WriteAsync(root, "C.cs", "class C { object M()=>Windows.UI.Xaml.Application.Current; }");
        Assert.AreEqual(1, MechanicalMigration.Engine.RewriteNamespaces(root.FullName));
        StringAssert.Contains(await ReadAsync(root, "C.cs"), "global::Microsoft.UI.Xaml.Application.Current");
        await WriteAsync(root, "C.cs", "using X=global::Microsoft.UI.Xaml; namespace UI { class C { object M()=>X.Window.Current; } }");
        await WriteAsync(root, "Unrelated.cs", "namespace Business { class Window {} }");
        Assert.IsTrue(MechanicalMigration.Engine.FindIncompatibleUwpResiduals(root.FullName).Any(issue => issue.RuleId == "UWPC003"));
    }

    [TestMethod]
    public async Task EmptyAssemblyInfoDoesNotDisableUnrelatedGeneratedAttributes()
    {
        var source = _tempDirectory.CreateSubdirectory("empty-assembly-source");
        await WriteAsync(source, "Source.csproj", ClassicUwpCsproj);
        await WriteAsync(source, "Properties\\AssemblyInfo.cs", "// no assembly attributes");
        var target = NewTarget("empty-assembly-target");
        ArrangeBasicTemplateCreation("EmptyAssembly");
        var (exit, output) = await InvokeMigrateAsync(source, target);
        Assert.AreEqual(0, exit, output);
        Assert.DoesNotContain("<GenerateAssembly", await ReadAsync(target, "EmptyAssembly.csproj"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NoneDeclarationDoesNotCancelARealCompileOwner(bool sdkDefaults)
    {
        var source = _tempDirectory.CreateSubdirectory("dual-kind-source");
        var project = sdkDefaults
            ? """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetPlatformIdentifier>UAP</TargetPlatformIdentifier></PropertyGroup><ItemGroup><None Include="A.cs"/></ItemGroup></Project>"""
            : """<Project><PropertyGroup><TargetPlatformIdentifier>UAP</TargetPlatformIdentifier></PropertyGroup><ItemGroup><Compile Include="A.cs"/><None Include="A.cs"/></ItemGroup></Project>""";
        await WriteAsync(source, "Source.csproj", project);
        await WriteAsync(source, "A.cs", "class A {}");
        var target = NewTarget("dual-kind-target");
        ArrangeBasicTemplateCreation("DualKind");
        var (exit, output) = await InvokeMigrateAsync(source, target);
        Assert.AreEqual(0, exit, output);
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "A.cs")));
    }

    [TestMethod]
    [DataRow("category")]
    [DataRow("summary")]
    [DataRow("reason")]
    [DataRow("priority")]
    [DataRow("reasonCode")]
    public void PersistedTodoRejectsEmptySemanticFields(string field)
    {
        var todo = ValidTodo();
        switch (field)
        {
            case "category": todo.Category = ""; break;
            case "summary": todo.Summary = ""; break;
            case "reason": todo.Reason = ""; break;
            case "priority": todo.Priority = null!; break;
            case "reasonCode": todo.Guidance!.ReasonCode = ""; break;
        }
        Assert.ThrowsExactly<InvalidDataException>(() => MigrationTodoValidator.ValidatePersisted([todo]));
    }

    [TestMethod]
    public void PersistedTodoRejectsDuplicateFactsAndUiaOnlyResourceClosure()
    {
        var todo = ValidTodo();
        todo.Guidance!.Facts = [new() { Id = "same", Summary = "A" }, new() { Id = "same", Summary = "B" }];
        Assert.ThrowsExactly<InvalidDataException>(() => MigrationTodoValidator.ValidatePersisted([todo]));
        todo = ValidTodo();
        todo.Id = "UWMIG001";
        todo.Guidance!.ReasonCode = "preserved-uwp-resource-contract-requires-merge";
        todo.Guidance.CompletionChecks = [new() { Id = "uia", Summary = "Visual structure", VerificationKind = "surface-binding", Target = todo.Id, Expected = "passed", AssertionIds = ["target-reachable", "uia-structure-match"] }];
        Assert.ThrowsExactly<InvalidDataException>(() => MigrationTodoValidator.ValidatePersisted([todo]));
    }

    [TestMethod]
    public async Task ItemMetadataOperationsAndConditionalIdentityUseSemanticFacts()
    {
        var source = _tempDirectory.CreateSubdirectory("item-source");
        var target = _tempDirectory.CreateSubdirectory("item-target");
        await WriteAsync(source, "App.csproj", """<Project><ItemGroup><Content Include="a.json" CopyToOutputDirectory="Always" /><Content Include="b.json" Exclude="b.json" /></ItemGroup></Project>""");
        await WriteAsync(target, "App.csproj", CleanCsproj);
        await WriteAsync(target, "a.json", "{}");
        await WriteAsync(target, "b.json", "{}");
        await WriteAsync(source, "a.json", "{}");
        await WriteAsync(source, "b.json", "{}");
        var sourceProject = Path.Combine(source.FullName, "App.csproj");
        var targetProject = Path.Combine(target.FullName, "App.csproj");
        var first = MechanicalMigration.Engine.MigrateSourceProjectItems(source.FullName, sourceProject, target.FullName, targetProject, true);
        Assert.AreEqual(1, first.MigratedItems);
        Assert.HasCount(1, first.DecisionRequiredItems);
        Assert.AreEqual("Always", XDocument.Load(targetProject).Descendants("CopyToOutputDirectory").Single().Value);
        _ = MechanicalMigration.Engine.MigrateSourceProjectItems(source.FullName, sourceProject, target.FullName, targetProject, true);
        Assert.AreEqual(1, XDocument.Load(targetProject).Root!.Elements("ItemGroup").Count(group => (string?)group.Attribute("Label") == "winapp-migrate:uwp-source-items"));
        await WriteAsync(source, "App.csproj", """<Project><Choose><When Condition="'$(Configuration)'=='Debug'"/><Otherwise><ItemGroup><Content Include="a.json"/></ItemGroup></Otherwise></Choose></Project>""");
        var debug = MechanicalMigration.Engine.MigrateSourceProjectItems(source.FullName, sourceProject, target.FullName, targetProject, true).DecisionRequiredItems.Single().Id;
        await WriteAsync(source, "App.csproj", """<Project><Choose><When Condition="'$(Configuration)'=='Release'"/><Otherwise><ItemGroup><Content Include="a.json"/></ItemGroup></Otherwise></Choose></Project>""");
        var release = MechanicalMigration.Engine.MigrateSourceProjectItems(source.FullName, sourceProject, target.FullName, targetProject, true).DecisionRequiredItems.Single().Id;
        Assert.AreNotEqual(debug, release);
    }

    [TestMethod]
    public async Task RealMsBuildVerifiesDefaultPriOwnershipAndMetadataDrift()
    {
        var source = _tempDirectory.CreateSubdirectory("real-item-source");
        var target = _tempDirectory.CreateSubdirectory("real-item-target");
        await WriteAsync(source, "App.csproj", """<Project><ItemGroup><Content Include="a.json" CopyToOutputDirectory="Always"/><PRIResource Include="Resources.resw"/></ItemGroup></Project>""");
        await WriteAsync(target, "App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Content Include="a.json" CopyToOutputDirectory="Never"/></ItemGroup></Project>""");
        await WriteAsync(target, "a.json", "{}");
        await WriteAsync(target, "Resources.resw", "<root/>");
        await WriteAsync(source, "a.json", "{}");
        await WriteAsync(source, "Resources.resw", "<root/>");
        var evaluator = new MsBuildProjectItemEvaluator(new ProcessRunner());
        var project = Path.Combine(target.FullName, "App.csproj");
        var facts = await MechanicalMigration.Engine.EvaluateProjectItemFactsAsync(target.FullName, project, evaluator, TestContext.CancellationToken);
        Assert.IsTrue(facts.Succeeded, facts.Error);
        var result = MechanicalMigration.Engine.MigrateSourceProjectItems(source.FullName, Path.Combine(source.FullName, "App.csproj"), target.FullName, project, false, facts);
        Assert.AreEqual(0, result.MigratedItems);
        Assert.HasCount(2, result.MissingTargetItems);
        Assert.IsTrue(result.DecisionRequiredItems.Any(item => item.ReviewReason == "target-item-metadata-drift"));
        await WriteAsync(target, "App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Content Include="a.json" CopyToOutputDirectory="Always"/><PRIResource Include="Resources.resw"/></ItemGroup></Project>""");
        facts = await MechanicalMigration.Engine.EvaluateProjectItemFactsAsync(target.FullName, project, evaluator, TestContext.CancellationToken);
        result = MechanicalMigration.Engine.MigrateSourceProjectItems(source.FullName, Path.Combine(source.FullName, "App.csproj"), target.FullName, project, false, facts);
        Assert.AreEqual(2, result.MigratedItems);
        Assert.HasCount(0, result.MissingTargetItems);
    }

    [TestMethod]
    public async Task RealMsBuildTraversesSiblingAndTransitiveProjectReferencesAndAllowsSharedNone()
    {
        var root = _tempDirectory.CreateSubdirectory("real-graph");
        var app = root.CreateSubdirectory("App");
        var library = root.CreateSubdirectory("Library");
        var external = _tempDirectory.CreateSubdirectory("external-library");
        const string libraryProject = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><None Include="..\readme.txt"/></ItemGroup></Project>""";
        await WriteAsync(root, "readme.txt", "shared");
        await WriteAsync(library, "Library.csproj", libraryProject);
        await WriteAsync(external, "External.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        await WriteAsync(app, "App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="..\Library\Library.csproj"/><None Include="..\readme.txt"/></ItemGroup></Project>""");
        var evaluator = new MsBuildProjectItemEvaluator(new ProcessRunner());
        var entry = Path.Combine(app.FullName, "App.csproj");
        var result = await MechanicalMigration.Engine.AnalyzeTargetProjectGraphAsync(root.FullName, entry, evaluator, TestContext.CancellationToken);
        Assert.AreEqual("passed", result.Status);
        Assert.IsTrue(result.NestedProjects.Contains("Library/Library.csproj"));
        var document = XDocument.Parse(libraryProject);
        document.Root!.Add(new XElement("ItemGroup", new XElement("ProjectReference", new XAttribute("Include", Path.Combine(external.FullName, "External.csproj")))));
        document.Save(Path.Combine(library.FullName, "Library.csproj"));
        result = await MechanicalMigration.Engine.AnalyzeTargetProjectGraphAsync(root.FullName, entry, evaluator, TestContext.CancellationToken);
        Assert.AreEqual("failed", result.Status);
        Assert.IsTrue(result.Issues.Any(issue => issue.ItemKinds.Contains("ProjectReference")));
    }

    [TestMethod]
    public async Task MigrationPreservesStartupAndInactiveItemsThenCompilesRootedOutput()
    {
        var source = _tempDirectory.CreateSubdirectory("compile-source");
        await WriteAsync(source, "Source.csproj", """<Project><PropertyGroup><TargetPlatformIdentifier>UAP</TargetPlatformIdentifier></PropertyGroup><ItemGroup><Compile Include="MainPage.xaml.cs"/><Compile Include="MainWindow.xaml.cs"/><Compile Include="Caller.cs"/><Page Include="MainPage.xaml"/><Page Include="MainWindow.xaml"/><None Include="Scratch.cs"/></ItemGroup></Project>""");
        await WriteAsync(source, "MainWindow.xaml", """<Page x:Class="CompileApp.MainWindow" />""");
        const string mainWindow = "namespace CompileApp; class MainWindow : Windows.UI.Xaml.Controls.Page {}";
        await WriteAsync(source, "MainWindow.xaml.cs", mainWindow);
        await WriteAsync(source, "MainPage.xaml", "<Page/>");
        await WriteAsync(source, "MainPage.xaml.cs", """
            using Windows.UI.Xaml.Controls;
            namespace CompileApp;
            class MainPage : Page
            {
                bool Access()=>this.Dispatcher.HasThreadAccess;
                object Key()=>global::Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState('x');
            }
            class Microsoft {}
            """);
        await WriteAsync(source, "Scratch.cs", "THIS MUST NOT COMPILE");
        await WriteAsync(source, "Caller.cs", "namespace CompileApp; class Caller { System.Type Destination => typeof(MainWindow); }");
        await WriteAsync(source, "Directory.Build.props", "<Project><PropertyGroup><TargetFramework>invalid-source-control</TargetFramework></PropertyGroup></Project>");
        var target = NewTarget("real-compile-target");
        ArrangeBasicTemplateCreation("CompileApp");
        var (created, output) = await InvokeMigrateAsync(source, target);
        Assert.AreEqual(0, created, output);
        Assert.AreEqual(mainWindow, await ReadAsync(target, ".uwp-source", "MainWindow.xaml.cs.reference"));
        StringAssert.Contains(await ReadAsync(target, "MainWindow.xaml.cs"), ": global::Microsoft.UI.Xaml.Window");
        Assert.IsFalse(File.Exists(Path.Combine(target.FullName, "Scratch.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(target.FullName, "Caller.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, ".uwp-source", "Caller.cs.reference")));
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, ".uwp-source", "Scratch.cs.reference")));
        Assert.IsFalse(File.Exists(Path.Combine(target.FullName, "Directory.Build.props")));
        var transformed = await ReadAsync(target, "MainPage.xaml.cs");
        StringAssert.Contains(transformed, "global::Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread('x')");
        await WriteAsync(target, "CompileContracts.cs", """
            namespace Microsoft.UI.Xaml { public class Application {} public class Window { public void Activate() {} } }
            namespace Microsoft.UI.Xaml.Controls { public class Page { public Queue DispatcherQueue { get; } = new(); } public class Queue { public bool HasThreadAccess => true; } }
            namespace Microsoft.UI.Input { public static class InputKeyboardSource { public static object GetKeyStateForCurrentThread(char key) => key; } }
            namespace CompileApp { partial class MainWindow { void InitializeComponent() {} } }
            """);
        var project = XDocument.Load(Path.Combine(target.FullName, "CompileApp.csproj"));
        project.Descendants("OutputType").Single().Value = "Library";
        project.Save(Path.Combine(target.FullName, "CompileApp.csproj"));
        var runner = new ProcessRunner();
        var (exit, standardOutput, error) = await runner.RunDotNetAsync(target, ["build", "--no-restore", "-v:q"], TestContext.CancellationToken);
        if (exit != 0 && (standardOutput + error).Contains("NETSDK1004", StringComparison.Ordinal))
        {
            var restore = await runner.RunDotNetAsync(target, ["restore", "--ignore-failed-sources", "-v:q"], TestContext.CancellationToken);
            Assert.AreEqual(0, restore.ExitCode, restore.Output + restore.Error);
            (exit, standardOutput, error) = await runner.RunDotNetAsync(target, ["build", "--no-restore", "-v:q"], TestContext.CancellationToken);
        }
        Assert.AreEqual(0, exit, standardOutput + error);
        var evaluated = await new MsBuildProjectItemEvaluator(runner).EvaluateAsync(new FileInfo(Path.Combine(target.FullName, "CompileApp.csproj")), TestContext.CancellationToken);
        Assert.IsTrue(evaluated.Succeeded, evaluated.Error);
        Assert.IsFalse(evaluated.Items.Any(item => item.Kind == "Compile" && item.Identity.Contains("Scratch", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task PreflightRejectsAmbiguousAndInvalidInputsWithoutInvokingTemplate()
    {
        var root = _tempDirectory.CreateSubdirectory("ambiguous-source");
        await WriteAsync(root, "A.csproj", ClassicUwpCsproj);
        await WriteAsync(root, "B.csproj", ClassicUwpCsproj);
        var target = NewTarget("preflight-output");
        var (exit, _) = await InvokeMigrateAsync(root, target);
        Assert.AreEqual(1, exit);
        Assert.IsEmpty(FakeDotNet.CommandCalls);
        File.Delete(Path.Combine(root.FullName, "B.csproj"));
        await WriteAsync(root, "A.csproj", "<Project/>");
        (exit, _) = await InvokeMigrateAsync(root, target);
        Assert.AreEqual(1, exit);
        Assert.IsEmpty(FakeDotNet.CommandCalls);
    }

    private static MigrationTodo ValidTodo() => new()
    {
        Id = "T", Category = "test", Priority = "required", Summary = "Review", Reason = "Deferred intent",
        Guidance = new()
        {
            ReasonCode = "review-required",
            CompletionChecks = [new() { Id = "review", Summary = "Review evidence", VerificationKind = "todo-resolution-evidence", Target = "T", Expected = "terminal-with-evidence" }]
        }
    };
}
