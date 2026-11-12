// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using UwpMigration.Contracts;
using UwpMigration.Mechanical;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

public sealed class FourCheckAcceptance
{
    public const string SurfaceMapPath = ".migration-evidence/acceptance/four-checks-surfaces.json";
    public const string InventoryPath = ".migration-evidence/acceptance/four-checks-inventory.json";
    public const string ResultPath = ".migration-evidence/acceptance/four-checks-result.json";
    private readonly FourCheckInventoryBuilder _inventoryBuilder;
    private readonly IWindowOwnerResolver _windowOwnerResolver;

    public FourCheckAcceptance(IEnforcementProcessRunner processRunner, IWindowOwnerResolver? windowOwnerResolver = null)
        : this(new UwpSourceBuilder(processRunner).InspectItemsAsync, windowOwnerResolver)
    {
    }

    internal FourCheckAcceptance(
        Func<FileInfo, CancellationToken, Task<IReadOnlyList<(string Kind, string Path)>>> inspectItems,
        IWindowOwnerResolver? windowOwnerResolver = null)
    {
        _inventoryBuilder = new(inspectItems);
        _windowOwnerResolver = windowOwnerResolver ?? WindowsWindowOwnerResolver.Instance;
    }

    public async Task<FourCheckInventory> InventoryAsync(
        DirectoryInfo target, CancellationToken cancellationToken = default)
    {
        await using var operationLock = await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var (report, source, project) = await LoadSourceAsync(target, cancellationToken);
        var inventory = await _inventoryBuilder.BuildAsync(project, cancellationToken);
        if (!EnforcementStore.TryResolveContainedPath(target, SurfaceMapPath, out var planFile, out var error))
            throw new InvalidDataException(error);
        var plan = !File.Exists(planFile) ? new FourCheckSurfaceMap
        {
            SourceInputFingerprint = inventory.SourceInputFingerprint
        } : FourCheckJson.Read<FourCheckSurfaceMap>(target, SurfaceMapPath);
        ValidateMap(plan);
        AddDeclaredSurfaces(plan, inventory);
        var states = await LoadStatesAsync(target, report, cancellationToken);
        AddRuntimeSurfaces(target, report, source, project, plan, states, inventory);
        foreach (var surface in inventory.Surfaces.Where(surface => surface.Kind != "runtime-uia"))
            if (plan.Mappings.All(mapping => mapping.SurfaceId != surface.Id))
                plan.Mappings.Add(new() { SurfaceId = surface.Id });
        await FourCheckJson.WriteAsync(target, InventoryPath, inventory, cancellationToken);
        await FourCheckJson.WriteAsync(target, SurfaceMapPath, plan, cancellationToken);
        return inventory;
    }

