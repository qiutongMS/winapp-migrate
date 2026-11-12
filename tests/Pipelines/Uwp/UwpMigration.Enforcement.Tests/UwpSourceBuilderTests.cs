// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Text.Json;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

[TestClass]
public sealed class UwpSourceBuilderTests
{
    [TestMethod]
    public async Task InspectItems_UsesEvaluatedAbsoluteLinkedPathsWithoutBuildOrRestore()
    {
        using var fixture = await BuilderFixture.CreateAsync();
        var linked = Path.GetFullPath(Path.Combine(fixture.Project.DirectoryName!, "..", "Shared.xaml"));
        var output = JsonSerializer.Serialize(new
        {
            Items = new
            {
                Compile = Array.Empty<object>(),
                Page = new[] { new { Identity = "..\\Shared.xaml", FullPath = linked, DefiningProjectFullPath = fixture.Project.FullName } },
                ApplicationDefinition = Array.Empty<object>(),
                ProjectReference = Array.Empty<object>()
            }
        });
        var process = new BuildProcessRunner(() => new EnforcementProcessResult(0, output, ""));
        var builder = new UwpSourceBuilder(process, new FakePackageDependencyService(),
            () => fixture.MsBuild, () => [new Version(10, 0, 22621, 0)]);

        var items = await builder.InspectItemsAsync(fixture.Project, CancellationToken.None);

        Assert.HasCount(1, items);
        Assert.AreEqual(linked, items[0].Path);
        CollectionAssert.Contains(process.Arguments, "-getItem:Compile,Page,ApplicationDefinition,ProjectReference");
        Assert.IsFalse(process.Arguments.Any(argument =>
            argument.Contains("restore", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("-target", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Build_UsesVisualStudioMsBuildAndPreparesRestoredDependencies()
    {
        using var fixture = await BuilderFixture.CreateAsync();
        var process = new BuildProcessRunner(() =>
            fixture.CreateLayoutWithDependency());
        var packages = new FakePackageDependencyService();
        var builder = new UwpSourceBuilder(
            process,
            packages,
            () => fixture.MsBuild,
            () => [new Version(10, 0, 22621, 0)]);

        var result = await builder.BuildAsync(
            fixture.Project,
            "Release",
            "x64",
            CancellationToken.None);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("10.0.22621.0", result.TargetPlatformVersion);
        Assert.AreEqual(fixture.Layout.FullName, result.LayoutDirectory!.FullName);
        Assert.AreEqual(fixture.MsBuild.FullName, process.Executable);
        CollectionAssert.Contains(
            process.Arguments,
            "-property:Configuration=Release");
        CollectionAssert.Contains(
            process.Arguments,
            "-property:Platform=x64");
        CollectionAssert.Contains(
            process.Arguments,
            "-property:TargetPlatformVersion=10.0.22621.0");
        Assert.HasCount(1, packages.InstalledPackages);
        StringAssert.EndsWith(
            packages.InstalledPackages[0],
            "Microsoft.VCLibs.140.00.appx");
    }

    [TestMethod]
    public async Task Build_ReturnsMsBuildFailureWithoutResolvingLayout()
    {
        using var fixture = await BuilderFixture.CreateAsync();
        var process = new BuildProcessRunner(
            () => new EnforcementProcessResult(7, "build output", "build error"));
        var builder = new UwpSourceBuilder(
            process,
            new FakePackageDependencyService(),
            () => fixture.MsBuild,
            () => [new Version(10, 0, 22621, 0)]);

        var result = await builder.BuildAsync(
            fixture.Project,
            null,
            null,
            CancellationToken.None);

        Assert.AreEqual(7, result.ExitCode);
        Assert.IsNull(result.LayoutDirectory);
        Assert.AreEqual("build output", result.StandardOutput);
        CollectionAssert.Contains(
            process.Arguments,
            "-property:Configuration=Debug");
    }

    [TestMethod]
    public async Task Build_RejectsMissingVisualStudioUwpTools()
    {
        using var fixture = await BuilderFixture.CreateAsync();
        var process = new BuildProcessRunner();
        var builder = new UwpSourceBuilder(
            process,
            new FakePackageDependencyService(),
            () => null,
            () => [new Version(10, 0, 22621, 0)]);

        var exception =
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                builder.BuildAsync(
                    fixture.Project,
                    "Debug",
                    "x64",
                    CancellationToken.None));

        StringAssert.Contains(exception.Message, "Visual Studio MSBuild");
        Assert.IsNull(process.Executable);
    }

    [TestMethod]
    public void SelectTargetSdk_UsesExactRequestThenNewestCompatible()
    {
        Version[] installed =
        [
            new(10, 0, 19041, 0),
            new(10, 0, 22621, 0),
            new(10, 0, 26100, 0)
        ];

        Assert.AreEqual(
            new Version(10, 0, 22621, 0),
            UwpSourceBuilder.SelectTargetSdk(
                "10.0.22621.0",
                "10.0.17763.0",
                installed));
        Assert.AreEqual(
            new Version(10, 0, 26100, 0),
            UwpSourceBuilder.SelectTargetSdk(
                "10.0.22000.0",
                "10.0.19041.0",
                installed));
        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            UwpSourceBuilder.SelectTargetSdk(
                null,
                "10.0.30000.0",
                installed));
        StringAssert.Contains(exception.Message, "10.0.30000.0");
    }

    [TestMethod]
    public async Task ResolveLayout_RequiresRequestedConfigurationAndArchitecture()
    {
        using var fixture = await BuilderFixture.CreateAsync();
        var stale = Directory.CreateDirectory(
            Path.Combine(
                fixture.Root.FullName,
                "bin",
                "x86",
                "Debug"));
        await File.WriteAllTextAsync(
            Path.Combine(stale.FullName, "AppxManifest.xml"),
            "<Package />");

        Assert.IsNull(UwpSourceBuilder.ResolveLayoutDirectory(
            fixture.Project,
            "Release",
            "x64"));
    }

    private sealed class BuildProcessRunner(
        Func<EnforcementProcessResult>? run = null)
        : IEnforcementProcessRunner
    {
        private readonly Func<EnforcementProcessResult> _run =
            run ?? (() => new EnforcementProcessResult(0, "", ""));

        internal string? Executable { get; private set; }
        internal string[] Arguments { get; private set; } = [];

        public Task<EnforcementProcessResult> RunAsync(
            string executable,
            DirectoryInfo workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Executable = executable;
            Arguments = [.. arguments];
            return Task.FromResult(_run());
        }
    }

    private sealed class FakePackageDependencyService
        : IUwpPackageDependencyService
    {
        internal List<string> InstalledPackages { get; } = [];

        public string? GetInstalledVersion(
            string packageName,
            string architecture) =>
            null;

        public Task InstallPackageAsync(
            string packagePath,
            CancellationToken cancellationToken)
        {
            InstalledPackages.Add(packagePath);
            return Task.CompletedTask;
        }
    }

    private sealed class BuilderFixture : IDisposable
    {
        private BuilderFixture(
            DirectoryInfo root,
            FileInfo project,
            FileInfo msBuild)
        {
            Root = root;
            Project = project;
            MsBuild = msBuild;
            Layout = new DirectoryInfo(
                Path.Combine(root.FullName, "bin", "x64", "Release"));
        }

        internal DirectoryInfo Root { get; }
        internal FileInfo Project { get; }
        internal FileInfo MsBuild { get; }
        internal DirectoryInfo Layout { get; }

        internal static async Task<BuilderFixture> CreateAsync()
        {
            var root = TestDirectories.Create("uwp-source-builder");
            var project = new FileInfo(
                Path.Combine(root.FullName, "Source.csproj"));
            await File.WriteAllTextAsync(
                project.FullName,
                """
                <Project>
                  <PropertyGroup>
                    <TargetPlatformIdentifier>UAP</TargetPlatformIdentifier>
                    <OutputType>AppContainerExe</OutputType>
                    <TargetPlatformVersion>10.0.22621.0</TargetPlatformVersion>
                    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
                  </PropertyGroup>
                </Project>
                """);
            var msBuild = new FileInfo(
                Path.Combine(root.FullName, "MSBuild.exe"));
            await File.WriteAllTextAsync(msBuild.FullName, "");
            return new BuilderFixture(root, project, msBuild);
        }

        internal EnforcementProcessResult CreateLayoutWithDependency()
        {
            Layout.Create();
            File.WriteAllText(
                Path.Combine(Layout.FullName, "AppxManifest.xml"),
                """
                <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                  <Dependencies>
                    <PackageDependency Name="Microsoft.VCLibs.140.00"
                                       MinVersion="14.0.0.0" />
                  </Dependencies>
                </Package>
                """);
            File.WriteAllText(
                Path.Combine(Layout.FullName, "Source.build.appxrecipe"),
                "");

            var packageRoot = Directory.CreateDirectory(
                Path.Combine(Root.FullName, "packages"));
            var libraryPath = Path.Combine(
                "microsoft.vclibs",
                "14.0.0");
            var library = Directory.CreateDirectory(
                Path.Combine(packageRoot.FullName, libraryPath));
            var appx = Path.Combine(
                library.FullName,
                "Microsoft.VCLibs.140.00.appx");
            using (var archive = ZipFile.Open(
                       appx,
                       ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("AppxManifest.xml");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(
                    """
                    <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                      <Identity Name="Microsoft.VCLibs.140.00"
                                Version="14.0.30704.0"
                                ProcessorArchitecture="x64" />
                    </Package>
                    """);
            }

            var obj = Directory.CreateDirectory(
                Path.Combine(Root.FullName, "obj"));
            File.WriteAllText(
                Path.Combine(obj.FullName, "project.assets.json"),
                JsonSerializer.Serialize(new
                {
                    packageFolders = new Dictionary<string, object>
                    {
                        [packageRoot.FullName] = new { }
                    },
                    libraries = new Dictionary<string, object>
                    {
                        ["Microsoft.VCLibs/14.0.0"] = new
                        {
                            path = libraryPath.Replace('\\', '/')
                        }
                    }
                }));
            return new EnforcementProcessResult(
                0,
                "Build succeeded.",
                "");
        }

        public void Dispose() => TestDirectories.Delete(Root);
    }
}
