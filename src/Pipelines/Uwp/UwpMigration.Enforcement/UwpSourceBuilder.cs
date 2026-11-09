// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;
using Windows.Management.Deployment;
using Windows.System;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

internal sealed record UwpSourceBuildResult(
    IReadOnlyList<string> Command,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string TargetPlatformVersion,
    DirectoryInfo? LayoutDirectory);

internal interface IUwpSourceBuilder
{
    Task<UwpSourceBuildResult> BuildAsync(
        FileInfo sourceProject,
        string? configuration,
        string? architecture,
        CancellationToken cancellationToken);
}

internal interface IUwpPackageDependencyService
{
    string? GetInstalledVersion(string packageName, string architecture);

    Task InstallPackageAsync(
        string packagePath,
        CancellationToken cancellationToken);
}

internal sealed class UwpSourceBuilder : IUwpSourceBuilder
{
    private const string UwpProjectTypeGuid =
        "{BC8A1FFA-BEE3-4634-8014-F334798102B3}";

    private readonly IEnforcementProcessRunner _processRunner;
    private readonly IUwpPackageDependencyService _packageDependencyService;
    private readonly Func<FileInfo?> _locateVisualStudioMsBuild;
    private readonly Func<IReadOnlyList<Version>> _locateWindowsSdkVersions;

    internal UwpSourceBuilder(
        IEnforcementProcessRunner processRunner,
        IUwpPackageDependencyService? packageDependencyService = null,
        Func<FileInfo?>? locateVisualStudioMsBuild = null,
        Func<IReadOnlyList<Version>>? locateWindowsSdkVersions = null)
    {
        _processRunner = processRunner;
        _packageDependencyService =
            packageDependencyService ?? new UwpPackageDependencyService();
        _locateVisualStudioMsBuild =
            locateVisualStudioMsBuild ?? FindVisualStudioMsBuild;
        _locateWindowsSdkVersions =
            locateWindowsSdkVersions ?? FindInstalledWindowsSdkVersions;
    }

    public async Task<UwpSourceBuildResult> BuildAsync(
        FileInfo sourceProject,
        string? configuration,
        string? architecture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceProject);
        if (!sourceProject.Exists)
        {
            throw new InvalidDataException(
                $"The recorded source project does not exist: {sourceProject.FullName}");
        }
        if (!IsClassicUwpProject(sourceProject))
        {
            throw new InvalidDataException(
                $"The recorded source project is not a classic UWP AppContainerExe project: {sourceProject.FullName}");
        }

        var effectiveConfiguration = string.IsNullOrWhiteSpace(configuration)
            ? "Debug"
            : configuration.Trim();
        var effectiveArchitecture = NormalizeArchitecture(architecture);
        var properties = ReadProjectProperties(sourceProject);
        var targetSdk = SelectTargetSdk(
            properties.GetValueOrDefault("TargetPlatformVersion"),
            properties.GetValueOrDefault("TargetPlatformMinVersion"),
            _locateWindowsSdkVersions());
        var msbuild = _locateVisualStudioMsBuild()
            ?? throw new InvalidDataException(
                "Classic UWP source baselines require Visual Studio MSBuild with the Universal Windows Platform development workload.");
        var arguments = BuildMsBuildArguments(
            sourceProject,
            effectiveConfiguration,
            effectiveArchitecture,
            targetSdk);
        var command = new List<string> { msbuild.FullName };
        command.AddRange(arguments);
        var result = await _processRunner.RunAsync(
            msbuild.FullName,
            sourceProject.Directory!,
            arguments,
            cancellationToken);
        if (result.InfrastructureFailure)
        {
            throw new IOException(
                $"Visual Studio MSBuild could not be invoked: {FirstNonEmpty(result.Error, result.Output)}");
        }
        if (result.ExitCode != 0)
        {
            return new UwpSourceBuildResult(
                command,
                result.ExitCode,
                result.Output,
                result.Error,
                targetSdk.ToString(),
                null);
        }

