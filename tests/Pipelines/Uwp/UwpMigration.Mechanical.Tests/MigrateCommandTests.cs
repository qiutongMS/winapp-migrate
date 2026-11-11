// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using WinAppMigration.Cli.Pipelines.Uwp;
using UwpMigration.Contracts;
using UwpMigration.Mechanical;

namespace UwpMigration.Mechanical.Tests;

[TestClass]
[DoNotParallelize]
public partial class MigrateCommandTests : MigrateCommandTestBase
{
    private async Task<DirectoryInfo> CreateUwpSourceAsync(string name)
    {
        var dir = _tempDirectory.CreateSubdirectory(name);
        await WriteAsync(dir, $"{name}.csproj", CleanCsproj);
        await WriteAsync(dir, "Package.appxmanifest", """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Capabilities><Capability Name="internetClient" /></Capabilities>
              <Applications><Application><Extensions>
                <uap:Extension Category="windows.appService" />
              </Extensions></Application></Applications>
            </Package>
            """);
        return dir;
    }

    private async Task<DirectoryInfo> CreateTemplateSelectorSourceAsync(
        string name,
        string fallbackStatement,
        bool useNavigationView)
    {
        var source = await CreateUwpSourceAsync(name);
        var selectorProperty = useNavigationView
            ? "MenuItemTemplateSelector=\"{StaticResource CatalogSelector}\""
            : string.Empty;
        var itemsControl = useNavigationView
            ? $"<NavigationView {selectorProperty} />"
            : """<ListView ItemTemplateSelector="{StaticResource CatalogSelector}" />""";
        await WriteAsync(source, "MainPage.xaml", $$"""
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  xmlns:local="using:GenericCatalog">
              <Page.Resources>
                <DataTemplate x:Key="SpecialTemplate"><TextBlock Text="Special" /></DataTemplate>
                <DataTemplate x:Key="DefaultTemplate"><TextBlock Text="Default" /></DataTemplate>
                <local:CatalogTemplateSelector x:Key="CatalogSelector"
                                               SpecialTemplate="{StaticResource SpecialTemplate}"
                                               DefaultTemplate="{StaticResource DefaultTemplate}" />
              </Page.Resources>
              {{itemsControl}}
            </Page>
            """);
        await WriteAsync(source, "CatalogTemplateSelector.cs", $$"""
            using Windows.UI.Xaml;
            using Windows.UI.Xaml.Controls;
            namespace GenericCatalog;
            public sealed class CatalogTemplateSelector : DataTemplateSelector
            {
                public DataTemplate SpecialTemplate { get; set; }
                public DataTemplate DefaultTemplate { get; set; }

                protected override DataTemplate SelectTemplateCore(object item)
                {
                    if (item is SpecialCatalogItem)
                    {
                        return SpecialTemplate;
                    }
                    {{fallbackStatement}}
                }
            }
            """);
        return source;
    }

