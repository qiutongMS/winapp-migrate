// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using UwpMigration.Mechanical;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

public sealed partial class UwpEnforcementRunner
{
    public static async Task<SourceNavigationContract> BindSourceAsync(
        DirectoryInfo target, string stateId, IReadOnlyList<string> probePaths,
        CancellationToken cancellationToken = default)
    {
        await using var enforcementLock = await EnforcementStore.AcquireLockAsync(ResolveLockTarget(target), cancellationToken);
        var workspace = await PrepareWorkspaceAsync(target, cancellationToken);
        await RequireNonterminalAsync(workspace.Target, EnforcementEvaluator.Evaluate(workspace.Input), cancellationToken);
        var plan = workspace.Input.StatePlan ?? throw new InvalidDataException("A valid surface inventory is required.");
        var state = plan.States.SingleOrDefault(item => item.Id == stateId) ??
            throw new InvalidDataException($"Unknown planned surface: {stateId}.");
        RequiredStateContracts.Require(workspace.Target, state);
        if (!RequiredStateContracts.UsesSourceNavigation(workspace.Target))
            throw new InvalidDataException("Legacy navigation is already frozen; use a fresh workspace for source discovery.");
        probePaths = probePaths.Select(path =>
        {
            if (!EnforcementStore.TryResolveContainedPath(workspace.Target, path, out var fullPath, out var error))
                throw new InvalidDataException(error);
            return Path.GetRelativePath(workspace.Target.FullName, fullPath).Replace('\\', '/');
        }).ToArray();
        if (probePaths.Count == 0 || probePaths.Distinct(StringComparer.Ordinal).Count() != probePaths.Count)
            throw new InvalidDataException("An ordered sequence of distinct source probe receipts is required.");
        var report = await MigrationReportStore.LoadAsync(workspace.ReportPath, cancellationToken);
        var gatewayRoot = report.Validation.SourceBaseline.EvidenceRoot.TrimEnd('/') + "/gateway";
        var probes = new List<SourceUiProbe>();
        var evidence = new List<string>();
        SourceRunAttestation? run = null;
        foreach (var path in probePaths)
        {
            var probe = await ReadNavigationEvidenceAsync<SourceUiProbe>(workspace.Target, path, cancellationToken);
            if (path != $"{gatewayRoot}/probes/{probe.Id}/probe.json" ||
                probe.ArtifactPath != path ||
                probe.StandardOutputPath != $"{gatewayRoot}/probes/{probe.Id}/stdout.log" ||
                probe.StandardErrorPath != $"{gatewayRoot}/probes/{probe.Id}/stderr.log" ||
                probe.SchemaVersion != SourceUiProbe.CurrentSchemaVersion || probe.Producer != MigrationArtifactClass.Producer ||
                probe.ArtifactClass != MigrationArtifactClass.DevelopmentProbe || probe.ExitCode != 0 ||
                probe.SourceRunAttestationId is null || probe.Command is not { Count: >= 3 } || probe.Command[1] != "ui")
                throw new InvalidDataException("Source navigation requires successful canonical source gateway receipts.");
            SourceGatewayAuthentication.Require(workspace.Target, path, probe.StandardOutputPath, probe.StandardErrorPath);
            if (run is null)
            {
                var runPath = $"{gatewayRoot}/runs/{probe.SourceRunAttestationId}/attestation.json";
                run = await ReadNavigationEvidenceAsync<SourceRunAttestation>(workspace.Target, runPath, cancellationToken);
                SourceGatewayAuthentication.Require(workspace.Target, runPath, run.StandardOutputPath, run.StandardErrorPath);
                var sourceRoot = Path.GetFullPath(Path.IsPathRooted(report.Source.Root) ? report.Source.Root :
                    Path.Combine(workspace.Target.FullName, report.Source.Root));
                var sourceProject = Path.GetFullPath(Path.Combine(sourceRoot, report.Source.ProjectFile!));
                if (run.Mode != "detached" || run.ExitCode != 0 || run.SourceBuildExitCode != 0 ||
                    !string.Equals(run.SourceProject, sourceProject, StringComparison.OrdinalIgnoreCase) ||
                    run.SourceFingerprint != await TargetFingerprint.ComputeAsync(new DirectoryInfo(sourceRoot), cancellationToken) ||
                    run.SourceProjectSha256 != Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourceProject, cancellationToken))))
                    throw new InvalidDataException("Source navigation is not bound to the unchanged report-recorded source.");
            }
            var previous = probes.LastOrDefault()?.CompletedAt ?? run.CompletedAt;
            if (probe.SourceRunAttestationId != run.Id || probe.SourceFingerprint != run.SourceFingerprint ||
                probe.SourceProjectSha256 != run.SourceProjectSha256 ||
                probe.StartedAt < previous || probe.CompletedAt < probe.StartedAt || probe.CompletedAt > workspace.Input.GeneratedAt ||
                probe.WindowHandle != ReplayWindowBinding.RequireWindow(probe.Command.Skip(2).ToArray()) ||
                !run.AuthorizedWindows.Any(window => window.Hwnd == probe.WindowHandle))
                throw new InvalidDataException("Source navigation must be ordered within one authorized source launch.");
            probes.Add(probe);
            evidence.AddRange([path, probe.StandardOutputPath, probe.StandardErrorPath]);
        }
        var navigation = state.Verification ?? throw new InvalidDataException("Declare the observed navigation recipe before binding it.");
        if (navigation.TreeProbeIndex != probes.Count - 1 ||
            probes[0].Command[2] != "inspect" || probes[^1].Command[2] != "inspect" ||
            !SurfaceTreeComparison.IsTree(workspace.Target, probes[0].StandardOutputPath, out _) ||
            !SurfaceTreeComparison.IsTree(workspace.Target, probes[^1].StandardOutputPath, out _))
            throw new InvalidDataException("Source navigation needs complete initial and final inspect trees.");
        foreach (var action in navigation.RequiredActions)
        {
            if (action.ProbeIndex >= probes.Count ||
                !RequiredActionProtocol.Normalize(action.Command).SequenceEqual(
                    RequiredActionProtocol.Normalize(probes[action.ProbeIndex].Command.Skip(2).ToArray()), StringComparer.Ordinal))
                throw new InvalidDataException($"Source navigation action {action.Id} does not match its observed probe.");
        }
        var history = await SourceGatewayAuthentication.ProbeHistoryAsync(workspace.Target, gatewayRoot, run!.Id, cancellationToken);
        var allProbes = new List<SourceUiProbe>();
        foreach (var path in history)
        {
            var probe = await ReadNavigationEvidenceAsync<SourceUiProbe>(workspace.Target, path, cancellationToken);
            SourceGatewayAuthentication.Require(workspace.Target, path, probe.StandardOutputPath, probe.StandardErrorPath);
            allProbes.Add(probe);
        }
        if (probePaths.Any(path => !history.Contains(path, StringComparer.Ordinal)))
            throw new InvalidDataException("Source navigation is absent from the authenticated source probe history.");
        if (navigation.StartSurfaceId is { } startId)
        {
            var start = plan.States.SingleOrDefault(item => item.Id == startId) ??
                throw new InvalidDataException($"Unknown navigation start surface: {startId}.");
            if (SourceNavigationContracts.Require(workspace.Target, start) is null)
                throw new InvalidDataException($"Capture and bind source start surface {startId} before source discovery.");
            if (!SurfaceTreeComparison.Compare(workspace.Target, start.Source.TreeEvidence!, probes[0].StandardOutputPath).Matches)
                throw new InvalidDataException($"Source start state differs from surface {startId}; recover the declared state before discovery.");
        }
        else
        {
            if (allProbes.Any(prior => prior.StartedAt < probes[0].StartedAt &&
                    !SourceNavigationContracts.IsReadOnly(prior.Command.Skip(2).ToArray())))
                throw new InvalidDataException("A fresh-launch source baseline cannot be captured after navigation; use its first inspect or declare startSurfaceId.");
        }
        var expected = probes.Where(probe => !SourceNavigationContracts.IsReadOnly(probe.Command.Skip(2).ToArray())).Select(probe => probe.Id);
        var observed = allProbes.Where(probe => probe.StartedAt >= probes[0].StartedAt &&
            probe.StartedAt <= probes[^1].CompletedAt &&
            !SourceNavigationContracts.IsReadOnly(probe.Command.Skip(2).ToArray()));
        if (!observed.Select(probe => probe.Id).SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("Source discovery omitted a failed or state-changing action; recover the start state and capture a fresh trace.");
        state.Source = new StateObservation
        {
            Status = "captured",
            TreeEvidence = probes[^1].StandardOutputPath,
            Evidence = evidence
        };
        var contract = await SourceNavigationContracts.EstablishAsync(workspace.Target, state, run!.Id,
            evidence, workspace.Input.GeneratedAt, cancellationToken);
        await EnforcementStore.WriteAtomicAsync(workspace.Target, report.Validation.StatePlan, plan, cancellationToken);
        return contract;
    }

    public async Task<IReadOnlyList<UiDevelopmentProbe>> NavigateAsync(
        DirectoryInfo target, string stateId, string winAppExecutable, long windowHandle,
        CancellationToken cancellationToken = default)
    {
        MigrationState state;
        await using (var enforcementLock = await EnforcementStore.AcquireLockAsync(ResolveLockTarget(target), cancellationToken))
        {
            var workspace = await PrepareWorkspaceAsync(target, cancellationToken, _timeProvider, _windowOwnerResolver);
            state = workspace.Input.StatePlan?.States.SingleOrDefault(item => item.Id == stateId) ??
                throw new InvalidDataException($"Unknown planned surface: {stateId}.");
            SourceNavigationContracts.Require(workspace.Target, state);
            if (!RequiredStateContracts.UsesSourceNavigation(workspace.Target))
                throw new InvalidDataException("Automatic navigation requires a declared route in a fresh workspace.");
        }
        var probes = new List<UiDevelopmentProbe>();
        async Task<UiDevelopmentProbe> CaptureAsync(IReadOnlyList<string> command)
        {
            var probe = await InvokeUiAsync(target, winAppExecutable,
                [.. command, "-w", windowHandle.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                [], stateId, cancellationToken);
            probes.Add(probe);
            return probe;
        }
        var initial = await CaptureAsync(["inspect", "--json", "--depth", "30"]);
        if (initial.ExitCode != 0 || initial.InfrastructureFailure)
            return probes;
        await using (var enforcementLock = await EnforcementStore.AcquireLockAsync(ResolveLockTarget(target), cancellationToken))
        {
            var workspace = await PrepareWorkspaceAsync(target, cancellationToken, _timeProvider, _windowOwnerResolver);
            SourceNavigationContracts.ValidateTargetStart(workspace.Target, state, workspace.Input.Replay!,
                initial, workspace.Input.Progress!, _timeProvider.GetUtcNow(), _windowOwnerResolver);
        }
        foreach (var action in state.Verification!.RequiredActions)
        {
            var probe = await CaptureAsync(SelectorBindingAmendments.ResolveCommand(target, state, action));
            if (probe.ExitCode != 0 || probe.InfrastructureFailure)
                return probes;
        }
        if (state.Verification.RequiredActions.Count > 0)
            await CaptureAsync(["inspect", "--json", "--depth", "30"]);
        return probes;
    }

    private static async Task<T> ReadNavigationEvidenceAsync<T>(DirectoryInfo root, string path, CancellationToken token) where T : class
    {
        var (value, error) = await EnforcementStore.TryLoadAsync<T>(root, path, token);
        return value ?? throw new InvalidDataException(error ?? $"Missing navigation evidence: {path}.");
    }
}