    public async Task<FourCheckResult> AssessAsync(
        DirectoryInfo target, CancellationToken cancellationToken = default)
    {
        await using var operationLock = await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var (report, source, project) = await LoadSourceAsync(target, cancellationToken);
        var plan = FourCheckJson.Read<FourCheckSurfaceMap>(target, SurfaceMapPath);
        ValidateMap(plan);
        var inventory = await _inventoryBuilder.BuildAsync(project, cancellationToken);
        if (plan.SourceInputFingerprint != inventory.SourceInputFingerprint)
            inventory.DiscoveryIssues.Add("Source evaluated inputs changed. Review a new inventory and explicitly update the plan fingerprint.");
        AddDeclaredSurfaces(plan, inventory);
        var plannedStates = await LoadStatesAsync(target, report, cancellationToken);
        AddRuntimeSurfaces(target, report, source, project, plan, plannedStates, inventory);
        var fingerprint = await TargetFingerprint.ComputeAsync(target, cancellationToken);
        var states = new List<FourCheckStateResult>();
        var startupIssues = new List<string>();
        ReplayAttestation? replay = null;
        try
        {
            var build = ReadGateway<BuildAttestation>(target, EnforcementStore.BuildAttestationPath);
            replay = ReadGateway<ReplayAttestation>(target, EnforcementStore.ReplayAttestationPath);
            if (!build.Success || build.ExitCode != 0 || !replay.Success || replay.ExitCode != 0 ||
                build.TargetFingerprint != fingerprint || replay.TargetFingerprint != fingerprint ||
                !CanonicalAttestationTrust.Verify(target, build) || !CanonicalAttestationTrust.Verify(target, replay))
                throw new InvalidDataException("Startup requires current authenticated successful build and replay evidence.");
        }
        catch (Exception exception) when (IsEvidenceError(exception))
        {
            replay = null;
            startupIssues.Add(exception.Message);
        }
        foreach (var state in plannedStates)
        {
            try
            {
                if (replay is null) throw new InvalidDataException("A fresh authenticated target replay is required.");
                var sourceProbes = ProbePaths(state.Source);
                var targetProbes = ProbePaths(state.Target);
                var left = ReadSourceSequence(target, report, source, project, sourceProbes);
                var sourceEndpoint = ReadGateway<SourceUiProbe>(target, sourceProbes[^1]);
                if (state.Assertions.Any(assertion => assertion.Kind != "ui-property-equals-source" ||
                        assertion.ProbeIndex != targetProbes.Count - 1 ||
                        StateClosureProtocol.FileHash(target, assertion.SourceEvidence) != sourceEndpoint.StandardOutputSha256))
                    throw new InvalidDataException("Four-checks reuses nonvisual endpoint assertions in state-plan; visual or intermediate assertions require a separate scope.");
                var proof = StateClosureProtocol.Derive(
                    target, state, replay, targetProbes, DateTimeOffset.UtcNow, _windowOwnerResolver);
                if (proof.VerificationLevel != StateVerificationLevel.ParityVerified)
                    throw new InvalidDataException(string.Join(" ", proof.Issues));
                if (targetProbes.Count(path => ReadGateway<UiDevelopmentProbe>(target, path).Command[2] == "inspect") != 1)
                    throw new InvalidDataException("Each state needs exactly one target inspect endpoint; account intermediate observations separately.");
                var endpoint = ReadGateway<UiDevelopmentProbe>(target, targetProbes[^1]);
                var right = UiStructureComparison.ReadCapture(
                    ReadGateway<JsonElement>(target, endpoint.StandardOutputPath), endpoint.Command, endpoint.WindowHandle);
                states.Add(new(state.Id, "passed",
                    UiStructureComparison.Compare(left, right, plan.StructureExceptions.GetValueOrDefault(state.Id) ?? []), null));
            }
            catch (Exception exception) when (IsEvidenceError(exception))
            {
                states.Add(new(state.Id, "unknown", null, exception.Message));
            }
        }
        var accountingIssues = new List<string>(inventory.DiscoveryIssues);
        var reachabilityIssues = new List<string>(inventory.DiscoveryIssues);
        var requiredStates = new HashSet<string>(StringComparer.Ordinal);
        var accounted = 0;
        var reachable = 0;
        foreach (var surface in inventory.Surfaces)
        {
            if (surface.Kind == "runtime-uia")
            {
                var stateId = surface.Origin;
                if (states.Any(state => state.Id == stateId && state.Structure?.Status == "passed")) accounted++;
                else accountingIssues.Add($"{surface.Id}: observed UIA surface has no complete structural target mapping.");
                continue;
            }
            var mapping = plan.Mappings.SingleOrDefault(item => item.SurfaceId == surface.Id);
            if (!TryAccount(target, surface, mapping, out var reason))
            {
                accountingIssues.Add($"{surface.Id}: {reason}");
                if (surface.RequiresReachability) reachabilityIssues.Add($"{surface.Id}: no explicit disposition.");
                continue;
            }
            accounted++;
            if (!surface.RequiresReachability) continue;
            if (mapping!.Disposition == "not-applicable")
            {
                reachable++;
                continue;
            }
            foreach (var stateId in mapping.StateIds) requiredStates.Add(stateId);
            if (mapping.StateIds.Count == 0 ||
                mapping.StateIds.Any(id => !states.Any(state => state.Id == id && state.Reachability == "passed")))
                reachabilityIssues.Add($"{surface.Id}: all mapped major states require successful endpoint observations.");
            else reachable++;
        }
        foreach (var group in inventory.Surfaces.Where(surface => surface.ExclusiveGroup is not null)
                     .GroupBy(surface => surface.ExclusiveGroup, StringComparer.Ordinal))
        {
            var assignments = group.SelectMany(surface => plan.Mappings
                .Where(mapping => mapping.SurfaceId == surface.Id && mapping.Disposition == "mapped")
                .SelectMany(mapping => mapping.StateIds)).ToArray();
            if (assignments.Distinct(StringComparer.Ordinal).Count() != assignments.Length)
                reachabilityIssues.Add($"{group.Key}: mutually exclusive VisualStates cannot share the same state observation.");
        }
        var staleMappings = plan.Mappings.Where(mapping =>
            inventory.Surfaces.All(surface => surface.Id != mapping.SurfaceId)).ToArray();
        if (staleMappings.Length > 0) accountingIssues.Add("Plan contains stale or unknown surface mappings.");
        if (plannedStates.Count == 0 || !states.Any(state => state.Reachability == "passed"))
            startupIssues.Add("No successful post-launch inspect proves an observable, responsive application window.");
        if (requiredStates.Count == 0) reachabilityIssues.Add("No required application states have been accounted for.");
        var structureIssues = states.SelectMany(state =>
            state.Structure is null ? new[] { $"{state.Id}: {state.Issue}" } :
                state.Structure.Differences.Concat(state.Structure.Unknowns).Select(issue => $"{state.Id}: {issue}")).ToList();
        if (states.Count == 0) structureIssues.Add("No paired source/target application trees were compared.");
        var checks = new[]
        {
            Check("startup", 1, startupIssues.Count == 0 ? 1 : 0, startupIssues),
            Check("surface-accounting", inventory.Surfaces.Count, accounted, accountingIssues),
            Check("reachability", inventory.Surfaces.Count(surface => surface.RequiresReachability), reachable, reachabilityIssues),
            new AcceptanceCheck("semantic-structure",
                states.Any(state => state.Structure?.Status == "failed") ? "failed" :
                    structureIssues.Count > 0 ? "unknown" : "passed",
                states.Count, states.Count(state => state.Structure?.Status == "passed"), structureIssues)
        };
        var result = new FourCheckResult("1.0", "four-checks",
            checks.All(check => check.Status == "passed") ? "passed" :
                checks.Any(check => check.Status == "failed") ? "failed" : "partial",
            fingerprint, inventory.SourceInputFingerprint, checks, states,
            ["strict-closure", "full-functional-parity", "visual-parity", "pixel-equivalence",
             "lazy-object-lifecycle", "performance", "unobserved-runtime-behavior"]);
        await FourCheckJson.WriteAsync(target, InventoryPath, inventory, cancellationToken);
        await FourCheckJson.WriteAsync(target, ResultPath, result, cancellationToken);
        return result;
    }