        var layout = ResolveLayoutDirectory(
            sourceProject,
            effectiveConfiguration,
            effectiveArchitecture)
            ?? throw new InvalidDataException(
                "The classic UWP build completed but no loose AppX layout containing AppxManifest.xml " +
                $"was found under '{Path.Combine(sourceProject.DirectoryName!, "bin")}'.");
        await EnsureFrameworkDependenciesAsync(
            layout,
            sourceProject,
            effectiveArchitecture,
            cancellationToken);
        return new UwpSourceBuildResult(
            command,
            0,
            result.Output,
            result.Error,
            targetSdk.ToString(),
            layout);
    }

    internal static bool IsClassicUwpProject(FileInfo sourceProject)
    {
        try
        {
            var document = XDocument.Load(sourceProject.FullName);
            var values = document.Descendants()
                .Where(element => element.Name.LocalName is
                    "TargetPlatformIdentifier" or
                    "OutputType" or
                    "ProjectTypeGuids")
                .Select(element => (
                    element.Name.LocalName,
                    Value: element.Value.Trim()))
                .ToList();
            return values.Any(value =>
                       value.LocalName == "TargetPlatformIdentifier" &&
                       value.Value.Equals(
                           "UAP",
                           StringComparison.OrdinalIgnoreCase))
                   || values.Any(value =>
                       value.LocalName == "ProjectTypeGuids" &&
                       value.Value.Contains(
                           UwpProjectTypeGuid,
                           StringComparison.OrdinalIgnoreCase))
                   || values.Any(value =>
                       value.LocalName == "OutputType" &&
                       value.Value.Equals(
                           "AppContainerExe",
                           StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Xml.XmlException)
        {
            throw new InvalidDataException(
                $"The recorded source project could not be inspected: {exception.Message}",
                exception);
        }
    }

    internal static IReadOnlyList<string> BuildMsBuildArguments(
        FileInfo sourceProject,
        string configuration,
        string architecture,
        Version targetSdk) =>
        [
            sourceProject.FullName,
            "-m",
            "-restore",
            "-verbosity:minimal",
            $"-property:Configuration={configuration}",
            $"-property:Platform={architecture}",
            $"-property:TargetPlatformVersion={targetSdk}",
            "-property:AppxPackageSigningEnabled=false",
            "-property:GenerateAppxPackageOnBuild=false"
        ];

    internal static Version SelectTargetSdk(
        string? requestedVersion,
        string? minimumVersion,
        IReadOnlyList<Version> installedVersions)
    {
        if (installedVersions.Count == 0)
        {
            throw new InvalidDataException(
                "No installed Universal Windows Platform SDK was found. Install a Windows SDK and the Visual Studio UWP workload.");
        }

        var ordered = installedVersions
            .Distinct()
            .OrderDescending()
            .ToList();
        var requested = TryParseVersion(requestedVersion);
        if (requested is not null && ordered.Contains(requested))
            return requested;

        var minimum = TryParseVersion(minimumVersion);
        var compatible = ordered.FirstOrDefault(
            version => minimum is null || version >= minimum);
        if (compatible is null)
        {
            throw new InvalidDataException(
                $"The project requires Windows SDK {minimumVersion}, but the newest installed UWP SDK is {ordered[0]}.");
        }
        return compatible;
    }

    internal static DirectoryInfo? ResolveLayoutDirectory(
        FileInfo sourceProject,
        string configuration,
        string architecture)
    {
        var bin = new DirectoryInfo(
            Path.Combine(sourceProject.DirectoryName!, "bin"));
        if (!bin.Exists)
            return null;

        return bin.EnumerateFiles(
                "AppxManifest.xml",
                SearchOption.AllDirectories)
            .Where(file => !file.FullName
                .Split(Path.DirectorySeparatorChar)
                .Any(segment => segment.Equals(
                    "AppX",
                    StringComparison.OrdinalIgnoreCase)))
            .Where(file =>
            {
                var segments = file.Directory!.FullName.Split(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                return segments.Contains(
                           configuration,
                           StringComparer.OrdinalIgnoreCase) &&
                       segments.Contains(
                           architecture,
                           StringComparer.OrdinalIgnoreCase);
            })
            .Select(file => new
            {
                Directory = file.Directory!,
                Score = ScoreLayout(
                    file.Directory!,
                    configuration,
                    architecture),
                file.LastWriteTimeUtc
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.LastWriteTimeUtc)
            .Select(candidate => candidate.Directory)
            .FirstOrDefault();
    }

    private async Task EnsureFrameworkDependenciesAsync(
        DirectoryInfo layout,
        FileInfo sourceProject,
        string architecture,
        CancellationToken cancellationToken)
    {
        var manifest = new FileInfo(
            Path.Combine(layout.FullName, "AppxManifest.xml"));
        var dependencies = ReadManifestDependencies(manifest);
        if (dependencies.Count == 0)
            return;

        var assets = new FileInfo(
            Path.Combine(
                sourceProject.DirectoryName!,
                "obj",
                "project.assets.json"));
        foreach (var dependency in dependencies)
        {
            var installed = _packageDependencyService.GetInstalledVersion(
                    dependency.Name,
                    architecture)
                ?? _packageDependencyService.GetInstalledVersion(
                    dependency.Name,
                    "neutral");
            if (VersionAtLeast(installed, dependency.MinimumVersion))
                continue;

            var package = FindRestoredFrameworkPackage(
                assets,
                dependency,
                architecture);
            if (package is null)
            {
                throw new InvalidDataException(
                    $"UWP framework dependency '{dependency.Name}' {dependency.MinimumVersion} for {architecture} is not installed, " +
                    $"and no matching .appx was found in the restored packages from '{assets.FullName}'.");
            }

            try
            {
                await _packageDependencyService.InstallPackageAsync(
                    package.FullName,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(
                    $"Failed to install UWP framework dependency '{dependency.Name}' from '{package.FullName}': {exception.Message}",
                    exception);
            }
        }
    }

    private static List<FrameworkDependency> ReadManifestDependencies(
        FileInfo manifest)
    {
        var document = XDocument.Load(manifest.FullName);
        return document.Descendants()
            .Where(element => element.Name.LocalName == "PackageDependency")
            .Select(element => new FrameworkDependency(
                element.Attribute("Name")?.Value ?? string.Empty,
                element.Attribute("MinVersion")?.Value ?? "0.0.0.0"))
            .Where(dependency => dependency.Name.Length > 0)
            .ToList();
    }

    private static FileInfo? FindRestoredFrameworkPackage(
        FileInfo assetsFile,
        FrameworkDependency dependency,
        string architecture)
    {
        if (!assetsFile.Exists)
            return null;

        using var assets = JsonDocument.Parse(
            File.ReadAllBytes(assetsFile.FullName));
        if (!assets.RootElement.TryGetProperty(
                "packageFolders",
                out var packageFolders) ||
            !assets.RootElement.TryGetProperty("libraries", out var libraries))
        {
            return null;
        }

        var roots = packageFolders
            .EnumerateObject()
            .Select(property => property.Name)
            .ToList();
        var libraryPaths = libraries
            .EnumerateObject()
            .Select(property =>
                property.Value.TryGetProperty("path", out var path)
                    ? path.GetString()
                    : null)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();
        var matches =
            new List<(FileInfo File, Version Version, bool ExactArchitecture)>();
        foreach (var root in roots)
        {
            foreach (var libraryPath in libraryPaths)
            {
                var directory = Path.Combine(
                    root,
                    libraryPath.Replace(
                        '/',
                        Path.DirectorySeparatorChar));
                if (!Directory.Exists(directory))
                    continue;

                foreach (var appxPath in Directory.EnumerateFiles(
                             directory,
                             "*.appx",
                             SearchOption.AllDirectories))
                {
                    if (TryReadPackageIdentity(
                            appxPath,
                            out var identity) &&
                        identity.Name.Equals(
                            dependency.Name,
                            StringComparison.OrdinalIgnoreCase) &&
                        VersionAtLeast(
                            identity.Version.ToString(),
                            dependency.MinimumVersion) &&
                        (identity.Architecture.Equals(
                             architecture,
                             StringComparison.OrdinalIgnoreCase) ||
                         identity.Architecture.Equals(
                             "neutral",
                             StringComparison.OrdinalIgnoreCase)))
                    {
                        matches.Add((
                            new FileInfo(appxPath),
                            identity.Version,
                            identity.Architecture.Equals(
                                architecture,
                                StringComparison.OrdinalIgnoreCase)));
                    }
                }
            }
        }

        return matches
            .OrderByDescending(match => match.ExactArchitecture)
            .ThenByDescending(match => match.Version)
            .Select(match => match.File)
            .FirstOrDefault();
    }

    private static bool TryReadPackageIdentity(
        string appxPath,
        out PackageIdentity identity)
    {
        identity = default;
        try
        {
            using var archive = ZipFile.OpenRead(appxPath);
            var manifestEntry = archive.Entries.FirstOrDefault(entry =>
                entry.FullName.Equals(
                    "AppxManifest.xml",
                    StringComparison.OrdinalIgnoreCase));
            if (manifestEntry is null)
                return false;

            using var stream = manifestEntry.Open();
            var document = XDocument.Load(stream);
            var element = document.Descendants().FirstOrDefault(item =>
                item.Name.LocalName == "Identity");
            var name = element?.Attribute("Name")?.Value;
            var version = TryParseVersion(
                element?.Attribute("Version")?.Value);
            var packageArchitecture =
                element?.Attribute("ProcessorArchitecture")?.Value
                ?? "neutral";
            if (string.IsNullOrWhiteSpace(name) || version is null)
                return false;

            identity = new PackageIdentity(
                name,
                version,
                packageArchitecture);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or System.Xml.XmlException)
        {
            return false;
        }
    }

    private static Dictionary<string, string> ReadProjectProperties(
        FileInfo sourceProject)
    {
        var document = XDocument.Load(sourceProject.FullName);
        return document.Descendants()
            .Where(element => element.Name.LocalName is
                "TargetPlatformVersion" or
                "TargetPlatformMinVersion")
            .GroupBy(
                element => element.Name.LocalName,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value.Trim(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static FileInfo? FindVisualStudioMsBuild()
    {
        var roots = new[]
            {
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFilesX86)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.Combine(path, "Microsoft Visual Studio"))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var visualStudioRoot in roots)
        {
            foreach (var versionDirectory in Directory
                         .EnumerateDirectories(visualStudioRoot)
                         .OrderByDescending(
                             path => path,
                             StringComparer.OrdinalIgnoreCase))
            {
                foreach (var editionDirectory in Directory
                             .EnumerateDirectories(versionDirectory)
                             .OrderByDescending(
                                 path => path,
                                 StringComparer.OrdinalIgnoreCase))
                {
                    if (!HasUwpBuildTools(editionDirectory))
                        continue;

                    foreach (var relativePath in new[]
                             {
                                 Path.Combine(
                                     "MSBuild",
                                     "Current",
                                     "Bin",
                                     "MSBuild.exe"),
                                 Path.Combine(
                                     "MSBuild",
                                     "15.0",
                                     "Bin",
                                     "MSBuild.exe")
                             })
                    {
                        var candidate = new FileInfo(
                            Path.Combine(
                                editionDirectory,
                                relativePath));
                        if (candidate.Exists)
                            return candidate;
                    }
                }
            }
        }
        return null;
    }

    private static bool HasUwpBuildTools(string visualStudioDirectory)
    {
        var windowsXaml = Path.Combine(
            visualStudioDirectory,
            "MSBuild",
            "Microsoft",
            "WindowsXaml");
        return Directory.Exists(windowsXaml) &&
               Directory.EnumerateFiles(
                       windowsXaml,
                       "Microsoft.Windows.UI.Xaml.CSharp.Targets",
                       SearchOption.AllDirectories)
                   .Any();
    }

    private static List<Version> FindInstalledWindowsSdkVersions()
    {
        var roots = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
            if (key?.GetValue("KitsRoot10") is string registryRoot)
                roots.Add(registryRoot);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
        }

        roots.Add(Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86),
            "Windows Kits",
            "10"));
        var versions = new List<Version>();
        foreach (var root in roots)
        {
            var platforms = Path.Combine(root, "Platforms", "UAP");
            if (!Directory.Exists(platforms))
                continue;

            foreach (var directory in Directory.EnumerateDirectories(platforms))
            {
                if (Version.TryParse(
                        Path.GetFileName(directory),
                        out var version))
                {
                    versions.Add(version);
                }
            }
        }
        return versions;
    }

    private static int ScoreLayout(
        DirectoryInfo directory,
        string configuration,
        string architecture)
    {
        var segments = directory.FullName.Split(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var score = segments.Count(segment => segment.Equals(
            configuration,
            StringComparison.OrdinalIgnoreCase)) * 2;
        score += segments.Count(segment => segment.Equals(
            architecture,
            StringComparison.OrdinalIgnoreCase)) * 2;
        score += directory.EnumerateFiles(
                "*.build.appxrecipe",
                SearchOption.TopDirectoryOnly)
            .Any()
            ? 1
            : 0;
        return score;
    }

    private static string NormalizeArchitecture(string? architecture)
    {
        var value = string.IsNullOrWhiteSpace(architecture)
            ? RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                _ => throw new InvalidDataException(
                    $"The current process architecture '{RuntimeInformation.ProcessArchitecture}' is not supported for UWP source builds.")
            }
            : architecture.Trim().ToLowerInvariant();
        return value is "x64" or "x86" or "arm64"
            ? value
            : throw new ArgumentException(
                "Source architecture must be x64, x86, or arm64.",
                nameof(architecture));
    }

    private static Version? TryParseVersion(string? value) =>
        Version.TryParse(value, out var version) ? version : null;

    private static bool VersionAtLeast(string? actual, string minimum)
    {
        var actualVersion = TryParseVersion(actual);
        var minimumVersion = TryParseVersion(minimum);
        return actualVersion is not null &&
               (minimumVersion is null || actualVersion >= minimumVersion);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
        ?? "No diagnostic output was produced.";

    private sealed record FrameworkDependency(
        string Name,
        string MinimumVersion);

    private readonly record struct PackageIdentity(
        string Name,
        Version Version,
        string Architecture);
}

internal sealed class UwpPackageDependencyService
    : IUwpPackageDependencyService
{
    public string? GetInstalledVersion(
        string packageName,
        string architecture)
    {
        var wantedArchitecture = MapArchitecture(architecture);
        Version? best = null;
        var packageManager = new PackageManager();
        foreach (var package in packageManager.FindPackagesForUser(string.Empty))
        {
            if (!package.Id.Name.Equals(
                    packageName,
                    StringComparison.OrdinalIgnoreCase) ||
                package.Id.Architecture != wantedArchitecture)
            {
                continue;
            }

            var version = package.Id.Version;
            var candidate = new Version(
                version.Major,
                version.Minor,
                version.Build,
                version.Revision);
            if (best is null || candidate > best)
                best = candidate;
        }
        return best?.ToString();
    }

    public async Task InstallPackageAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        var packageManager = new PackageManager();
        var result = await packageManager.AddPackageAsync(
                new Uri(Path.GetFullPath(packagePath)),
                null,
                DeploymentOptions.ForceApplicationShutdown)
            .AsTask(cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.ErrorText))
        {
            throw new InvalidOperationException(
                $"{result.ErrorText} (0x{result.ExtendedErrorCode?.HResult:X8})");
        }
    }

    private static ProcessorArchitecture MapArchitecture(
        string architecture) =>
        architecture.ToLowerInvariant() switch
        {
            "x64" => ProcessorArchitecture.X64,
            "x86" => ProcessorArchitecture.X86,
            "arm64" => ProcessorArchitecture.Arm64,
            "neutral" => ProcessorArchitecture.Neutral,
            _ => ProcessorArchitecture.Unknown
        };
}