    private void ArrangeTemplateCreation(
        DirectoryInfo target,
        string projectName = "MigratedApp",
        Action<DirectoryInfo>? customizeTemplate = null)
    {
        FakeDotNet.RunDotnetCommandHandler = arguments =>
        {
            CollectionAssert.AreEqual(
                new[] { "new", "winui" },
                arguments.Take(2).ToArray());
            var templateOutput = GetTemplateOutput(arguments);
            templateOutput.Create();
            File.WriteAllText(Path.Combine(templateOutput.FullName, $"{projectName}.csproj"), CleanCsproj);
            File.WriteAllText(Path.Combine(templateOutput.FullName, "App.xaml"),
                $"<Application x:Class=\"{projectName}.App\"><Application.Resources><ResourceDictionary /></Application.Resources></Application>");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "App.xaml.cs"),
                $"namespace {projectName}; public partial class App {{ }}");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "MainWindow.xaml"),
                $"<Window x:Class=\"{projectName}.MainWindow\"><Grid Grid.Row=\"1\" /></Window>");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "MainWindow.xaml.cs"),
                $"namespace {projectName}; public partial class MainWindow {{ public MainWindow() {{ InitializeComponent(); }} }}");
            customizeTemplate?.Invoke(templateOutput);
            return (0, "Template created.", string.Empty);
        };
    }

    private Task<(int ExitCode, string Output)> InvokeAsync(
        DirectoryInfo source,
        DirectoryInfo target,
        params string[] extra) =>
        InvokeMigrateAsync(source, target, extra);

    private async Task<(DirectoryInfo Target, int ExitCode, string Output)>
        MigrateAsync(
            DirectoryInfo source,
            string targetName,
            string projectName)
    {
        var target = NewTarget(targetName);
        ArrangeTemplateCreation(target, projectName);
        var (exitCode, output) = await InvokeAsync(source, target);
        return (target, exitCode, output);
    }

    [TestMethod]
    public void MigrationCommands_AdvertiseCurrentReportSchema()
    {
        var expected = $"migration-report schema {MigrationReport.CurrentSchemaVersion}";

        StringAssert.Contains(
            GetRequiredService<MigrateCommand>().Description,
            expected);
        StringAssert.Contains(
            GetRequiredService<MigrateVerifyCommand>().Description,
            expected);
    }

    [TestMethod]
    public async Task MigrateAndVerify_ManifestOnlySourcePreservesNullProject()
    {
        var source = _tempDirectory.CreateSubdirectory("ManifestOnlySource");
        await WriteAsync(
            source,
            "Package.appxmanifest",
            """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Applications>
                <Application Id="App">
                  <Extensions>
                    <uap:Extension Category="windows.appService" />
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """);
        var target = NewTarget("manifest-only-output");
        ArrangeTemplateCreation(target, "ManifestOnlySourceApp");

        var (migrateExit, migrateOutput) = await InvokeAsync(
            source,
            target,
            "--quiet");
        var (verifyExit, verifyOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(0, migrateExit, migrateOutput);
        StringAssert.Contains(
            migrateOutput,
            "[WARN] No .csproj found under source");
        Assert.AreEqual(0, verifyExit, verifyOutput);
        using var report = await ReadReportAsync(target);
        Assert.IsFalse(
            report.RootElement
                .GetProperty("source")
                .TryGetProperty("projectFile", out _));
        var projectItems = report.RootElement
            .GetProperty("mechanicalVerification")
            .GetProperty("projectItems");
        Assert.AreEqual(0, projectItems.GetProperty("sourceItems").GetInt32());
        Assert.AreEqual(0, projectItems.GetProperty("unresolvedItems").GetArrayLength());
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task Migrate_CreatesWinuiProjectAndWritesReport()
    {
        var source = await CreateUwpSourceAsync("SensorApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs",
            "using Windows.UI.Xaml.Controls; namespace SDKTemplate; public partial class MainPage { }");
        var (target, exit, output) = await MigrateAsync(
            source, "output", "SensorAppApp");

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "=== MECHANICAL MIGRATION COMPLETE ===");
        Assert.HasCount(1, FakeDotNet.CommandCalls);
        CollectionAssert.AreEqual(
            new[] { "new", "winui" },
            FakeDotNet.CommandCalls[0].Arguments.Take(2).ToArray());
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "SensorAppApp.csproj")));
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "migration-report.json")));
        var migrated = await ReadAsync(target, "MainPage.xaml.cs");
        StringAssert.Contains(migrated, "Microsoft.UI.Xaml.Controls");

        using var report = await ReadReportAsync(target);
        Assert.AreEqual("1.7", report.RootElement.GetProperty("schemaVersion").GetString());
        Assert.AreEqual("mechanical-migration-complete", report.RootElement.GetProperty("status").GetString());
        Assert.IsTrue(report.RootElement.GetProperty("todos").GetArrayLength() >= 2);
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray()
            .All(todo => todo.GetProperty("status").GetString() == "pending"));
        var validation = report.RootElement.GetProperty("validation");
        Assert.AreEqual(
            ".migration-evidence/state-plan.json",
            validation.GetProperty("statePlan").GetString());
        Assert.AreEqual(
            "not-run",
            validation.GetProperty("sourceBaseline").GetProperty("status").GetString());
        Assert.AreEqual(
            ".migration-evidence/source",
            validation.GetProperty("sourceBaseline").GetProperty("evidenceRoot").GetString());
        Assert.AreEqual(
            "not-run",
            validation.GetProperty("targetReplay").GetProperty("status").GetString());
        Assert.AreEqual(
            ".migration-evidence/target",
            validation.GetProperty("targetReplay").GetProperty("evidenceRoot").GetString());
        Assert.AreEqual("unverified", validation.GetProperty("parityStatus").GetString());
        var targetProjectGraph = report.RootElement
            .GetProperty("mechanicalVerification")
            .GetProperty("targetProjectGraph");
        Assert.AreEqual(
            "not-required",
            targetProjectGraph.GetProperty("status").GetString());
        Assert.AreEqual(
            0,
            targetProjectGraph.GetProperty("nestedProjects").GetArrayLength());
    }

    [TestMethod]
    public async Task Migrate_KnownUwpImportsDoNotOpenProjectItemTodo()
    {
        var source = await CreateUwpSourceAsync("KnownImportsApp");
        await WriteAsync(
            source,
            "KnownImportsApp.csproj",
            """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props"
                      Condition="Exists('$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props')" />
              <Import Project="$(MSBuildExtensionsPath)\Microsoft\WindowsXaml\v$(VisualStudioVersion)\Microsoft.Windows.UI.Xaml.CSharp.targets" />
            </Project>
            """);
        var (target, exit, output) = await MigrateAsync(
            source, "known-imports-output", "KnownImportsApp");

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        Assert.IsFalse(report.RootElement
            .GetProperty("todos")
            .EnumerateArray()
            .Any(todo => todo.GetProperty("id").GetString() == "UWMIG012"));
        Assert.AreEqual(
            0,
            report.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("projectItems")
                .GetProperty("unresolvedItems")
                .GetArrayLength());
    }

    [TestMethod]
    public async Task Migrate_CustomImportRemainsReviewRequired()
    {
        var source = await CreateUwpSourceAsync("CustomImportApp");
        await WriteAsync(
            source,
            "CustomImportApp.csproj",
            """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <Import Project="Build\Custom.targets" />
            </Project>
            """);
        var (target, exit, output) = await MigrateAsync(
            source, "custom-import-output", "CustomImportApp");

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        Assert.IsTrue(report.RootElement
            .GetProperty("todos")
            .EnumerateArray()
            .Any(todo => todo.GetProperty("id").GetString() == "UWMIG012"));
        Assert.AreEqual(
            1,
            report.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("projectItems")
                .GetProperty("unresolvedItems")
                .GetArrayLength());
    }

    [TestMethod]
    public async Task Migrate_LegacyAssemblyInfoDisablesGeneratedAssemblyAttributes()
    {
        var source = await CreateUwpSourceAsync("AssemblyInfoApp");
        await WriteAsync(
            source,
            @"Properties\AssemblyInfo.cs",
            """[assembly: System.Reflection.AssemblyCompany("Contoso")]""");
        var target = NewTarget("assembly-info-output");
        ArrangeTemplateCreation(target, "AssemblyInfoAppApp", templateOutput =>
        {
            var nestedDirectory = templateOutput.CreateSubdirectory("NestedLibrary");
            File.WriteAllText(Path.Combine(nestedDirectory.FullName, "NestedLibrary.csproj"), CleanCsproj);
        });

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "Properties", "AssemblyInfo.cs")));
        var project = await ReadAsync(target, "AssemblyInfoAppApp.csproj");
        StringAssert.Contains(project, "<GenerateAssemblyInfo>false</GenerateAssemblyInfo>");
        var nestedProject = await ReadAsync(
            target,
            "NestedLibrary",
            "NestedLibrary.csproj");
        Assert.DoesNotContain("<GenerateAssemblyInfo>", nestedProject);
        StringAssert.Contains(output, "preserved Properties\\AssemblyInfo.cs remains authoritative");
    }

    [TestMethod]
    public async Task Migrate_ReportsDependencyGraphWithoutChoosingReplacementStrategy()
    {
        var source = await CreateUwpSourceAsync("DependencyApp");
        var sibling = _tempDirectory.CreateSubdirectory("SharedDependency");
        var conditionedSibling =
            _tempDirectory.CreateSubdirectory("ConditionedDependency");
        await WriteAsync(source, "DependencyApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>uap10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Legacy.Editor" Version="1.2.3" />
                <PackageReference Include="$(ConditionalPackage)" Version="$(ConditionalVersion)" Condition="'$(UseConditionalPackage)' == 'true'" />
                <PackageReference Include="Conditional.Concrete" Version="7.8.9" Condition="'$(UseConditionalPackage)' == 'true'" />
                <ProjectReference Include="..\SharedDependency\SharedDependency.csproj" />
                <ProjectReference Include="..\ConditionedDependency\ConditionedDependency.csproj" Condition="false" />
                <ProjectReference Include="$(OptionalProject)" Condition="'$(UseOptionalProject)' == 'true'" />
              </ItemGroup>
            </Project>
            """);
        await WriteAsync(conditionedSibling, "ConditionedDependency.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Must.Not.Be.Traversed" Version="9.9.9" />
              </ItemGroup>
            </Project>
            """);
        await WriteAsync(sibling, "SharedDependency.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>uap10.0;netstandard2.0</TargetFrameworks></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Legacy.Helpers"><Version>4.5.6</Version></PackageReference>
              </ItemGroup>
            </Project>
            """);
        var (target, exit, output) = await MigrateAsync(
            source, "dependency-output", "DependencyAppApp");

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var dependencies = report.RootElement.GetProperty("dependencyAnalysis");
        Assert.AreEqual("review-required", dependencies.GetProperty("status").GetString());
        Assert.AreEqual(2, dependencies.GetProperty("projects").GetArrayLength());
        var packages = dependencies.GetProperty("packageReferences").EnumerateArray().ToList();
        Assert.AreEqual(4, packages.Count);
        Assert.IsTrue(packages.All(package =>
            package.GetProperty("compatibilityStatus").GetString() == "review-required"));
        Assert.IsTrue(packages.Any(package =>
            package.GetProperty("id").GetString() == "Legacy.Editor"
            && package.GetProperty("version").GetString() == "1.2.3"
            && package.GetProperty("line").GetInt32() > 0));
        var conditionalPackage = packages.Single(package =>
            package.GetProperty("id").GetString() == "$(ConditionalPackage)");
        Assert.AreEqual(
            "unresolved-properties",
            conditionalPackage.GetProperty("resolutionStatus").GetString());
        StringAssert.Contains(
            conditionalPackage.GetProperty("condition").GetString(),
            "UseConditionalPackage");
        Assert.AreEqual(
            "unresolved-condition",
            packages.Single(package =>
                    package.GetProperty("id").GetString() == "Conditional.Concrete")
                .GetProperty("resolutionStatus")
                .GetString());
        var projectReferences = dependencies.GetProperty("projectReferences").EnumerateArray().ToList();
        Assert.AreEqual(3, projectReferences.Count);
        var projectReference = projectReferences.Single(reference =>
            reference.GetProperty("resolutionStatus").GetString() == "resolved");
        Assert.AreEqual(
            @"..\SharedDependency\SharedDependency.csproj",
            projectReference.GetProperty("include").GetString());
        Assert.IsTrue(projectReference.GetProperty("line").GetInt32() > 0);
        Assert.IsTrue(projectReference.GetProperty("outsideSourceRoot").GetBoolean());
        Assert.AreEqual(
            "../SharedDependency/SharedDependency.csproj",
            projectReference.GetProperty("resolvedPath").GetString());
        Assert.IsTrue(projectReferences.Any(reference =>
            reference.GetProperty("include").GetString() == "$(OptionalProject)"
            && reference.GetProperty("resolutionStatus").GetString() == "unresolved"));
        var conditionedReference = projectReferences.Single(reference =>
            reference.GetProperty("include").GetString()
                == @"..\ConditionedDependency\ConditionedDependency.csproj");
        Assert.AreEqual(
            "unresolved-condition",
            conditionedReference.GetProperty("resolutionStatus").GetString());
        Assert.AreEqual(
            "false",
            conditionedReference.GetProperty("condition").GetString());
        Assert.IsFalse(packages.Any(package =>
            package.GetProperty("id").GetString()
                == "Must.Not.Be.Traversed"));
        Assert.AreEqual(0, dependencies.GetProperty("issues").GetArrayLength());
        Assert.DoesNotContain("replacement", dependencies.ToString().ToLowerInvariant());
    }

    [TestMethod]
    public async Task Migrate_ReportsDependencyProjectsThatCannotBeInspected()
    {
        var source = await CreateUwpSourceAsync("UnreadableDependencyApp");
        var sibling = _tempDirectory.CreateSubdirectory("MalformedDependency");
        await WriteAsync(source, "UnreadableDependencyApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>uap10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\MalformedDependency\MalformedDependency.csproj" />
              </ItemGroup>
            </Project>
            """);
        await WriteAsync(
            sibling,
            "MalformedDependency.csproj",
            "<Project><PropertyGroup>");
        var (target, exit, output) = await MigrateAsync(
            source, "unreadable-dependency-output", "UnreadableDependencyAppApp");

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var dependencies = report.RootElement.GetProperty("dependencyAnalysis");
        Assert.AreEqual("incomplete", dependencies.GetProperty("status").GetString());
        var issue = dependencies.GetProperty("issues").EnumerateArray().Single();
        Assert.AreEqual("project-inspection-failed", issue.GetProperty("kind").GetString());
        Assert.AreEqual(
            "../MalformedDependency/MalformedDependency.csproj",
            issue.GetProperty("sourceProject").GetString());
        StringAssert.Contains(output, "1 inspection issue(s)");
    }

    [TestMethod]
    public async Task Migrate_RewritesReswResourceKeyNamespaces()
    {
        var source = await CreateUwpSourceAsync("LocalizedApp");
        await WriteAsync(source, @"Resources\en-us\Resources.resw", """
            <?xml version="1.0" encoding="utf-8"?>
            <root>
              <data name="AddButton.[using:Windows.UI.Xaml.Automation]AutomationProperties.Name" xml:space="preserve">
                <value>Add</value>
              </data>
            </root>
            """);
        var (target, exit, output) = await MigrateAsync(
            source, "localized-output", "LocalizedAppApp");

        Assert.AreEqual(0, exit, output);
        var migrated = await ReadAsync(
            target,
            "Resources",
            "en-us",
            "Resources.resw");
        StringAssert.Contains(
            migrated,
            "[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name");
        Assert.DoesNotContain("Windows.UI.Xaml", migrated);

        using var report = await ReadReportAsync(target);
        var transform = report.RootElement.GetProperty("transforms").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "UWMIG-RESW-NS");
        Assert.AreEqual(1, transform.GetProperty("changedFiles").GetInt32());
        var verification = report.RootElement.GetProperty("mechanicalVerification");
        Assert.AreEqual("passed", verification.GetProperty("status").GetString());
        Assert.AreEqual(0, verification.GetProperty("legacyNamespaceResiduals").GetArrayLength());
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task Migrate_MigratesLocalContentAndPriResourceProjectItems()
    {
        var source = await CreateUwpSourceAsync("ProjectItemsApp");
        await WriteAsync(source, "ProjectItemsApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>uap10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <Content Include="Data\Model.json">
                  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
                </Content>
                <PRIResource Include="Resources\en-us\Resources.resw" />
              </ItemGroup>
            </Project>
            """);
        await WriteAsync(source, @"Data\Model.json", """{"name":"lamp"}""");
        await WriteAsync(source, @"Resources\en-us\Resources.resw", "<root />");
        var (target, exit, output) = await MigrateAsync(
            source, "project-items-output", "ProjectItemsAppApp");

        Assert.AreEqual(0, exit, output);
        var targetProject = XDocument.Load(Path.Combine(target.FullName, "ProjectItemsAppApp.csproj"));
        Assert.IsTrue(targetProject.Descendants().Any(element =>
            element.Name.LocalName == "Content"
            && element.Attribute("Include")?.Value == @"Data\Model.json"));
        Assert.IsTrue(targetProject.Descendants().Any(element =>
            element.Name.LocalName == "None"
            && element.Attribute("Remove")?.Value.Contains(@"Data\Model.json", StringComparison.Ordinal) == true));
        Assert.IsFalse(targetProject.Descendants().Any(element =>
            element.Name.LocalName == "PRIResource"
            && element.Attribute("Include")?.Value == @"Resources\en-us\Resources.resw"));

        using var report = await ReadReportAsync(target);
        var projectItems = report.RootElement
            .GetProperty("mechanicalVerification")
            .GetProperty("projectItems");
        Assert.AreEqual(2, projectItems.GetProperty("sourceItems").GetInt32());
        Assert.AreEqual(2, projectItems.GetProperty("migratedItems").GetInt32());
        Assert.AreEqual(0, projectItems.GetProperty("missingTargetItems").GetArrayLength());
    }

    [TestMethod]
    public async Task Migrate_ReportsConditionalWildcardItemAsRawReviewRequired()
    {
        var source = await CreateUwpSourceAsync("ConditionalItemsApp");
        await WriteAsync(source, "ConditionalItemsApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Content Include="Data\**\*.json" Condition="'$(Configuration)' == 'Debug'" />
              </ItemGroup>
            </Project>
            """);
        await WriteAsync(source, @"Data\Model.json", """{"name":"lamp"}""");
        var (target, exit, output) = await MigrateAsync(
            source, "conditional-items-output", "ConditionalItemsAppApp");

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var projectItems = report.RootElement
            .GetProperty("mechanicalVerification")
            .GetProperty("projectItems");
        Assert.AreEqual(1, projectItems.GetProperty("sourceItems").GetInt32());
        Assert.AreEqual(0, projectItems.GetProperty("migratedItems").GetInt32());
        Assert.AreEqual(1, projectItems.GetProperty("unresolvedItems").GetArrayLength());
        var reviewItem = projectItems
            .GetProperty("decisionRequiredItems")
            .EnumerateArray()
            .Single();
        Assert.AreEqual(
            "conditional",
            reviewItem.GetProperty("reviewReason").GetString());
        StringAssert.Contains(
            reviewItem.GetProperty("condition").GetString(),
            "$(Configuration)");
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray().Any(todo =>
            todo.GetProperty("category").GetString() == "project-items"));
    }

    [TestMethod]
    public async Task Migrate_ConditionFalseItemRemainsReviewRequired()
    {
        var source = await CreateUwpSourceAsync("FalseConditionItemApp");
        await WriteAsync(source, "FalseConditionItemApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Content Include="Data\Model.json" Condition="false" />
              </ItemGroup>
            </Project>
            """);
        await WriteAsync(source, @"Data\Model.json", """{"name":"lamp"}""");
        var (target, exit, output) = await MigrateAsync(
            source, "false-condition-item-output", "FalseConditionItemAppApp");

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var item = report.RootElement
            .GetProperty("mechanicalVerification")
            .GetProperty("projectItems")
            .GetProperty("decisionRequiredItems")
            .EnumerateArray()
            .Single();
        Assert.AreEqual("conditional", item.GetProperty("reviewReason").GetString());
        Assert.AreEqual("false", item.GetProperty("condition").GetString());
    }

    [TestMethod]
    public async Task Migrate_ChooseItemRemainsReviewRequired()
    {
        var source = await CreateUwpSourceAsync("ChooseItemApp");
        await WriteAsync(source, "ChooseItemApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Choose>
                <Otherwise>
                  <ItemGroup>
                    <Content Include="Data\Model.json" />
                  </ItemGroup>
                </Otherwise>
              </Choose>
            </Project>
            """);
        await WriteAsync(source, @"Data\Model.json", """{"name":"lamp"}""");
        var (target, exit, output) = await MigrateAsync(
            source, "choose-item-output", "ChooseItemAppApp");

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var item = report.RootElement
            .GetProperty("mechanicalVerification")
            .GetProperty("projectItems")
            .GetProperty("decisionRequiredItems")
            .EnumerateArray()
            .Single();
        Assert.AreEqual("choose-branch", item.GetProperty("reviewReason").GetString());
        Assert.AreEqual(
            @"Data\Model.json",
            item.GetProperty("include").GetString());
    }

    [TestMethod]
    public async Task Migrate_FailsVerificationForLegacyNamespaceInOtherText()
    {
        var source = await CreateUwpSourceAsync("ResidualApp");
        await WriteAsync(source, "Data.json", """{"type":"Windows.UI.Xaml.Controls.Button"}""");
        var (target, exit, output) = await MigrateAsync(
            source, "residual-output", "ResidualAppApp");

        Assert.AreEqual(1, exit, output);
        StringAssert.Contains(output, "MECHANICAL MIGRATION VERIFICATION FAILED");
        using var report = await ReadReportAsync(target);
        Assert.AreEqual(
            "mechanical-verification-failed",
            report.RootElement.GetProperty("status").GetString());
        var verification = report.RootElement.GetProperty("mechanicalVerification");
        Assert.AreEqual("failed", verification.GetProperty("status").GetString());
        var residual = verification.GetProperty("legacyNamespaceResiduals")[0];
        Assert.AreEqual("Data.json", residual.GetProperty("path").GetString());
        Assert.AreEqual(1, residual.GetProperty("line").GetInt32());
    }

    [TestMethod]
    public async Task Migrate_IgnoresNestedMigrationReportsDuringVerification()
    {
        var source = await CreateUwpSourceAsync("NestedReportApp");
        var target = NewTarget("nested-report-output");
        ArrangeTemplateCreation(target, "NestedReportAppApp", templateOutput =>
        {
            var nestedDirectory = templateOutput.CreateSubdirectory("NestedLibrary");
            File.WriteAllText(
                Path.Combine(nestedDirectory.FullName, "migration-report.json"),
                """{"legacyExample":"Windows.UI.Xaml.Controls.Button"}""");
        });

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        Assert.AreEqual(
            0,
            report.RootElement
                .GetProperty("mechanicalVerification")
                .GetProperty("legacyNamespaceResiduals")
                .GetArrayLength());
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task Migrate_AccountsForEveryEligibleSourceFile()
    {
        var source = await CreateUwpSourceAsync("InventoryApp");
        await WriteAsync(source, "MainPage.xaml", "<Page />");
        await WriteAsync(source, "InventoryApp_TemporaryKey.pfx", "not-a-real-certificate");
        var (target, exit, output) = await MigrateAsync(
            source, "inventory-output", "InventoryAppApp");

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var inventory = report.RootElement
            .GetProperty("mechanicalVerification")
            .GetProperty("inventory");
        Assert.AreEqual(
            inventory.GetProperty("sourceFiles").GetInt32(),
            inventory.GetProperty("classifiedFiles").GetInt32());
        Assert.AreEqual(1, inventory.GetProperty("copiedFiles").GetInt32());
        Assert.AreEqual(2, inventory.GetProperty("preservedReferenceFiles").GetInt32());
        Assert.AreEqual(1, inventory.GetProperty("intentionallyExcludedFiles").GetInt32());
        Assert.AreEqual(0, inventory.GetProperty("unclassifiedFiles").GetArrayLength());
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task Verify_RefreshesMechanicalStateWithoutChangingBehavioralValidation()
    {
        var source = await CreateUwpSourceAsync("VerifyApp");
        await WriteAsync(source, "MainPage.xaml", "<Page />");
        var target = NewTarget("verify-output");
        ArrangeTemplateCreation(target, "VerifyAppApp");
        var (migrateExit, migrateOutput) = await InvokeAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);

        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var reportNode = JsonNode.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken))!;
        reportNode["validation"]!["sourceBaseline"]!["status"] = "captured";
        reportNode["dependencyAnalysis"]!["status"] = "resolved";
        await File.WriteAllTextAsync(
            reportPath,
            reportNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            TestContext.CancellationToken);
        await WriteAsync(
            target,
            "AgentChange.json",
            """{"type":"Windows.UI.Xaml.Controls.Button"}""");

        var (failedExit, failedOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(1, failedExit, failedOutput);
        StringAssert.Contains(failedOutput, "verification failed");
        using (var failedReport = await ReadReportAsync(target))
        {
            Assert.AreEqual(
                "captured",
                failedReport.RootElement
                    .GetProperty("validation")
                    .GetProperty("sourceBaseline")
                    .GetProperty("status")
                    .GetString());
            Assert.AreEqual(
                "resolved",
                failedReport.RootElement
                    .GetProperty("dependencyAnalysis")
                    .GetProperty("status")
                    .GetString());
            Assert.IsTrue(failedReport.RootElement.GetProperty("todos").EnumerateArray().Any(todo =>
                todo.GetProperty("id").GetString() == "UWMIG011"));
        }

        await WriteAsync(
            target,
            "AgentChange.json",
            """{"type":"Microsoft.UI.Xaml.Controls.Button"}""");
        var (passedExit, passedOutput) = await InvokeVerifyAsync(target);

        Assert.AreEqual(0, passedExit, passedOutput);
        StringAssert.Contains(passedOutput, "verification passed");
        using var passedReport = await ReadReportAsync(target);
        Assert.IsFalse(passedReport.RootElement.GetProperty("todos").EnumerateArray().Any(todo =>
            todo.GetProperty("id").GetString() == "UWMIG011"));
        Assert.AreEqual(
            "captured",
            passedReport.RootElement
                .GetProperty("validation")
                .GetProperty("sourceBaseline")
                .GetProperty("status")
                .GetString());
        Assert.AreEqual(
            "resolved",
            passedReport.RootElement
                .GetProperty("dependencyAnalysis")
                .GetProperty("status")
                .GetString());
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task Migrate_LeavesSemanticAndExternalLayoutDecisionsForLater()
    {
        var source = await CreateUwpSourceAsync("BoundaryApp");
        await WriteAsync(source, "App.xaml", """
            <Application>
              <Application.Resources>
                <ResourceDictionary Source="Styles.xaml" />
              </Application.Resources>
            </Application>
            """);
        const string navigationHelper = """
            namespace BoundaryApp.Common;
            public sealed class RootFrameNavigationHelper
            {
                public bool IsEnabled { get; set; } = true;
            }
            """;
        await WriteAsync(source, @"Common\NavigationHelper.cs", navigationHelper);
        await WriteAsync(source, "Styles.xaml", "<ResourceDictionary />");

        var siblingShared = source.Parent!.CreateSubdirectory("shared");
        await WriteAsync(siblingShared, "SiblingOnly.cs", "namespace External.Shared;");

        var (target, exit, output) = await MigrateAsync(
            source, "boundary-output", "BoundaryAppApp");

        Assert.AreEqual(0, exit, output);
        Assert.IsFalse(File.Exists(Path.Combine(target.FullName, "SiblingOnly.cs")));
        Assert.AreEqual(
            navigationHelper,
            await ReadAsync(target, "Common", "NavigationHelper.cs"));

        var targetApp = await ReadAsync(target, "App.xaml");
        Assert.DoesNotContain("Styles.xaml", targetApp);

        var mainWindow = await ReadAsync(target, "MainWindow.xaml");
        Assert.DoesNotContain("RootFrame", mainWindow);

        using var report = await ReadReportAsync(target);
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray().Any(todo =>
            todo.GetProperty("category").GetString() == "app-resources"));
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task Migrate_RewritesSafeVirtualizingStackPanelAndPreservesCompatibleAttributes()
    {
        var source = await CreateUwpSourceAsync("ItemsPanelApp");
        await WriteAsync(source, "Styles.xaml", """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:controls="using:Microsoft.UI.Xaml.Controls">
              <!-- <VirtualizingStackPanel Background="Red" /> -->
              <ItemsPanelTemplate x:Key="LegacyItemsPanel">
                <VirtualizingStackPanel Background="Transparent" Orientation="Horizontal" CacheLength="2" AreStickyGroupHeadersEnabled="False" GroupHeaderPlacement="Top" />
              </ItemsPanelTemplate>
              <ItemsPanelTemplate x:Key="PrefixedItemsPanel">
                <controls:VirtualizingStackPanel Background="Green"></controls:VirtualizingStackPanel>
              </ItemsPanelTemplate>
              <ItemsPanelTemplate x:Key="AlreadyMigrated">
                <ItemsStackPanel Background="Blue" />
              </ItemsPanelTemplate>
              <TextBlock Text="VirtualizingStackPanel" />
            </ResourceDictionary>
            """);
        var (target, exit, output) = await MigrateAsync(
            source, "items-panel-output", "ItemsPanelApp");

        Assert.AreEqual(0, exit, output);
        var migrated = await ReadAsync(target, "Styles.xaml");
        StringAssert.Contains(
            migrated,
            "<ItemsStackPanel Background=\"Transparent\" Orientation=\"Horizontal\" CacheLength=\"2\" AreStickyGroupHeadersEnabled=\"False\" GroupHeaderPlacement=\"Top\" />");
        StringAssert.Contains(
            migrated,
            "<controls:ItemsStackPanel Background=\"Green\"></controls:ItemsStackPanel>");
        StringAssert.Contains(migrated, "<!-- <VirtualizingStackPanel Background=\"Red\" /> -->");
        StringAssert.Contains(migrated, "<ItemsStackPanel Background=\"Blue\" />");
        StringAssert.Contains(migrated, "Text=\"VirtualizingStackPanel\"");

        using var report = await ReadReportAsync(target);
        var transform = report.RootElement.GetProperty("transforms").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "UWMIG-XAML-ITEMS-PANEL");
        Assert.AreEqual(1, transform.GetProperty("changedFiles").GetInt32());
        Assert.IsFalse(report.RootElement.GetProperty("todos").EnumerateArray()
            .Any(todo => todo.GetProperty("category").GetString() == "xaml-items-panel"));
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task Migrate_PreservesAndReportsUnsafeVirtualizingStackPanelOccurrences()
    {
        var source = await CreateUwpSourceAsync("UnsafeItemsPanelApp");
        await WriteAsync(source, "Styles.xaml", """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:local="using:UnsafeItemsPanelApp">
              <VirtualizingStackPanel Background="Transparent" />
              <ItemsPanelTemplate x:Key="UnsupportedAttribute">
                <VirtualizingStackPanel VirtualizingStackPanel.VirtualizationMode="Recycling" />
              </ItemsPanelTemplate>
              <ItemsPanelTemplate x:Key="TypeSpecificStyle">
                <VirtualizingStackPanel Style="{StaticResource LegacyPanelStyle}" />
              </ItemsPanelTemplate>
              <ResourceDictionary>
                <Style TargetType="VirtualizingStackPanel" />
                <ItemsPanelTemplate x:Key="ImplicitTypeSpecificStyle">
                  <VirtualizingStackPanel />
                </ItemsPanelTemplate>
              </ResourceDictionary>
              <ItemsPanelTemplate x:Key="CustomType">
                <local:VirtualizingStackPanel />
              </ItemsPanelTemplate>
              <!-- <VirtualizingStackPanel /> -->
            </ResourceDictionary>
            """);
        var (target, exit, output) = await MigrateAsync(
            source, "unsafe-items-panel-output", "UnsafeItemsPanelApp");

        Assert.AreEqual(0, exit, output);
        var migrated = await ReadAsync(target, "Styles.xaml");
        StringAssert.Contains(migrated, "<VirtualizingStackPanel Background=\"Transparent\" />");
        StringAssert.Contains(
            migrated,
            "<VirtualizingStackPanel VirtualizingStackPanel.VirtualizationMode=\"Recycling\" />");
        StringAssert.Contains(
            migrated,
            "<VirtualizingStackPanel Style=\"{StaticResource LegacyPanelStyle}\" />");
        StringAssert.Contains(migrated, "<local:VirtualizingStackPanel />");
        StringAssert.Contains(migrated, "<!-- <VirtualizingStackPanel /> -->");

        using var report = await ReadReportAsync(target);
        var todo = report.RootElement.GetProperty("todos").EnumerateArray()
            .Single(item => item.GetProperty("category").GetString() == "xaml-items-panel");
        Assert.AreEqual("required", todo.GetProperty("priority").GetString());
        var locations = todo.GetProperty("locations").EnumerateArray().ToList();
        Assert.HasCount(5, locations);
        Assert.AreEqual(4, locations[0].GetProperty("line").GetInt32());
        Assert.AreEqual(6, locations[1].GetProperty("line").GetInt32());
        Assert.AreEqual(9, locations[2].GetProperty("line").GetInt32());
        Assert.AreEqual(14, locations[3].GetProperty("line").GetInt32());
        Assert.AreEqual(18, locations[4].GetProperty("line").GetInt32());
        Assert.IsTrue(locations.All(location =>
            location.GetProperty("path").GetString() == "Styles.xaml"));
    }

    [TestMethod]
    public async Task Migrate_PreservesUtf16EncodingWhenRewritingItemsPanel()
    {
        var source = await CreateUwpSourceAsync("Utf16ItemsPanelApp");
        var stylesPath = Path.Combine(source.FullName, "Styles.xaml");
        const string styles = """
            <?xml version="1.0" encoding="utf-16"?>
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:legacy="using:Windows.UI.Xaml.Controls">
              <ItemsPanelTemplate x:Key="LegacyItemsPanel">
                <VirtualizingStackPanel Orientation="Vertical" />
              </ItemsPanelTemplate>
            </ResourceDictionary>
            """;
        await File.WriteAllTextAsync(
            stylesPath,
            styles,
            new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            TestContext.CancellationToken);
        var (target, exit, output) = await MigrateAsync(
            source, "utf16-items-panel-output", "Utf16ItemsPanelApp");

        Assert.AreEqual(0, exit, output);
        var migratedPath = Path.Combine(target.FullName, "Styles.xaml");
        var bytes = await File.ReadAllBytesAsync(migratedPath, TestContext.CancellationToken);
        CollectionAssert.AreEqual(Encoding.Unicode.GetPreamble(), bytes[..2]);
        var migrated = await File.ReadAllTextAsync(migratedPath, TestContext.CancellationToken);
        StringAssert.Contains(migrated, "<ItemsStackPanel Orientation=\"Vertical\" />");
        StringAssert.Contains(migrated, "xmlns:legacy=\"using:Microsoft.UI.Xaml.Controls\"");
        _ = XDocument.Load(migratedPath);
    }

    [TestMethod]
    public async Task Migrate_ReportsPanelWhenAnotherDictionaryDefinesImplicitLegacyStyle()
    {
        var source = await CreateUwpSourceAsync("GlobalStyleItemsPanelApp");
        await WriteAsync(source, "Styles.xaml", """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Style TargetType="VirtualizingStackPanel" />
            </ResourceDictionary>
            """);
        await WriteAsync(source, "PageResources.xaml", """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <ItemsPanelTemplate x:Key="LegacyItemsPanel">
                <VirtualizingStackPanel />
              </ItemsPanelTemplate>
            </ResourceDictionary>
            """);
        var (target, exit, output) = await MigrateAsync(
            source, "global-style-output", "GlobalStyleItemsPanelApp");

        Assert.AreEqual(0, exit, output);
        var migrated = await ReadAsync(target, "PageResources.xaml");
        StringAssert.Contains(migrated, "<VirtualizingStackPanel />");
        using var report = await ReadReportAsync(target);
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray()
            .Any(todo => todo.GetProperty("category").GetString() == "xaml-items-panel"));
    }

    [TestMethod]
    public async Task Migrate_DoesNotRewriteBomlessLegacyEncodedXaml()
    {
        var source = await CreateUwpSourceAsync("LegacyEncodingItemsPanelApp");
        var stylesPath = Path.Combine(source.FullName, "Styles.xaml");
        var styles = """
            <?xml version="1.0" encoding="windows-1252"?>
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <!-- café -->
              <ItemsPanelTemplate>
                <VirtualizingStackPanel />
              </ItemsPanelTemplate>
            </ResourceDictionary>
            """;
        var originalBytes = Encoding.Latin1.GetBytes(styles);
        await File.WriteAllBytesAsync(
            stylesPath,
            originalBytes,
            TestContext.CancellationToken);
        var (target, exit, output) = await MigrateAsync(
            source, "legacy-encoding-output", "LegacyEncodingItemsPanelApp");

        Assert.AreEqual(0, exit, output);
        CollectionAssert.AreEqual(
            originalBytes,
            await File.ReadAllBytesAsync(
                Path.Combine(target.FullName, "Styles.xaml"),
                TestContext.CancellationToken));
        using var report = await ReadReportAsync(target);
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray()
            .Any(todo => todo.GetProperty("category").GetString() == "xaml-items-panel"));
    }

    [TestMethod]
    public async Task Migrate_LeavesExistingItemsStackPanelUnchanged()
    {
        var source = await CreateUwpSourceAsync("CurrentItemsPanelApp");
        const string styles = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <ItemsPanelTemplate x:Key="CurrentItemsPanel">
                <ItemsStackPanel Background="Transparent" Orientation="Vertical" />
              </ItemsPanelTemplate>
            </ResourceDictionary>
            """;
        await WriteAsync(source, "Styles.xaml", styles);
        var (target, exit, output) = await MigrateAsync(
            source, "current-items-panel-output", "CurrentItemsPanelApp");

        Assert.AreEqual(0, exit, output);
        Assert.AreEqual(
            styles,
            await ReadAsync(target, "Styles.xaml"));
        using var report = await ReadReportAsync(target);
        var transform = report.RootElement.GetProperty("transforms").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "UWMIG-XAML-ITEMS-PANEL");
        Assert.AreEqual(0, transform.GetProperty("changedFiles").GetInt32());
        Assert.IsFalse(report.RootElement.GetProperty("todos").EnumerateArray()
            .Any(todo => todo.GetProperty("category").GetString() == "xaml-items-panel"));
    }

    [TestMethod]
    public async Task Migrate_RewritesDispatcherHasThreadAccessWithoutTodo()
    {
        var source = await CreateUwpSourceAsync("DispatcherApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs", """
            namespace SDKTemplate;
            public partial class MainPage : Page
            {
                void NotifyUser()
                {
                    if (Dispatcher.HasThreadAccess) { }
                }
            }
            """);
        var (target, exit, output) = await MigrateAsync(
            source, "dispatcher-output", "DispatcherApp");

        Assert.AreEqual(0, exit, output);
        var migrated = await ReadAsync(target, "MainPage.xaml.cs");
        StringAssert.Contains(migrated, "DispatcherQueue.HasThreadAccess");
        Assert.IsFalse(migrated.Contains("Dispatcher.HasThreadAccess", StringComparison.Ordinal));

        using var report = await ReadReportAsync(target);
        var todos = report.RootElement.GetProperty("todos").EnumerateArray();
        Assert.IsFalse(todos.Any(todo => todo.GetProperty("category").GetString() == "dispatcher"));
    }

    [TestMethod]
    public async Task Migrate_ReportsDispatcherRunAsyncAsKnownResidual()
    {
        var source = await CreateUwpSourceAsync("AsyncDispatcherApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs", """
            namespace SDKTemplate;
            public partial class MainPage
            {
                async void Update() =>
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { });
            }
            """);
        var (target, exit, output) = await MigrateAsync(
            source, "async-dispatcher-output", "AsyncDispatcherApp");

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var dispatcherTodo = report.RootElement.GetProperty("todos").EnumerateArray()
            .Single(todo => todo.GetProperty("category").GetString() == "dispatcher");
        Assert.AreEqual("required", dispatcherTodo.GetProperty("priority").GetString());
        Assert.AreEqual(1, dispatcherTodo.GetProperty("locations").GetArrayLength());
        Assert.AreEqual(5, dispatcherTodo.GetProperty("locations")[0].GetProperty("line").GetInt32());
        Assert.AreEqual(
            "dispatcher-operation-requires-semantic-port",
            dispatcherTodo.GetProperty("guidance")
                .GetProperty("reasonCode")
                .GetString());
        Assert.IsTrue(dispatcherTodo.GetProperty("guidance")
            .GetProperty("mechanicallyAppliedTransforms")
            .EnumerateArray()
            .Any(transform =>
                transform.GetProperty("id").GetString() ==
                "UWMIG-DISPATCHER"));
    }

    [TestMethod]
    public async Task Migrate_ReportsSystemPickerFlowBeforeContractSealing()
    {
        var source = await CreateUwpSourceAsync("PickerApp");
        await WriteAsync(source, "MainPage.xaml.cs", """
            using Windows.Storage.Pickers;
            namespace SDKTemplate;
            public partial class MainPage
            {
                // FileSavePicker in a comment must not add another location.
                async void Open() =>
                    await new FileOpenPicker().PickSingleFileAsync();
            }
            """);
        var (target, exit, output) = await MigrateAsync(
            source,
            "picker-output",
            "PickerApp");

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var todo = report.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Single(item =>
                item.GetProperty("id").GetString() == "UWMIG016");
        Assert.AreEqual(
            "system-picker",
            todo.GetProperty("category").GetString());
        Assert.AreEqual(
            2,
            todo.GetProperty("locations").GetArrayLength());
        Assert.AreEqual(
            "system-picker-requires-delegated-window-evidence",
            todo.GetProperty("guidance")
                .GetProperty("reasonCode")
                .GetString());
        Assert.IsTrue(todo.GetProperty("guidance")
            .GetProperty("completionConditions")
            .EnumerateArray()
            .Any(condition => condition.GetString()!
                .Contains(
                    "before seal-contract",
                    StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Migrate_ReportsNavigationViewSelectorNullFallbackWithStableGuidance()
    {
        var source = await CreateTemplateSelectorSourceAsync(
            "NullSelectorApp",
            "return null;",
            useNavigationView: true);
        var (target, exit, output) = await MigrateAsync(
            source, "null-selector-output", "NullSelectorApp");

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var todo = report.RootElement.GetProperty("todos").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "UWMIG014");
        Assert.AreEqual("required", todo.GetProperty("priority").GetString());
        var locations = todo.GetProperty("locations").EnumerateArray()
            .Select(location => (
                Path: location.GetProperty("path").GetString(),
                Line: location.GetProperty("line").GetInt32()))
            .ToArray();
        CollectionAssert.Contains(
            locations,
            ("CatalogTemplateSelector.cs", 4));
        CollectionAssert.Contains(
            locations,
            ("MainPage.xaml", 11));

        var guidance = todo.GetProperty("guidance");
        Assert.AreEqual(
            "navigation-view-selector-null-fallback",
            guidance.GetProperty("reasonCode").GetString());
        CollectionAssert.AreEquivalent(
            new[]
            {
                "selector-consumed-by-navigation-view",
                "selector-has-null-fallback"
            },
            guidance.GetProperty("facts").EnumerateArray()
                .Select(fact => fact.GetProperty("id").GetString())
                .ToArray());
        CollectionAssert.AreEquivalent(
            new[]
            {
                "preserve-native-items",
                "provide-non-null-default-template"
            },
            guidance.GetProperty("candidateStrategies").EnumerateArray()
                .Select(strategy => strategy.GetProperty("id").GetString())
                .ToArray());
        Assert.IsTrue(guidance.GetProperty("forbiddenShortcuts")
            .EnumerateArray()
            .Any(item => item.GetString()!.Contains(
                "generic TextBlock",
                StringComparison.Ordinal)));
        Assert.IsTrue(guidance.GetProperty("forbiddenShortcuts")
            .EnumerateArray()
            .Any(item => item.GetString()!.Contains(
                "NavigationViewItem ControlTemplate",
                StringComparison.Ordinal)));
        Assert.IsTrue(guidance.GetProperty("completionConditions")
            .EnumerateArray()
            .Any(item => item.GetString()!.Contains(
                "selected-state property",
                StringComparison.Ordinal)));
        Assert.AreEqual(
            0,
            guidance.GetProperty("mechanicallyAppliedTransforms")
                .GetArrayLength());
    }

    [TestMethod]
    [DataRow(
        "DefaultSelectorApp",
        "return DefaultTemplate;",
        true,
        false)]
    [DataRow(
        "BaseFallbackSelectorApp",
        "return DefaultTemplate ?? base.SelectTemplateCore(item);",
        true,
        true)]
    [DataRow(
        "ListSelectorApp",
        "return null;",
        false,
        false)]
    public async Task Migrate_TemplateSelectorFallbackReportsTodoOnlyWhenConsumedByNavigationView(
        string appName,
        string fallbackStatement,
        bool useNavigationView,
        bool expectedTodo)
    {
        var source = await CreateTemplateSelectorSourceAsync(
            appName,
            fallbackStatement,
            useNavigationView);
        var target = NewTarget($"{appName}-output");
        ArrangeTemplateCreation(target, appName);

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        Assert.AreEqual(
            expectedTodo,
            report.RootElement.GetProperty("todos").EnumerateArray()
                .Any(item => item.GetProperty("id").GetString() == "UWMIG014"));
    }

    [TestMethod]
    public async Task Migrate_ReportsInlineNavigationViewSelectorSwitchFallback()
    {
        var source = await CreateUwpSourceAsync("InlineSelectorApp");
        await WriteAsync(source, "MainPage.xaml", """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  xmlns:selectors="using:Independent.Navigation">
              <Page.Resources>
                <DataTemplate x:Key="TextTemplate"><TextBlock Text="{Binding}" /></DataTemplate>
              </Page.Resources>
              <NavigationView>
                <NavigationView.MenuItemTemplateSelector>
                  <selectors:MixedItemSelector TextTemplate="{StaticResource TextTemplate}" />
                </NavigationView.MenuItemTemplateSelector>
              </NavigationView>
            </Page>
            """);
        await WriteAsync(source, "MixedItemSelector.cs", """
            using Windows.UI.Xaml;
            using Windows.UI.Xaml.Controls;
            namespace Independent.Navigation;
            public sealed class MixedItemSelector : DataTemplateSelector
            {
                public DataTemplate TextTemplate { get; set; }

                protected override DataTemplate SelectTemplateCore(
                    object item,
                    DependencyObject container) =>
                    item switch
                    {
                        string => TextTemplate,
                        _ => base.SelectTemplateCore(item, container)
                    };
            }
            """);
        var (target, exit, output) = await MigrateAsync(
            source, "inline-selector-output", "InlineSelectorApp");

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var todo = report.RootElement.GetProperty("todos").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "UWMIG014");
        CollectionAssert.AreEquivalent(
            new[] { "MixedItemSelector.cs", "MainPage.xaml" },
            todo.GetProperty("locations").EnumerateArray()
                .Select(location => location.GetProperty("path").GetString())
                .ToArray());
    }

    [TestMethod]
    public async Task Migrate_NullReturningSelectorHelperDoesNotReportTodo()
    {
        var source = await CreateUwpSourceAsync("SelectorHelperApp");
        await WriteAsync(source, "MainPage.xaml", """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  xmlns:local="using:Independent.Navigation">
              <Page.Resources>
                <DataTemplate x:Key="DefaultTemplate"><TextBlock Text="Default" /></DataTemplate>
                <local:SafeSelector x:Key="SafeSelector"
                                    DefaultTemplate="{StaticResource DefaultTemplate}" />
              </Page.Resources>
              <NavigationView MenuItemTemplateSelector="{StaticResource SafeSelector}" />
            </Page>
            """);
        await WriteAsync(source, "SafeSelector.cs", """
            using Windows.UI.Xaml;
            using Windows.UI.Xaml.Controls;
            namespace Independent.Navigation;
            public sealed class SafeSelector : DataTemplateSelector
            {
                public DataTemplate DefaultTemplate { get; set; }

                protected override DataTemplate SelectTemplateCore(object item)
                {
                    return DefaultTemplate;
                }

                private static DataTemplate UnusedFallback()
                {
                    return null;
                }
            }
            """);
        var (target, exit, output) = await MigrateAsync(
            source, "selector-helper-output", "SelectorHelperApp");

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        Assert.IsFalse(report.RootElement.GetProperty("todos").EnumerateArray()
            .Any(item => item.GetProperty("id").GetString() == "UWMIG014"));
    }

    [TestMethod]
    public async Task Verify_DoesNotDuplicateOrDestabilizeNavigationViewSelectorTodo()
    {
        var source = await CreateTemplateSelectorSourceAsync(
            "RepeatSelectorApp",
            "return base.SelectTemplateCore(item);",
            useNavigationView: true);
        var target = NewTarget("repeat-selector-output");
        ArrangeTemplateCreation(target, "RepeatSelectorApp");
        var (migrateExit, migrateOutput) = await InvokeAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);

        var (firstExit, firstOutput) = await InvokeVerifyAsync(target);
        Assert.AreEqual(0, firstExit, firstOutput);
        using var firstReport = await ReadReportAsync(target);
        var firstTodo = firstReport.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "UWMIG014");
        var firstGuidance = firstTodo.GetProperty("guidance").GetRawText();
        var firstLocations = firstTodo.GetProperty("locations").GetRawText();
        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var reportNode = JsonNode.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken))!;
        reportNode["todos"]!.AsArray()
            .Single(item => item!["id"]!.GetValue<string>() == "UWMIG014")![
                "status"] = "accepted";
        await File.WriteAllTextAsync(
            reportPath,
            reportNode.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }),
            TestContext.CancellationToken);
        var rollbackRoot = Path.Combine(
            target.FullName,
            ".migration-evidence",
            "enforcement",
            "work-items",
            "packet",
            "rollback");
        Directory.CreateDirectory(rollbackRoot);
        File.Copy(
            Path.Combine(target.FullName, "MainPage.xaml"),
            Path.Combine(rollbackRoot, "MainPage.xaml"));
        File.Copy(
            Path.Combine(target.FullName, "CatalogTemplateSelector.cs"),
            Path.Combine(rollbackRoot, "CatalogTemplateSelector.cs"));

        var (secondExit, secondOutput) = await InvokeVerifyAsync(target);
        Assert.AreEqual(0, secondExit, secondOutput);
        using var secondReport = await ReadReportAsync(target);
        var matchingTodos = secondReport.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Where(item => item.GetProperty("id").GetString() == "UWMIG014")
            .ToArray();

        Assert.HasCount(1, matchingTodos);
        Assert.AreEqual(firstGuidance, matchingTodos[0]
            .GetProperty("guidance").GetRawText());
        Assert.AreEqual(firstLocations, matchingTodos[0]
            .GetProperty("locations").GetRawText());
        Assert.AreEqual(
            "accepted",
            matchingTodos[0].GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task Migrate_ReportsWindowCurrentBoundsAsWindowSizingResidual()
    {
        var source = await CreateUwpSourceAsync("WindowApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs", """
            namespace SDKTemplate;
            public partial class MainPage
            {
                double Width() => Window.Current.Bounds.Width;
            }
            """);
        var (target, exit, output) = await MigrateAsync(
            source, "window-output", "WindowApp");

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var windowSizingTodo = report.RootElement.GetProperty("todos").EnumerateArray()
            .Single(todo => todo.GetProperty("category").GetString() == "window-sizing");
        Assert.AreEqual("required", windowSizingTodo.GetProperty("priority").GetString());
        Assert.AreEqual(4, windowSizingTodo.GetProperty("locations")[0].GetProperty("line").GetInt32());
        StringAssert.Contains(windowSizingTodo.GetProperty("reason").GetString(), "XamlRoot.Size");
        Assert.IsFalse(report.RootElement.GetProperty("todos").EnumerateArray()
            .Any(todo => todo.GetProperty("category").GetString() == "windowing"));
    }

    [TestMethod]
    public async Task Migrate_ReportsOtherWindowCurrentAsWindowingResidual()
    {
        var source = await CreateUwpSourceAsync("WindowContentApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs", """
            namespace SDKTemplate;
            public partial class MainPage
            {
                object? Content() => Window.Current.Content;
            }
            """);
        var (target, exit, output) = await MigrateAsync(
            source, "window-content-output", "WindowContentApp");

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var windowTodo = report.RootElement.GetProperty("todos").EnumerateArray()
            .Single(todo => todo.GetProperty("category").GetString() == "windowing");
        Assert.AreEqual(
            "window-current-requires-explicit-owner",
            windowTodo.GetProperty("guidance")
                .GetProperty("reasonCode")
                .GetString());
        Assert.IsFalse(report.RootElement.GetProperty("todos").EnumerateArray()
            .Any(todo => todo.GetProperty("category").GetString() == "window-sizing"));
    }

    [TestMethod]
    public async Task Migrate_ReportsDisplayInformationWithApiSpecificGuidance()
    {
        var source = await CreateUwpSourceAsync("DisplayInformationApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs", """
            using Windows.Graphics.Display;
            namespace SDKTemplate;
            public partial class MainPage
            {
                DisplayInformation Current() => DisplayInformation.GetForCurrentView();
            }
            """);
        var (target, exit, output) = await MigrateAsync(
            source, "display-information-output", "DisplayInformationApp");

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        var todo = report.RootElement.GetProperty("todos").EnumerateArray()
            .Single(item => item.GetProperty("category").GetString() == "display-information");
        StringAssert.Contains(todo.GetProperty("reason").GetString(), "Do not invent IDisplayInformationStaticsInterop");
        StringAssert.Contains(todo.GetProperty("reason").GetString(), "MonitorFromWindow");
        StringAssert.Contains(todo.GetProperty("reason").GetString(), "GetMonitorInfo");
        StringAssert.Contains(todo.GetProperty("reason").GetString(), "EnumDisplaySettings");
    }

    [TestMethod]
    public async Task Migrate_DoesNotRewriteDispatcherTextOrAmbiguousReceiver()
    {
        var source = await CreateUwpSourceAsync("AmbiguousDispatcherApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        await WriteAsync(source, "MainPage.xaml.cs", """
            namespace SDKTemplate;
            public partial class MainPage : Page
            {
                string Description = "Dispatcher.HasThreadAccess";
                // Dispatcher.HasThreadAccess must not be changed in this comment.
                bool Access() => Dispatcher.HasThreadAccess;
            }
            """);
        await WriteAsync(source, "Worker.cs", """
            public class Worker
            {
                bool Access() => Dispatcher.HasThreadAccess;
            }
            """);
        var (target, exit, output) = await MigrateAsync(
            source, "ambiguous-dispatcher-output", "AmbiguousDispatcherApp");

        Assert.AreEqual(0, exit, output);
        var page = await ReadAsync(target, "MainPage.xaml.cs");
        StringAssert.Contains(page, "\"Dispatcher.HasThreadAccess\"");
        StringAssert.Contains(page, "// Dispatcher.HasThreadAccess must not be changed");
        StringAssert.Contains(page, "DispatcherQueue.HasThreadAccess");
        var worker = await ReadAsync(target, "Worker.cs");
        StringAssert.Contains(worker, "Dispatcher.HasThreadAccess");

        using var report = await ReadReportAsync(target);
        var dispatcherTodo = report.RootElement.GetProperty("todos").EnumerateArray()
            .Single(todo => todo.GetProperty("category").GetString() == "dispatcher");
        Assert.IsTrue(dispatcherTodo.GetProperty("locations").EnumerateArray()
            .Any(location => location.GetProperty("path").GetString() == "Worker.cs"));
    }

    [TestMethod]
    public async Task Migrate_PreservesSkippedAppFilesForTodo()
    {
        var source = await CreateUwpSourceAsync("ResourcesApp");
        await WriteAsync(source, "App.xaml", "<Application><Application.Resources><Color x:Key=\"Accent\">Red</Color></Application.Resources></Application>");
        await WriteAsync(source, "App.xaml.cs", "class UwpApp { }");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        var (target, exit, output) = await MigrateAsync(
            source, "resources-output", "ResourcesApp");

        Assert.AreEqual(0, exit, output);
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, ".uwp-source", "App.xaml.reference")));
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, ".uwp-source", "App.xaml.cs.reference")));
        using var report = await ReadReportAsync(target);
        Assert.IsTrue(report.RootElement.GetProperty("todos").EnumerateArray()
            .Any(todo => todo.GetProperty("category").GetString() == "app-resources"));
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task Migrate_TemplateFailureRemovesNewPartialOutput()
    {
        var source = await CreateUwpSourceAsync("FailedTemplateApp");
        var target = NewTarget("failed-output");
        FakeDotNet.RunDotnetCommandHandler = arguments =>
        {
            var templateOutput = GetTemplateOutput(arguments);
            templateOutput.Create();
            File.WriteAllText(Path.Combine(templateOutput.FullName, "partial.txt"), "partial");
            return (1, string.Empty, "Template not installed.");
        };

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "Template not installed.");
        Assert.IsFalse(target.Exists);
    }

    [TestMethod]
    public async Task Migrate_MalformedTemplateRestoresExistingEmptyOutput()
    {
        var source = await CreateUwpSourceAsync("MalformedTemplateApp");
        var target = _tempDirectory.CreateSubdirectory("empty-output");
        FakeDotNet.RunDotnetCommandHandler = arguments =>
        {
            var templateOutput = GetTemplateOutput(arguments);
            templateOutput.Create();
            File.WriteAllText(Path.Combine(templateOutput.FullName, "partial.txt"), "partial");
            return (0, "Template claimed success.", string.Empty);
        };

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "did not produce the expected project files");
        Assert.IsTrue(target.Exists);
        Assert.IsFalse(target.EnumerateFileSystemInfos().Any());
    }

    [TestMethod]
    public async Task Migrate_QuietRemainsActiveAcrossAsyncTemplateCreation()
    {
        var source = await CreateUwpSourceAsync("QuietApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        var target = NewTarget("quiet-output");
        FakeDotNet.RunDotnetCommandAsyncHandler = async arguments =>
        {
            await Task.Yield();
            var templateOutput = GetTemplateOutput(arguments);
            templateOutput.Create();
            File.WriteAllText(Path.Combine(templateOutput.FullName, "QuietAppApp.csproj"), CleanCsproj);
            File.WriteAllText(Path.Combine(templateOutput.FullName, "App.xaml"),
                "<Application><Application.Resources><ResourceDictionary /></Application.Resources></Application>");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "App.xaml.cs"), "class App { }");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "MainWindow.xaml"), "<Window><Grid Grid.Row=\"1\" /></Window>");
            File.WriteAllText(Path.Combine(templateOutput.FullName, "MainWindow.xaml.cs"),
                "class MainWindow { MainWindow() { InitializeComponent(); } }");
            return (0, "Template created.", string.Empty);
        };

        var (exit, output) = await InvokeAsync(source, target, "--quiet");

        Assert.AreEqual(0, exit, output);
        Assert.IsFalse(output.Contains("MECHANICAL MIGRATION COMPLETE", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("Copied", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Migrate_QuietPreservesFailureLine()
    {
        var source = _tempDirectory.CreateSubdirectory("quiet-invalid-source");
        await WriteAsync(source, "MainPage.xaml", "<Page />");
        var target = NewTarget("quiet-invalid-output");

        var (exit, output) = await InvokeAsync(
            source,
            target,
            "--quiet");

        Assert.AreEqual(1, exit);
        StringAssert.Contains(
            output,
            "[ERROR] Source is not a UWP project");
        Assert.IsFalse(
            output.Contains("==> winapp-migrate uwp create", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Migrate_UsesSourceInventoryFrozenBeforeTemplateCreation()
    {
        var source = await CreateUwpSourceAsync("FrozenInventoryApp");
        await WriteAsync(source, "Before.txt", "before");
        var target = NewTarget("frozen-inventory-output");
        ArrangeTemplateCreation(
            target,
            "FrozenInventoryApp",
            _ => File.WriteAllText(
                Path.Combine(source.FullName, "After.txt"),
                "after"));

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "Before.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(target.FullName, "After.txt")));
    }

    [TestMethod]
    public async Task Migrate_TargetUnderJunctionAliasFailsBeforeTemplateCreation()
    {
        var source = await CreateUwpSourceAsync("JunctionAliasApp");
        var junctionPath = Path.Combine(
            _tempDirectory.FullName,
            "source-junction");
        if (!TryCreateDirectoryJunction(
                junctionPath,
                source.FullName,
                out var junctionError))
        {
            Assert.Inconclusive(
                $"Directory junctions are unavailable: {junctionError}");
        }

        try
        {
            var target = new DirectoryInfo(Path.Combine(
                junctionPath,
                "nested-output"));

            var (exit, output) = await InvokeAsync(source, target);

            Assert.AreEqual(1, exit);
            StringAssert.Contains(output, "reparse-point ancestor");
            Assert.IsEmpty(FakeDotNet.CommandCalls);
            Assert.IsFalse(target.Exists);
        }
        finally
        {
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }
        }
    }

    [TestMethod]
    public async Task Migrate_SourceSymlinkAliasFailsBeforeTemplateCreation()
    {
        var source = await CreateUwpSourceAsync("SymlinkAliasApp");
        var symlinkPath = Path.Combine(
            _tempDirectory.FullName,
            "source-symlink");
        try
        {
            Directory.CreateSymbolicLink(symlinkPath, source.FullName);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            Assert.Inconclusive(
                $"Directory symbolic links are unavailable: {exception.Message}");
        }

        try
        {
            var target = new DirectoryInfo(Path.Combine(
                source.FullName,
                "symlink-alias-output"));

            var (exit, output) = await InvokeAsync(
                new DirectoryInfo(symlinkPath),
                target);

            Assert.AreEqual(1, exit);
            StringAssert.Contains(output, "reparse-point ancestor");
            Assert.IsEmpty(FakeDotNet.CommandCalls);
            Assert.IsFalse(target.Exists);
        }
        finally
        {
            if (Directory.Exists(symlinkPath))
            {
                Directory.Delete(symlinkPath);
            }
        }
    }

    [TestMethod]
    public async Task Migrate_DoesNotTraverseSourceDirectoryJunction()
    {
        var source = await CreateUwpSourceAsync("JunctionTraversalApp");
        var external = _tempDirectory.CreateSubdirectory(
            "junction-external");
        await WriteAsync(external, "External.txt", "external");
        var junctionPath = Path.Combine(source.FullName, "External");
        if (!TryCreateDirectoryJunction(
                junctionPath,
                external.FullName,
                out var junctionError))
        {
            Assert.Inconclusive(
                $"Directory junctions are unavailable: {junctionError}");
        }

        try
        {
            var target = NewTarget("junction-traversal-output");
            ArrangeTemplateCreation(target, "JunctionTraversalApp");

            var (exit, output) = await InvokeAsync(source, target);

            Assert.AreEqual(0, exit, output);
            Assert.IsFalse(Directory.Exists(
                Path.Combine(target.FullName, "External")));
            Assert.IsFalse(File.Exists(
                Path.Combine(target.FullName, "External", "External.txt")));
        }
        finally
        {
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }
        }
    }

    [TestMethod]
    public async Task Migrate_DoesNotCopySourceFileSymlink()
    {
        var source = await CreateUwpSourceAsync("FileSymlinkApp");
        var external = _tempDirectory.CreateSubdirectory(
            "file-symlink-external");
        var externalFile = Path.Combine(external.FullName, "External.txt");
        await File.WriteAllTextAsync(
            externalFile,
            "external",
            TestContext.CancellationToken);
        var symlinkPath = Path.Combine(source.FullName, "Linked.txt");
        try
        {
            File.CreateSymbolicLink(symlinkPath, externalFile);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            Assert.Inconclusive(
                $"File symbolic links are unavailable: {exception.Message}");
        }

        try
        {
            var target = NewTarget("file-symlink-output");
            ArrangeTemplateCreation(target, "FileSymlinkApp");

            var (exit, output) = await InvokeAsync(source, target);

            Assert.AreEqual(0, exit, output);
            Assert.IsFalse(File.Exists(
                Path.Combine(target.FullName, "Linked.txt")));
        }
        finally
        {
            if (File.Exists(symlinkPath))
            {
                File.Delete(symlinkPath);
            }
        }
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task Migrate_NonEmptyOutputFailsBeforeTemplateCreation()
    {
        var source = await CreateUwpSourceAsync("ExistingOutputApp");
        var target = _tempDirectory.CreateSubdirectory("existing-output");
        await WriteAsync(target, "keep.txt", "existing");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "[ERROR] Output directory may contain only supported control-plane metadata");
        Assert.IsEmpty(FakeDotNet.CommandCalls);
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "keep.txt")));
    }

    [TestMethod]
    public async Task Migrate_MetadataOnlyOutputPreservesMetadataAndMergesScaffold()
    {
        var source = await CreateUwpSourceAsync("MetadataOutputApp");
        await WriteAsync(source, "MainPage.xaml", "<Page x:Class=\"SDKTemplate.MainPage\" />");
        var target = _tempDirectory.CreateSubdirectory("metadata-output");
        await WriteAsync(target, ".git\\HEAD", "ref: refs/heads/main\n");
        await WriteAsync(target, ".github\\instructions\\existing.md", "existing instructions");
        ArrangeTemplateCreation(target, "MetadataOutputApp", template =>
        {
            var instructions = Directory.CreateDirectory(Path.Combine(template.FullName, ".github", "instructions"));
            File.WriteAllText(Path.Combine(instructions.FullName, "template.md"), "template instructions");
        });

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        Assert.AreEqual(
            "ref: refs/heads/main\n",
            await ReadAsync(target, ".git", "HEAD"));
        Assert.AreEqual(
            "existing instructions",
            await ReadAsync(target, ".github", "instructions", "existing.md"));
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, ".github", "instructions", "template.md")));
        Assert.IsFalse(Directory.Exists(Path.Combine(target.FullName, ".github", ".github")));
        Assert.IsTrue(File.Exists(Path.Combine(target.FullName, "MetadataOutputApp.csproj")));

        using var report = await ReadReportAsync(target);
        Assert.AreEqual(
            target.FullName.Replace('\\', '/'),
            report.RootElement.GetProperty("target").GetProperty("root").GetString());
    }

    [TestMethod]
    public async Task Migrate_PortableReportAndPackageIdentityProduceRelocatableDeterministicOutput()
    {
        var source = await CreateUwpSourceAsync("PortableOutputApp");
        var target = NewTarget("portable-output");
        ArrangeTemplateCreation(target, "PortableOutputApp", template =>
        {
            File.WriteAllText(
                Path.Combine(template.FullName, "Package.appxmanifest"),
                """
                <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                         xmlns:mp="http://schemas.microsoft.com/appx/2014/phone/manifest">
                  <Identity Name="11111111-1111-1111-1111-111111111111" />
                  <mp:PhoneIdentity PhoneProductId="22222222-2222-2222-2222-222222222222" />
                </Package>
                """);
        });
        const string packageIdentity = "ABCDEF01-2345-6789-ABCD-EF0123456789";

        var (exit, output) = await InvokeAsync(
            source,
            target,
            "--portable-report",
            "--package-identity",
            packageIdentity);

        Assert.AreEqual(0, exit, output);
        using var report = await ReadReportAsync(target);
        Assert.AreEqual(
            ".",
            report.RootElement.GetProperty("target").GetProperty("root").GetString());
        var manifest = XDocument.Load(Path.Combine(target.FullName, "Package.appxmanifest"));
        Assert.AreEqual(
            packageIdentity,
            manifest.Descendants()
                .Single(element => element.Name.LocalName == "Identity")
                .Attribute("Name")?.Value);
        Assert.AreEqual(
            packageIdentity,
            manifest.Descendants()
                .Single(element => element.Name.LocalName == "PhoneIdentity")
                .Attribute("PhoneProductId")?.Value);
    }

    [TestMethod]
    public async Task Migrate_MetadataConflictLeavesExistingOutputUnchanged()
    {
        var source = await CreateUwpSourceAsync("MetadataConflictApp");
        var target = _tempDirectory.CreateSubdirectory("metadata-conflict-output");
        await WriteAsync(target, ".git\\HEAD", "original head");
        await WriteAsync(target, ".github\\instructions\\conflict.md", "original instructions");
        ArrangeTemplateCreation(target, "MetadataConflictApp", template =>
        {
            var instructions = Directory.CreateDirectory(Path.Combine(template.FullName, ".github", "instructions"));
            File.WriteAllText(Path.Combine(instructions.FullName, "conflict.md"), "different instructions");
        });

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "conflicts with an existing metadata file");
        Assert.AreEqual(
            "original head",
            await ReadAsync(target, ".git", "HEAD"));
        Assert.AreEqual(
            "original instructions",
            await ReadAsync(target, ".github", "instructions", "conflict.md"));
        Assert.AreEqual(2, target.EnumerateFileSystemInfos().Count());
    }

    [TestMethod]
    public async Task Migrate_TemplateFailurePreservesMetadataOnlyOutput()
    {
        var source = await CreateUwpSourceAsync("MetadataFailureApp");
        var target = _tempDirectory.CreateSubdirectory("metadata-failure-output");
        await WriteAsync(target, ".git\\HEAD", "original head");
        await WriteAsync(target, ".github\\instructions\\existing.md", "original instructions");
        FakeDotNet.RunDotnetCommandHandler = arguments =>
        {
            var templateOutput = GetTemplateOutput(arguments);
            templateOutput.Create();
            File.WriteAllText(Path.Combine(templateOutput.FullName, "partial.txt"), "partial");
            return (1, string.Empty, "Template failed.");
        };

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "Template failed.");
        Assert.AreEqual(
            "original head",
            await ReadAsync(target, ".git", "HEAD"));
        Assert.AreEqual(
            "original instructions",
            await ReadAsync(target, ".github", "instructions", "existing.md"));
        Assert.AreEqual(2, target.EnumerateFileSystemInfos().Count());
    }

    [TestMethod]
    [TestCategory("MigrationAcceptance")]
    public async Task Migrate_SourceWithoutProjectFails()
    {
        var source = _tempDirectory.CreateSubdirectory("not-uwp");
        await WriteAsync(source, "MainPage.xaml", "<Page />");
        var target = NewTarget("invalid-source-output");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "[ERROR] Source is not a UWP project");
        Assert.IsEmpty(FakeDotNet.CommandCalls);
    }
}