    private static AcceptanceCheck Check(string id, int required, int satisfied, List<string> issues) =>
        new(id, required > 0 && issues.Count == 0 ? "passed" : "unknown", required, satisfied, issues);

    private static void ValidateMap(FourCheckSurfaceMap plan)
    {
        if (plan.SchemaVersion != "1.0" || plan.Profile != "four-checks" ||
            string.IsNullOrWhiteSpace(plan.SourceInputFingerprint) ||
            plan.Mappings is null || plan.StructureExceptions is null || plan.AdditionalSurfaces is null ||
            plan.Mappings.Any(mapping => mapping is null || string.IsNullOrWhiteSpace(mapping.SurfaceId) || mapping.StateIds is null) ||
            plan.Mappings.Select(mapping => mapping.SurfaceId).Distinct(StringComparer.Ordinal).Count() != plan.Mappings.Count ||
            plan.StructureExceptions.Any(pair => pair.Value is null || pair.Value.Any(item => item is null)) ||
            plan.AdditionalSurfaces.Any(surface => surface is null))
            throw new InvalidDataException("Malformed, duplicate or unsupported four-checks surface map.");
    }

    private static void AddDeclaredSurfaces(FourCheckSurfaceMap plan, FourCheckInventory inventory)
    {
        foreach (var surface in plan.AdditionalSurfaces)
        {
            if (string.IsNullOrWhiteSpace(surface.Id) || string.IsNullOrWhiteSpace(surface.Kind) ||
                surface.Kind == "runtime-uia" || string.IsNullOrWhiteSpace(surface.Reason))
                throw new InvalidDataException("Additional dynamic surfaces require unique IDs, non-runtime-uia kinds and discovery reasons.");
            inventory.Surfaces.Add(new($"declared:{surface.Id}", surface.Kind, "declared",
                surface.Reason, true));
        }
        if (inventory.Surfaces.Select(surface => surface.Id).Distinct(StringComparer.Ordinal).Count() != inventory.Surfaces.Count)
            throw new InvalidDataException("Duplicate surface IDs.");
    }

