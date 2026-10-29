// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using UwpMigration.Contracts;
using UwpMigration.Mechanical;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

public sealed class UwpSourceRunOptions
{
    public string WinAppExecutable { get; init; } = "winapp";
    public string? Architecture { get; init; }
    public string? Configuration { get; init; }
    public int WindowPollAttempts { get; init; } = 20;
    public TimeSpan WindowPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}

public sealed class SourceWindow
{
    [JsonPropertyName("hwnd")]
    public long Hwnd { get; set; }

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("className")]
    public string? ClassName { get; set; }
}

public sealed class SourceRunAttestation
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("artifactClass")]
    public string ArtifactClass { get; set; } = "source-baseline-attestation";

    [JsonPropertyName("producer")]
    public string Producer { get; set; } = MigrationArtifactClass.Producer;

    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("mode")]
    public required string Mode { get; set; }

    [JsonPropertyName("sourceRoot")]
    public required string SourceRoot { get; set; }

    [JsonPropertyName("sourceProject")]
    public required string SourceProject { get; set; }

    [JsonPropertyName("sourceProjectSha256")]
    public required string SourceProjectSha256 { get; set; }

    [JsonPropertyName("sourceFingerprint")]
    public required string SourceFingerprint { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset CompletedAt { get; set; }

    [JsonPropertyName("command")]
    public List<string> Command { get; set; } = [];

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }

    [JsonPropertyName("aumid")]
    public string? Aumid { get; set; }

    [JsonPropertyName("processId")]
    public int? ProcessId { get; set; }

    [JsonPropertyName("authorizedWindows")]
    public List<SourceWindow> AuthorizedWindows { get; set; } = [];

    [JsonPropertyName("windowInventoryBefore")]
    public string? WindowInventoryBeforePath { get; set; }

    [JsonPropertyName("windowInventoryAfter")]
    public string? WindowInventoryAfterPath { get; set; }

    [JsonPropertyName("stdout")]
    public required string StandardOutputPath { get; set; }

    [JsonPropertyName("stderr")]
    public required string StandardErrorPath { get; set; }
}

public sealed class SourceUiProbe
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("artifactClass")]
    public string ArtifactClass { get; set; } = MigrationArtifactClass.DevelopmentProbe;

    [JsonPropertyName("producer")]
    public string Producer { get; set; } = MigrationArtifactClass.Producer;

    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("sourceProjectSha256")]
    public required string SourceProjectSha256 { get; set; }

    [JsonPropertyName("sourceFingerprint")]
    public required string SourceFingerprint { get; set; }

    [JsonPropertyName("sourceRunAttestationId")]
    public string? SourceRunAttestationId { get; set; }

    [JsonPropertyName("windowHandle")]
    public long? WindowHandle { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset CompletedAt { get; set; }

    [JsonPropertyName("command")]
    public List<string> Command { get; set; } = [];

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }

    [JsonPropertyName("artifact")]
    public required string ArtifactPath { get; set; }

    [JsonPropertyName("stdout")]
    public required string StandardOutputPath { get; set; }

    [JsonPropertyName("stderr")]
    public required string StandardErrorPath { get; set; }
}

public sealed class UwpSourceGateway
{
    private readonly IEnforcementProcessRunner _processRunner;
    private readonly TimeProvider _timeProvider;

    public UwpSourceGateway(
        IEnforcementProcessRunner processRunner,
        TimeProvider? timeProvider = null)
    {
        _processRunner = processRunner;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SourceRunAttestation> RunAsync(
        DirectoryInfo target,
        UwpSourceRunOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        await using var workspace = await LoadWorkspaceAsync(
            target,
            cancellationToken);
        var id = CreateId("source-run");
        var directory = $"{workspace.GatewayRoot}/runs/{id}";
        var beforePath = $"{directory}/windows-before.json";
        var afterPath = $"{directory}/windows-after.json";
        var stdoutPath = $"{directory}/stdout.log";
        var stderrPath = $"{directory}/stderr.log";
        var startedAt = _timeProvider.GetUtcNow();

        var beforeResult = await InvokeAsync(
            workspace,
            options.WinAppExecutable,
            ["ui", "list-windows", "--json"],
            cancellationToken);
        RequireSuccessfulWindowInventory(beforeResult, "pre-launch");
        var before = ParseWindows(beforeResult.Output);
        await WriteOutputAsync(
            workspace.Target,
            beforePath,
            $"{directory}/windows-before.stderr.log",
            beforeResult,
            cancellationToken);

        var arguments = BuildRunArguments(workspace.SourceProject, options, diagnostic: false);
        var result = await InvokeAsync(
            workspace,
            options.WinAppExecutable,
            arguments,
            cancellationToken);
        await WriteOutputAsync(
            workspace.Target,
            stdoutPath,
            stderrPath,
            result,
            cancellationToken);

        var parsed = WinAppRunResultParser.Parse(result.Output);
        var after = before;
        if (!result.InfrastructureFailure && result.ExitCode == 0)
        {
            for (var attempt = 0; attempt < options.WindowPollAttempts; attempt++)
            {
                if (attempt > 0 && options.WindowPollInterval > TimeSpan.Zero)
                {
                    await Task.Delay(
                        options.WindowPollInterval,
                        _timeProvider,
                        cancellationToken);
                }
                var inventory = await InvokeAsync(
                    workspace,
                    options.WinAppExecutable,
                    ["ui", "list-windows", "--json"],
                    cancellationToken);
                RequireSuccessfulWindowInventory(inventory, "post-launch");
                after = ParseWindows(inventory.Output);
                await WriteOutputAsync(
                    workspace.Target,
                    afterPath,
                    $"{directory}/windows-after.stderr.log",
                    inventory,
                    cancellationToken);
                if (NewWindows(before, after).Count > 0)
                    break;
            }
        }
        var completedAt = _timeProvider.GetUtcNow();

        var attestation = new SourceRunAttestation
        {
            Id = id,
            Mode = "detached",
            SourceRoot = workspace.SourceRoot.FullName,
            SourceProject = workspace.SourceProject,
            SourceProjectSha256 = workspace.SourceProjectSha256,
            SourceFingerprint = workspace.SourceFingerprint,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Command = [options.WinAppExecutable, .. arguments],
            ExitCode = result.ExitCode,
            Aumid = parsed.Aumid,
            ProcessId = parsed.ProcessId,
            AuthorizedWindows = NewWindows(before, after),
            WindowInventoryBeforePath = beforePath,
            WindowInventoryAfterPath = File.Exists(workspace.Resolve(afterPath))
                ? afterPath
                : null,
            StandardOutputPath = stdoutPath,
            StandardErrorPath = stderrPath
        };
        await PersistRunAsync(workspace, attestation, directory, cancellationToken);
        return attestation;
    }

    public async Task<SourceRunAttestation> DiagnoseAsync(
        DirectoryInfo target,
        UwpSourceRunOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        await using var workspace = await LoadWorkspaceAsync(
            target,
            cancellationToken);
        var id = CreateId("source-diagnostic");
        var directory = $"{workspace.GatewayRoot}/diagnostics/{id}";
        var stdoutPath = $"{directory}/stdout.log";
        var stderrPath = $"{directory}/stderr.log";
        var arguments = BuildRunArguments(workspace.SourceProject, options, diagnostic: true);
        var startedAt = _timeProvider.GetUtcNow();
        var result = await InvokeAsync(
            workspace,
            options.WinAppExecutable,
            arguments,
            cancellationToken);
        var completedAt = _timeProvider.GetUtcNow();
        await WriteOutputAsync(
            workspace.Target,
            stdoutPath,
            stderrPath,
            result,
            cancellationToken);
        var parsed = WinAppRunResultParser.Parse(result.Output);
        var attestation = new SourceRunAttestation
        {
            Id = id,
            Mode = "diagnostic",
            SourceRoot = workspace.SourceRoot.FullName,
            SourceProject = workspace.SourceProject,
            SourceProjectSha256 = workspace.SourceProjectSha256,
            SourceFingerprint = workspace.SourceFingerprint,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Command = [options.WinAppExecutable, .. arguments],
            ExitCode = result.ExitCode,
            Aumid = parsed.Aumid,
            ProcessId = parsed.ProcessId,
            StandardOutputPath = stdoutPath,
            StandardErrorPath = stderrPath
        };
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            $"{directory}/attestation.json",
            attestation,
            cancellationToken);
        return attestation;
    }