    private static void AddRuntimeSurfaces(
        DirectoryInfo target, MigrationReport report, DirectoryInfo source, FileInfo project,
        FourCheckSurfaceMap plan, IReadOnlyList<MigrationState> states, FourCheckInventory inventory)
    {
        var assigned = new HashSet<string>(
            states.SelectMany(state => ProbePaths(state.Source)), StringComparer.Ordinal);
        var gatewayRoot = $"{report.Validation.SourceBaseline.EvidenceRoot.TrimEnd('/')}/gateway/probes";
        if (EnforcementStore.TryResolveContainedPath(target, gatewayRoot, out var probesRoot, out _) &&
            Directory.Exists(probesRoot))
        {
            foreach (var directory in new DirectoryInfo(probesRoot).EnumerateDirectories())
            {
                if (directory.LinkTarget is not null)
                {
                    inventory.DiscoveryIssues.Add("Source runtime probe directory is a reparse point.");
                    continue;
                }
                var path = $"{gatewayRoot}/{directory.Name}/probe.json";
                if (assigned.Contains(path)) continue;
                try
                {
                    var probe = ReadGateway<SourceUiProbe>(target, path);
                    if (probe.Command.Count < 3 || probe.Command[2] != "inspect" || probe.ExitCode != 0 ||
                        !SourceAcceptanceSeal.Verify(probe) || string.IsNullOrWhiteSpace(probe.SourceRunAttestationId))
                        continue;
                    if (probe.SourceRunAttestationId.Any(character =>
                            !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
                        throw new InvalidDataException("Invalid unassigned source run identity.");
                    var run = ReadGateway<SourceRunAttestation>(target,
                        $"{report.Validation.SourceBaseline.EvidenceRoot.TrimEnd('/')}/gateway/runs/{probe.SourceRunAttestationId}/attestation.json");
                    if (SourceAcceptanceSeal.Verify(run) &&
                        run.AcceptanceSourceInputFingerprint == inventory.SourceInputFingerprint)
                        inventory.DiscoveryIssues.Add($"Unassigned source inspect observation: {path}. Map it to a recorded state; do not silently omit observed surfaces.");
                }
                catch (Exception exception) when (IsEvidenceError(exception))
                {
                    inventory.DiscoveryIssues.Add(exception.Message);
                }
            }
        }
        foreach (var state in states)
        {
            try
            {
                var nodes = ReadSourceSequence(target, report, source, project, ProbePaths(state.Source));
                inventory.Surfaces.AddRange(nodes.Select(node => new AcceptanceSurface(
                    $"runtime:{state.Id}:{node.Path}", "runtime-uia", state.Id,
                    $"{node.Type} {node.Name}", false)));
            }
            catch (Exception exception) when (IsEvidenceError(exception))
            {
                inventory.DiscoveryIssues.Add($"{state.Id}: source runtime inventory incomplete: {exception.Message}");
            }
        }
        if (states.Count == 0) inventory.DiscoveryIssues.Add("Source runtime surface inventory has not been captured.");
        if (plan.StructureExceptions.Keys.Any(id => states.All(state => state.Id != id)))
            inventory.DiscoveryIssues.Add("Structure exceptions reference an unknown state-plan state.");
    }

    private static bool TryAccount(
        DirectoryInfo target, AcceptanceSurface surface, SurfaceMapping? mapping, out string reason)
    {
        reason = "An explicit mapped or not-applicable disposition and rationale are required.";
        if (mapping is null || string.IsNullOrWhiteSpace(mapping.Reason)) return false;
        if (mapping.Disposition == "not-applicable") return true;
        if (mapping.Disposition != "mapped" || string.IsNullOrWhiteSpace(mapping.TargetFile) ||
            !EnforcementStore.TryResolveContainedPath(target, mapping.TargetFile, out var path, out _) ||
            !File.Exists(path) || path.Contains($"{Path.DirectorySeparatorChar}.migration-evidence{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return false;
        if (surface.Id.StartsWith("xaml:", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(mapping.TargetNode)) return false;
            try
            {
                var document = XDocument.Load(path);
                var element = document.Root;
                var components = mapping.TargetNode.Split('.');
                if (components[0] != "0") return false;
                foreach (var component in components.Skip(1))
                {
                    if (!int.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                        return false;
                    element = element?.Elements().ElementAtOrDefault(index);
                }
                if (element is null) return false;
            }
            catch (Exception exception) when (exception is IOException or System.Xml.XmlException)
            {
                reason = exception.Message;
                return false;
            }
        }
        return true;
    }

    private static IReadOnlyList<UiStructureNode> ReadSourceSequence(
        DirectoryInfo target, MigrationReport report, DirectoryInfo source, FileInfo project,
        List<string> paths)
    {
        using var verification = StateClosureSeal.BeginVerification();
        if (paths.Count == 0 || paths.Distinct(StringComparer.Ordinal).Count() != paths.Count)
            throw new InvalidDataException("A nonempty ordered source gateway probe sequence is required.");
        var gateway = $"{report.Validation.SourceBaseline.EvidenceRoot.TrimEnd('/')}/gateway";
        var plan = FourCheckJson.Read<FourCheckSurfaceMap>(target, SurfaceMapPath);
        SourceUiProbe? last = null;
        SourceRunAttestation? run = null;
        var previous = DateTimeOffset.MinValue;
        var inspectCount = 0;
        foreach (var path in paths)
        {
            var probe = ReadGateway<SourceUiProbe>(target, path);
            if (string.IsNullOrWhiteSpace(probe.Id) ||
                probe.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') ||
                string.IsNullOrWhiteSpace(probe.SourceRunAttestationId) ||
                probe.SourceRunAttestationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
                throw new InvalidDataException("Invalid source gateway probe/run identity.");
            run ??= ReadGateway<SourceRunAttestation>(target,
                $"{gateway}/runs/{probe.SourceRunAttestationId}/attestation.json");
            if (!SourceAcceptanceSeal.Verify(run) || !SourceAcceptanceSeal.Verify(probe) ||
                run.AcceptanceSourceInputFingerprint != plan.SourceInputFingerprint ||
                run.SourceFingerprint != TargetFingerprint.Compute(source) ||
                run.SourceRoot != source.FullName || run.SourceProject != project.FullName ||
                run.SourceProjectSha256 != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(project.FullName))) ||
                run.ExitCode != 0 || run.SourceBuildExitCode != 0 ||
                probe.SourceRunAttestationId != run.Id ||
                probe.SourceFingerprint != run.SourceFingerprint ||
                probe.SourceProjectSha256 != run.SourceProjectSha256 ||
                probe.SchemaVersion != SourceUiProbe.CurrentSchemaVersion ||
                probe.Producer != MigrationArtifactClass.Producer ||
                probe.ExitCode != 0 || probe.StartedAt < run.CompletedAt ||
                probe.StartedAt < previous || probe.CompletedAt < probe.StartedAt ||
                probe.CompletedAt > DateTimeOffset.UtcNow ||
                path != $"{gateway}/probes/{probe.Id}/probe.json" ||
                probe.ArtifactPath != path ||
                probe.StandardOutputPath != $"{gateway}/probes/{probe.Id}/stdout.log" ||
                probe.StandardErrorPath != $"{gateway}/probes/{probe.Id}/stderr.log" ||
                probe.StandardOutputSha256 != StateClosureProtocol.FileHash(target, probe.StandardOutputPath) ||
                probe.StandardErrorSha256 != StateClosureProtocol.FileHash(target, probe.StandardErrorPath) ||
                !run.AuthorizedWindows.Any(window => window.Hwnd == probe.WindowHandle))
                throw new InvalidDataException("Source probe is unsuccessful, stale, reordered or not bound to the recorded source run.");
            previous = probe.CompletedAt;
            if (probe.Command.Count >= 3 && probe.Command[2] == "inspect") inspectCount++;
            last = probe;
        }
        if (inspectCount != 1)
            throw new InvalidDataException("Each state requires exactly one source inspect endpoint; account intermediate observed states separately.");
        return UiStructureComparison.ReadCapture(
            ReadGateway<JsonElement>(target, last!.StandardOutputPath), last.Command, last.WindowHandle);
    }

    private static List<string> ProbePaths(StateObservation observation) =>
        observation.Evidence.Where(path => path.EndsWith("/probe.json", StringComparison.Ordinal)).ToList();

    private static async Task<IReadOnlyList<MigrationState>> LoadStatesAsync(
        DirectoryInfo target, MigrationReport report, CancellationToken cancellationToken)
    {
        var (plan, error) = await EnforcementStore.TryLoadAsync<StatePlanDocument>(
            target, report.Validation.StatePlan, cancellationToken);
        if (error is not null) throw new InvalidDataException(error);
        if (plan is null) return [];
        if (plan.SchemaVersion != StatePlanDocument.CurrentSchemaVersion)
            throw new InvalidDataException("A current state-plan is required.");
        if (plan.States.Count > 0) RequiredStateContracts.ValidateRequirements(plan);
        return plan.States;
    }

    private static T ReadGateway<T>(DirectoryInfo target, string path)
    {
        if (!EnforcementStore.TryResolveContainedPath(target, path, out var fullPath, out var error))
            throw new InvalidDataException(error);
        return JsonSerializer.Deserialize<T>(File.ReadAllText(fullPath), EnforcementStore.JsonOptions)
            ?? throw new InvalidDataException($"Empty gateway artifact: {path}");
    }

    private static async Task<(MigrationReport Report, DirectoryInfo Source, FileInfo Project)> LoadSourceAsync(
        DirectoryInfo target, CancellationToken cancellationToken)
    {
        var report = await MigrationReportStore.LoadAsync(
            Path.Combine(target.FullName, "migration-report.json"), cancellationToken);
        if (report.SchemaVersion != MigrationReport.CurrentSchemaVersion)
            throw new InvalidDataException("Four-checks requires a current migration report.");
        var sourcePath = Path.GetFullPath(report.Source.Root, target.FullName);
        if (!MigrationPathResolver.TryCanonicalizeRoot(sourcePath, out var canonicalSource, out var error) ||
            string.IsNullOrWhiteSpace(report.Source.ProjectFile) ||
            !MigrationPathResolver.TryResolveContainedRelativePath(canonicalSource, report.Source.ProjectFile,
                out var project, out _, out error))
            throw new InvalidDataException(error ?? "Missing recorded source project.");
        return (report, new(canonicalSource), new(project));
    }

    private static bool IsEvidenceError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException;

}