    public async Task<SourceUiProbe> CaptureAsync(
        DirectoryInfo target,
        string winAppExecutable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        if (arguments.Count == 0)
        {
            throw new ArgumentException(
                "At least one WinApp UI argument is required.",
                nameof(arguments));
        }

        await using var workspace = await LoadWorkspaceAsync(
            target,
            cancellationToken);
        var commandName = arguments[0];
        SourceRunAttestation? launch = null;
        long? windowHandle = null;
        if (!commandName.Equals("list-windows", StringComparison.OrdinalIgnoreCase))
        {
            windowHandle = ParseWindowHandle(arguments)
                ?? throw new InvalidDataException(
                    "Source UI commands other than list-windows must use -w or --window with a gateway-authorized source HWND.");
            launch = await LoadCurrentRunAsync(workspace, cancellationToken);
            if (launch.Mode != "detached" ||
                launch.ExitCode != 0 ||
                launch.SourceProjectSha256 != workspace.SourceProjectSha256 ||
                launch.SourceFingerprint != workspace.SourceFingerprint)
            {
                throw new InvalidDataException(
                    "A successful current source run for the unchanged recorded source project is required.");
            }
            if (!launch.AuthorizedWindows.Any(window => window.Hwnd == windowHandle.Value))
            {
                throw new InvalidDataException(
                    $"Window {windowHandle.Value} was not created by the current source run.");
            }
        }
        ValidateSourceOutputPath(workspace, arguments);

        var id = CreateId("source-ui");
        var directory = $"{workspace.GatewayRoot}/probes/{id}";
        var artifactPath = $"{directory}/probe.json";
        var stdoutPath = $"{directory}/stdout.log";
        var stderrPath = $"{directory}/stderr.log";
        var command = new List<string> { "ui" };
        command.AddRange(arguments);
        var startedAt = _timeProvider.GetUtcNow();
        var result = await InvokeAsync(
            workspace,
            winAppExecutable,
            command,
            cancellationToken);
        var completedAt = _timeProvider.GetUtcNow();
        await WriteOutputAsync(
            workspace.Target,
            stdoutPath,
            stderrPath,
            result,
            cancellationToken);
        var probe = new SourceUiProbe
        {
            Id = id,
            SourceProjectSha256 = workspace.SourceProjectSha256,
            SourceFingerprint = workspace.SourceFingerprint,
            SourceRunAttestationId = launch?.Id,
            WindowHandle = windowHandle,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Command = [winAppExecutable, .. command],
            ExitCode = result.ExitCode,
            ArtifactPath = artifactPath,
            StandardOutputPath = stdoutPath,
            StandardErrorPath = stderrPath
        };
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            artifactPath,
            probe,
            cancellationToken);
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            $"{workspace.GatewayRoot}/current-probe.json",
            probe,
            cancellationToken);
        return probe;
    }

    private static void ValidateOptions(UwpSourceRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.WinAppExecutable))
            throw new ArgumentException("WinApp executable must not be empty.", nameof(options));
        if (options.WindowPollAttempts is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(options), "Window poll attempts must be between 1 and 120.");
        if (options.WindowPollInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Window poll interval cannot be negative.");
    }

    private async Task<EnforcementProcessResult> InvokeAsync(
        SourceWorkspace workspace,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            executable,
            workspace.Target,
            arguments,
            cancellationToken);
        if (result.InfrastructureFailure)
        {
            throw new IOException(
                $"WinApp CLI could not be invoked: {FirstNonEmpty(result.Error, result.Output)}");
        }
        return result;
    }

    private static async Task<SourceWorkspace> LoadWorkspaceAsync(
        DirectoryInfo requestedTarget,
        CancellationToken cancellationToken)
    {
        var snapshot = await UwpEnforcementRunner.AssessAsync(
            requestedTarget,
            cancellationToken: cancellationToken);
        if (snapshot.Disposition?.Terminal == true)
            throw new InvalidDataException("Source operations are not permitted after a terminal closure disposition.");
        if (snapshot.Stage != WorkflowStage.SourceOracle ||
            snapshot.Frontier?.Kind != "source-oracle")
        {
            throw new InvalidDataException(
                "Source operations are permitted only while the closure frontier is source-oracle.");
        }
        if (!MigrationPathResolver.TryCanonicalizeRoot(
                requestedTarget.FullName,
                out var targetRoot,
                out var targetError))
        {
            throw new InvalidDataException(targetError);
        }
        var target = new DirectoryInfo(targetRoot);
        var operationLock = await EnforcementStore.AcquireLockAsync(
            target,
            cancellationToken);
        try
        {
            var (persisted, persistedError) =
                await EnforcementStore.TryLoadAsync<EnforcementSnapshot>(
                    target,
                    EnforcementStore.StatePath,
                    cancellationToken);
            if (persistedError is not null)
                throw new InvalidDataException(persistedError);
            if (persisted?.Disposition?.Terminal == true ||
                persisted?.Stage != WorkflowStage.SourceOracle ||
                persisted.Frontier?.Kind != "source-oracle")
            {
                throw new InvalidDataException(
                    "Source operations are permitted only while the persisted closure frontier is source-oracle.");
            }

            var reportPath = Path.Combine(targetRoot, "migration-report.json");
            var report = await MigrationReportStore.LoadAsync(
                reportPath,
                cancellationToken);
            if (report.SchemaVersion != MigrationReport.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported migration-report schema {report.SchemaVersion}; expected {MigrationReport.CurrentSchemaVersion}.");
            }

            var sourceRootValue = Path.IsPathFullyQualified(report.Source.Root)
                ? report.Source.Root
                : Path.Combine(targetRoot, report.Source.Root);
            if (!MigrationPathResolver.TryCanonicalizeRoot(
                    sourceRootValue,
                    out var sourceRootPath,
                    out var sourceRootError))
            {
                throw new InvalidDataException(
                    $"The recorded source root is invalid: {sourceRootError}");
            }
            var sourceProjectError =
                "The recorded source project does not exist.";
            if (string.IsNullOrWhiteSpace(report.Source.ProjectFile) ||
                !MigrationPathResolver.TryResolveContainedRelativePath(
                    sourceRootPath,
                    report.Source.ProjectFile,
                    out var sourceProject,
                    out _,
                    out sourceProjectError) ||
                !File.Exists(sourceProject))
            {
                throw new InvalidDataException(
                    string.IsNullOrWhiteSpace(report.Source.ProjectFile)
                        ? "migration-report.json does not identify a source project."
                        : sourceProjectError ??
                          "The recorded source project does not exist.");
            }
            if (!EnforcementStore.TryResolveContainedPath(
                    target,
                    report.Validation.SourceBaseline.EvidenceRoot,
                    out var sourceEvidenceRoot,
                    out var evidenceError))
            {
                throw new InvalidDataException(evidenceError);
            }

            await using var stream = new FileStream(
                sourceProject,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var sourceProjectSha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken));
            var sourceFingerprint = await TargetFingerprint.ComputeAsync(
                new DirectoryInfo(sourceRootPath),
                cancellationToken);
            var evidenceRelative =
                Path.GetRelativePath(targetRoot, sourceEvidenceRoot)
                    .Replace('\\', '/')
                    .TrimEnd('/');
            return new SourceWorkspace(
                target,
                new DirectoryInfo(sourceRootPath),
                sourceProject,
                sourceProjectSha256,
                sourceFingerprint,
                evidenceRelative,
                $"{evidenceRelative}/gateway",
                operationLock);
        }
        catch
        {
            await operationLock.DisposeAsync();
            throw;
        }
    }

    private static List<string> BuildRunArguments(
        string sourceProject,
        UwpSourceRunOptions options,
        bool diagnostic)
    {
        var arguments = new List<string> { "run", sourceProject };
        if (!string.IsNullOrWhiteSpace(options.Architecture))
        {
            arguments.Add("--arch");
            arguments.Add(options.Architecture);
        }
        if (!string.IsNullOrWhiteSpace(options.Configuration))
        {
            arguments.Add("--configuration");
            arguments.Add(options.Configuration);
        }
        if (diagnostic)
        {
            arguments.Add("--debug-output");
        }
        else
        {
            arguments.Add("--detach");
            arguments.Add("--json");
        }
        return arguments;
    }

    private static void RequireSuccessfulWindowInventory(
        EnforcementProcessResult result,
        string phase)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"WinApp UI {phase} window inventory failed: {FirstNonEmpty(result.Error, result.Output)}");
        }
        _ = ParseWindows(result.Output);
    }

    private static List<SourceWindow> ParseWindows(string output)
    {
        try
        {
            var windows = JsonSerializer.Deserialize<List<SourceWindow>>(
                output,
                EnforcementStore.JsonOptions);
            return windows
                ?? throw new InvalidDataException(
                    "WinApp UI window inventory contains JSON null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"WinApp UI window inventory is not valid JSON: {exception.Message}",
                exception);
        }
    }

    private static List<SourceWindow> NewWindows(
        IReadOnlyCollection<SourceWindow> before,
        IEnumerable<SourceWindow> after)
    {
        var existing = before.Select(window => window.Hwnd).ToHashSet();
        return after
            .Where(window => window.Hwnd != 0 && !existing.Contains(window.Hwnd))
            .GroupBy(window => window.Hwnd)
            .Select(group => group.First())
            .OrderBy(window => window.Hwnd)
            .ToList();
    }

    private static long? ParseWindowHandle(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is "-w" or "--window")
            {
                if (index + 1 < arguments.Count &&
                    long.TryParse(arguments[index + 1], out var value))
                {
                    return value;
                }
                return null;
            }
            const string longPrefix = "--window=";
            if (argument.StartsWith(longPrefix, StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(argument[longPrefix.Length..], out var attached))
            {
                return attached;
            }
        }
        return null;
    }

    private static void ValidateSourceOutputPath(
        SourceWorkspace workspace,
        IReadOnlyList<string> arguments)
    {
        if (!arguments[0].Equals("screenshot", StringComparison.OrdinalIgnoreCase))
            return;
        var output = OptionValue(arguments, "-o", "--output")
            ?? throw new InvalidDataException(
                "Source screenshots require -o or --output under the report-declared source evidence root.");
        var candidate = Path.IsPathFullyQualified(output)
            ? output
            : Path.Combine(workspace.Target.FullName, output);
        var relative = Path.GetRelativePath(workspace.Target.FullName, candidate)
            .Replace('\\', '/');
        if (!EnforcementStore.TryResolveContainedPath(
                workspace.Target,
                relative,
                out var resolved,
                out var pathError))
        {
            throw new InvalidDataException(pathError);
        }
        var rootPrefix = Path.TrimEndingDirectorySeparator(
                workspace.Resolve(workspace.EvidenceRoot))
            + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Source screenshot output must remain under the report-declared source evidence root.");
        }
    }

    private static string? OptionValue(
        IReadOnlyList<string> arguments,
        params string[] names)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (names.Contains(arguments[index], StringComparer.OrdinalIgnoreCase))
                return index + 1 < arguments.Count ? arguments[index + 1] : null;
            foreach (var name in names)
            {
                var prefix = name + "=";
                if (arguments[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return arguments[index][prefix.Length..];
            }
        }
        return null;
    }

    private static async Task PersistRunAsync(
        SourceWorkspace workspace,
        SourceRunAttestation attestation,
        string directory,
        CancellationToken cancellationToken)
    {
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            $"{directory}/attestation.json",
            attestation,
            cancellationToken);
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            $"{workspace.GatewayRoot}/current-run.json",
            attestation,
            cancellationToken);
    }

    private static async Task<SourceRunAttestation> LoadCurrentRunAsync(
        SourceWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var path = $"{workspace.GatewayRoot}/current-run.json";
        var (attestation, error) =
            await EnforcementStore.TryLoadAsync<SourceRunAttestation>(
                workspace.Target,
                path,
                cancellationToken);
        if (error is not null)
            throw new InvalidDataException(error);
        if (attestation is null ||
            attestation.SchemaVersion != SourceRunAttestation.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "A valid current source run attestation is required before source UI automation.");
        }
        return attestation;
    }

    private static async Task WriteOutputAsync(
        DirectoryInfo target,
        string stdoutPath,
        string stderrPath,
        EnforcementProcessResult result,
        CancellationToken cancellationToken)
    {
        await EnforcementStore.WriteTextAtomicAsync(
            target,
            stdoutPath,
            result.Output,
            cancellationToken);
        await EnforcementStore.WriteTextAtomicAsync(
            target,
            stderrPath,
            result.Error,
            cancellationToken);
    }

    private static string CreateId(string prefix) =>
        $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
        ?? "No diagnostic output was produced.";

    private sealed record SourceWorkspace(
        DirectoryInfo Target,
        DirectoryInfo SourceRoot,
        string SourceProject,
        string SourceProjectSha256,
        string SourceFingerprint,
        string EvidenceRoot,
        string GatewayRoot,
        FileStream OperationLock)
        : IAsyncDisposable
    {
        internal string Resolve(string relativePath) =>
            Path.Combine(
                Target.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

        public ValueTask DisposeAsync() =>
            OperationLock.DisposeAsync();
    }
}
