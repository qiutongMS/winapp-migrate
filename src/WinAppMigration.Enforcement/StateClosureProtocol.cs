// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinAppMigration.Enforcement;

public sealed record StateEvidenceHash(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256);

public static class SurfaceVerificationResult
{
    public const string Pending = "not-run";
    public const string Verified = "surface-parity-verified";
    public const string Mismatch = "surface-mismatch";
    public const string Unreachable = "surface-unreachable";
    public const string HumanValidationRequired =
        "human-validation-required";
}

public sealed class StateClosureAttestation
{
    [JsonPropertyName("requiredContractHash")]
    public string RequiredContractHash { get; set; } = string.Empty;
    [JsonPropertyName("selectorBindingHash")]
    public string? SelectorBindingHash { get; set; }
    [JsonPropertyName("verificationLevel")]
    public string VerificationLevel { get; set; } =
        StateVerificationLevel.EvidenceValid;
    [JsonPropertyName("result")]
    public string Result { get; set; } =
        SurfaceVerificationResult.HumanValidationRequired;
    [JsonPropertyName("requiredActionIds")]
    public List<string> RequiredActionIds { get; set; } = [];
    [JsonPropertyName("verifiedNavigation")]
    public List<StateStepReceipt> VerifiedSteps { get; set; } = [];
    [JsonPropertyName("issues")]
    public List<string> Issues { get; set; } = [];
    [JsonPropertyName("comparisonDiagnostics")]
    public List<SurfaceComparisonDiagnostic>? ComparisonDiagnostics { get; set; }
    [JsonPropertyName("seal")]
    public string? Seal { get; set; }
    [JsonPropertyName("sealPublicKey")]
    public string? SealPublicKey { get; set; }
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "2.0";
    [JsonPropertyName("artifactClass")]
    public string ArtifactClass { get; set; } =
        MigrationArtifactClass.CanonicalAttestation;
    [JsonPropertyName("producer")]
    public string Producer { get; set; } =
        MigrationArtifactClass.Producer;
    [JsonPropertyName("stateId")]
    public required string StateId { get; set; }
    [JsonPropertyName("stateContractFingerprint")]
    public required string StateContractFingerprint { get; set; }
    [JsonPropertyName("targetFingerprint")]
    public required string TargetFingerprint { get; set; }
    [JsonPropertyName("replayAttestationId")]
    public required string ReplayAttestationId { get; set; }
    [JsonPropertyName("recordedAt")]
    public DateTimeOffset RecordedAt { get; set; }
    [JsonPropertyName("probes")]
    public List<string> Probes { get; set; } = [];
    [JsonPropertyName("checkIds")]
    public List<string> CheckIds { get; set; } = [];
    [JsonPropertyName("targetTreeEvidence")]
    public string? TargetTreeEvidence { get; set; }
    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = [];
    [JsonPropertyName("sourceEvidenceHashes")]
    public List<StateEvidenceHash> SourceEvidenceHashes { get; set; } = [];
    [JsonPropertyName("targetEvidenceHashes")]
    public List<StateEvidenceHash> TargetEvidenceHashes { get; set; } = [];
}

public static class StateClosureProtocol
{
    public static bool HasHistoricalParity(
        DirectoryInfo root,
        MigrationState state)
    {
        try
        {
            if (state.Disposition != "required" ||
                state.Source is not
                    { Status: "captured", Evidence: not null } ||
                state.Target is not
                    { Status: "passed", Evidence: not null } ||
                state.Comparison is not { Status: "verified" } ||
                state.Target.VerificationLevel !=
                    StateVerificationLevel.SurfaceParityVerified ||
                state.Target.Attestation != AttestationPath(state.Id))
            {
                return false;
            }
            var proof =
                Read<StateClosureAttestation>(
                    root,
                    state.Target.Attestation);
            return StateClosureSeal.Verify(proof) &&
                proof.Result == SurfaceVerificationResult.Verified &&
                proof.StateId == state.Id &&
                proof.RequiredContractHash ==
                    RequiredStateContracts.Require(
                        root,
                        state,
                        proof.RecordedAt) &&
                proof.SelectorBindingHash ==
                    SelectorBindingAmendments.StateHash(root, state) &&
                proof.StateContractFingerprint ==
                    ContractFingerprint(root, state) &&
                proof.RequiredActionIds.SequenceEqual(
                    state.Verification!.RequiredActions.Select(
                        action => action.Id)) &&
                proof.CheckIds.SequenceEqual(
                    SurfaceCheckIds.All,
                    StringComparer.Ordinal) &&
                proof.Evidence.SequenceEqual(
                    state.Target.Evidence,
                    StringComparer.Ordinal) &&
                proof.SourceEvidenceHashes.SequenceEqual(
                    state.Source.Evidence.Select(path =>
                        new StateEvidenceHash(
                            path,
                            FileHash(root, path)))) &&
                proof.TargetEvidenceHashes.SequenceEqual(
                    state.Target.Evidence.Select(path =>
                        new StateEvidenceHash(
                            path,
                            FileHash(root, path))));
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            IOException or
            UnauthorizedAccessException or
            JsonException or
            ArgumentException)
        {
            return false;
        }
    }

    public static string AttestationPath(string stateId) =>
        $"{EnforcementStore.EnforcementDirectory}/surfaces/{HashText(stateId)}/attestation.json";

    public static string FileHash(
        DirectoryInfo root,
        string relativePath)
    {
        using var stream = File.OpenRead(Resolve(root, relativePath));
        return Convert.ToHexString(SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    public static string ContractFingerprint(
        DirectoryInfo root,
        MigrationState state)
    {
        var contract = new MigrationState
        {
            Id = state.Id,
            Kind = state.Kind,
            Disposition = state.Disposition,
            ExclusionReason = state.ExclusionReason,
            SourceDefinitions = state.SourceDefinitions,
            Slice = state.Slice,
            Dependencies = state.Dependencies,
            Criticality = state.Criticality,
            FeaturePaths = state.FeaturePaths,
            Verification = state.Verification,
            Source = state.Source,
            Target = new StateObservation { Status = "not-run" },
            Comparison = new StateComparison { Status = "not-run" }
        };
        return HashText(
            JsonSerializer.Serialize(
                contract,
                EnforcementStore.JsonOptions) +
            "\n" +
            string.Join(
                "\n",
                state.Source.Evidence.Select(path =>
                    FileHash(root, path))));
    }

    public static StateClosureAttestation Derive(
        DirectoryInfo root,
        MigrationState state,
        ReplayAttestation replay,
        IReadOnlyList<string> probePaths,
        DateTimeOffset now,
        IWindowOwnerResolver? windowOwnerResolver = null)
    {
        using var verification = StateClosureSeal.BeginVerification();
        try
        {
            return DeriveCore(
                root,
                state,
                replay,
                probePaths,
                now,
                windowOwnerResolver ??
                    WindowsWindowOwnerResolver.Instance);
        }
        catch (Exception exception) when (
            exception is JsonException or
            IOException or
            UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"Surface closure evidence could not be verified: {exception.Message}",
                exception);
        }
    }

    internal static async Task<StateClosureAttestation> RecordAsync(
        DirectoryInfo root,
        MigrationState state,
        ReplayAttestation replay,
        IReadOnlyList<string> probePaths,
        DateTimeOffset now,
        IWindowOwnerResolver windowOwnerResolver,
        CancellationToken cancellationToken)
    {
        var path = AttestationPath(state.Id);
        var (existing, _) = await EnforcementStore.TryLoadAsync<StateClosureAttestation>(
            root, path, cancellationToken);
        if (existing?.Probes is not null &&
            existing.Probes.SequenceEqual(probePaths, StringComparer.Ordinal) &&
            VerifyRecordedLevel(root, state, replay, now, out _, out _, windowOwnerResolver))
        {
            return existing;
        }

        var attestation = Derive(root, state, replay, probePaths, now, windowOwnerResolver);
        StateClosureSeal.Sign(attestation);
        await EnforcementStore.WriteAtomicAsync(root, path, attestation, cancellationToken);
        var verified = attestation.Result == SurfaceVerificationResult.Verified;
        var reachable = attestation.CheckIds.Contains(SurfaceCheckIds.TargetReachable, StringComparer.Ordinal);
        state.Target.Status = reachable ? "passed" :
            attestation.Result == SurfaceVerificationResult.Unreachable ? "failed" : "unverified";
        state.Target.VerificationLevel = attestation.VerificationLevel;
        state.Target.Reason = verified ? null : string.Join(" ", attestation.Issues);
        state.Target.Attestation = path;
        state.Target.Evidence = attestation.Evidence;
        state.Target.TreeEvidence = attestation.TargetTreeEvidence;
        state.Comparison.Status = verified ? "verified" :
            attestation.Result == SurfaceVerificationResult.Mismatch ? "failed" : "unverified";
        state.Comparison.Reason = state.Target.Reason;
        return attestation;
    }

    private static StateClosureAttestation DeriveCore(
        DirectoryInfo root,
        MigrationState state,
        ReplayAttestation replay,
        IReadOnlyList<string> probePaths,
        DateTimeOffset now,
        IWindowOwnerResolver windowOwnerResolver)
    {
        if (state.Disposition != "required")
            throw new InvalidDataException(
                $"Excluded surface {state.Id} has no runtime closure.");
        if (!replay.Success ||
            replay.Producer != MigrationArtifactClass.Producer ||
            replay.ArtifactClass !=
                MigrationArtifactClass.CanonicalAttestation ||
            replay.ProcessStartedAt is not { } processStart ||
            processStart == DateTimeOffset.MinValue ||
            processStart > replay.CompletedAt)
        {
            throw new InvalidDataException(
                "A successful product-generated canonical replay is required.");
        }
        if (!CanonicalAttestationTrust.Verify(root, replay))
            throw new InvalidDataException(
                "The canonical replay is not authenticated by the local operation/attestation chain.");
        var requiredHash =
            RequiredStateContracts.Require(root, state, replay.StartedAt);
        RequiredStateContracts.ValidateRequirements(
            new StatePlanDocument { States = [state] });
        if (replay.RequiredContractHash != requiredHash)
            throw new InvalidDataException(
                "Canonical replay is not bound to the immutable surface contract.");
        if (state.Source.Status != "captured" ||
            string.IsNullOrWhiteSpace(state.Source.TreeEvidence) ||
            !state.Source.Evidence.Contains(
                state.Source.TreeEvidence,
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Surface {state.Id} requires captured source UIA tree evidence.");
        }
        if (probePaths.Count == 0 ||
            probePaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != probePaths.Count)
        {
            throw new InvalidDataException(
                "A nonempty ordered sequence of distinct canonical probe receipts is required.");
        }

        var fingerprint = ContractFingerprint(root, state);
        var progress =
            Read<WorkflowProgress>(root, EnforcementStore.ProgressPath);
        if (progress.SchemaVersion != WorkflowProgress.CurrentSchemaVersion ||
            progress.Operations is null ||
            progress.Operations.Any(operation => operation is null))
        {
            throw new InvalidDataException(
                "Probe receipts require workflow operation records.");
        }
        if (!WorkflowOperationAuthentication.VerifyAnchored(
                root,
                progress.Operations))
        {
            throw new InvalidDataException(
                "Operation history is incomplete, reordered, or not locally authenticated.");
        }

        var probes = new List<UiDevelopmentProbe>();
        var evidence = new List<string>();
        var previous = replay.CompletedAt;
        foreach (var path in probePaths)
        {
            var probe = Read<UiDevelopmentProbe>(root, path);
            if (string.IsNullOrWhiteSpace(probe.Id) ||
                probe.Id.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) &&
                    character is not '-' and not '_'))
            {
                throw new InvalidDataException(
                    "Invalid probe identity.");
            }
            var directory =
                $"{EnforcementStore.EnforcementDirectory}/probes/{probe.Id}";
            if (path != $"{directory}/probe.json" ||
                probe.ArtifactPath != path ||
                probe.StandardOutputPath !=
                    $"{directory}/stdout.log" ||
                probe.StandardErrorPath !=
                    $"{directory}/stderr.log")
            {
                throw new InvalidDataException(
                    "Only canonical receipts and raw outputs under enforcement probe storage are accepted.");
            }
            if (probe.SchemaVersion !=
                    UiDevelopmentProbe.CurrentSchemaVersion ||
                probe.Producer != MigrationArtifactClass.Producer ||
                probe.ArtifactClass !=
                    MigrationArtifactClass.DevelopmentProbe ||
                probe.TargetFingerprint != replay.TargetFingerprint ||
                probe.ReplayAttestationId != replay.Id)
            {
                throw new InvalidDataException(
                    "Probe producer, target fingerprint, or canonical replay does not match.");
            }
            if (!StateClosureSeal.Verify(probe))
                throw new InvalidDataException(
                    "Probe receipt integrity seal is missing or invalid.");
            if (probe.StateId != state.Id ||
                probe.StateContractFingerprint != fingerprint ||
                probe.RequiredContractHash != requiredHash)
            {
                throw new InvalidDataException(
                    $"Probe does not match the current planned surface identity/contract: {state.Id}.");
            }
            if (probe.SelectorBindingHash !=
                SelectorBindingAmendments.StateHash(root, state))
            {
                throw new InvalidDataException(
                    $"Probe selector binding does not match the current audited binding generation for surface {state.Id}.");
            }
            StateAttribution.Validate(
                new StatePlanDocument { States = [state] },
                new StateSliceBinding(
                    probe.StateId,
                    probe.SliceId));
            if (probe.ExitCode != 0 ||
                probe.InfrastructureFailure)
            {
                throw new InvalidDataException(
                    $"Probe {probe.Id} did not succeed.");
            }
            if (probe.StartedAt < previous ||
                probe.CompletedAt < probe.StartedAt ||
                probe.CompletedAt > now)
            {
                throw new InvalidDataException(
                    "Probe sequence is stale, out of order, or has invalid timestamps.");
            }
            if (probe.Command is not { Count: >= 3 } ||
                probe.Command[1] != "ui" ||
                probe.CommandSignature !=
                    DiagnosticGrouper.CreateSignature(
                        "ui-command",
                        [],
                        string.Join(
                            '\u001f',
                            probe.Command.Skip(1))))
            {
                throw new InvalidDataException(
                    "Probe command signature is invalid.");
            }
            if (probe.Command[2] == "screenshot")
            {
                throw new InvalidDataException(
                    "Screenshots are not part of UI Surface Parity closure.");
            }
            var uiArguments =
                probe.Command.Skip(2).ToArray();
            var window =
                ReplayWindowBinding.RequireWindow(uiArguments);
            if (probe.WindowHandle != window ||
                probe.WindowProcessId is not > 0 ||
                probe.WindowProcessStartedAt is null)
            {
                throw new InvalidDataException(
                    "Probe window/process binding does not match the canonical replay and command.");
            }
            ValidateProbeWindowBinding(
                probe,
                replay,
                windowOwnerResolver,
                window);
            var operations = progress.Operations
                .Where(operation =>
                    operation.Kind == WorkflowOperation.UiAction &&
                    !string.IsNullOrWhiteSpace(probe.OperationId) &&
                    operation.Id == probe.OperationId &&
                    StateClosureSeal.Verify(operation) &&
                    operation.ProbeId == probe.Id &&
                    operation.ProbeSha256 == FileHash(root, path) &&
                    operation.TargetFingerprint ==
                        replay.TargetFingerprint &&
                    operation.ReplayAttestationId == replay.Id &&
                    operation.Signature == probe.CommandSignature &&
                    operation.RecordedAt <= probe.StartedAt &&
                    operation.RequiredContractHash == requiredHash &&
                    operation.StateId == probe.StateId &&
                    operation.IntendedSliceId == probe.SliceId &&
                    operation.RecordedAt >= replay.CompletedAt)
                .ToArray();
            if (operations.Length != 1)
            {
                throw new InvalidDataException(
                    "Probe is fabricated, modified, or untracked by workflow operations.");
            }
            CheckFile(root, path, null, replay.StartedAt);
            CheckFile(
                root,
                probe.StandardOutputPath,
                probe.StandardOutputSha256,
                replay.StartedAt);
            CheckFile(
                root,
                probe.StandardErrorPath,
                probe.StandardErrorSha256,
                replay.StartedAt);
            RejectReportedFailure(root, probe.StandardOutputPath);
            evidence.Add(path);
            evidence.Add(probe.StandardOutputPath);
            evidence.Add(probe.StandardErrorPath);
            probes.Add(probe);
            previous = probe.CompletedAt;
        }

        var issues = new List<string>();
        var navigation =
            RequiredActionProtocol.Derive(
                root,
                state,
                probes,
                issues);
        var treeIndex = state.Verification!.TreeProbeIndex;
        string? targetTree = null;
        var targetTreeValid = false;
        if (treeIndex < probes.Count)
        {
            targetTree = probes[treeIndex].StandardOutputPath;
            targetTreeValid =
                SurfaceTreeComparison.IsTree(
                    root,
                    targetTree,
                    out var treeReason);
            if (!targetTreeValid)
                issues.Add(treeReason);
        }
        var reachable =
            targetTreeValid &&
            navigation.Count ==
                state.Verification.RequiredActions.Count &&
            issues.Count == 0;
        var comparison = targetTree is null
            ? new SurfaceTreeComparisonResult(
                false,
                false,
                "No target UIA tree was captured.")
            : SurfaceTreeComparison.Compare(
                root,
                state.Source.TreeEvidence!,
                targetTree);
        if (reachable && !comparison.Matches)
            issues.Add(comparison.Reason);

        var result = !targetTreeValid || !comparison.Comparable
            ? SurfaceVerificationResult.HumanValidationRequired
            : !reachable
                ? SurfaceVerificationResult.Unreachable
                : !comparison.Matches
                    ? SurfaceVerificationResult.Mismatch
                    : SurfaceVerificationResult.Verified;
        var checkIds = new List<string>();
        if (reachable)
            checkIds.Add(SurfaceCheckIds.TargetReachable);
        if (result == SurfaceVerificationResult.Verified)
            checkIds.Add(SurfaceCheckIds.StructureMatch);

        return new StateClosureAttestation
        {
            StateId = state.Id,
            StateContractFingerprint = fingerprint,
            RequiredContractHash = requiredHash,
            SelectorBindingHash =
                SelectorBindingAmendments.StateHash(root, state),
            VerificationLevel =
                result == SurfaceVerificationResult.Verified
                    ? StateVerificationLevel.SurfaceParityVerified
                    : reachable
                        ? StateVerificationLevel.SurfaceReachable
                        : StateVerificationLevel.EvidenceValid,
            Result = result,
            RequiredActionIds = state.Verification
                .RequiredActions
                .Select(action => action.Id)
                .ToList(),
            VerifiedSteps = navigation,
            Issues = issues,
            ComparisonDiagnostics = comparison.Diagnostics.Count == 0 ? null : comparison.Diagnostics,
            TargetFingerprint = replay.TargetFingerprint,
            ReplayAttestationId = replay.Id,
            RecordedAt = now,
            Probes = [.. probePaths],
            CheckIds = checkIds,
            TargetTreeEvidence = targetTree,
            Evidence = evidence,
            SourceEvidenceHashes = state.Source.Evidence
                .Select(path => new StateEvidenceHash(
                    path,
                    FileHash(root, path)))
                .ToList(),
            TargetEvidenceHashes = evidence
                .Select(path => new StateEvidenceHash(
                    path,
                    FileHash(root, path)))
                .ToList()
        };
    }

    private static void ValidateProbeWindowBinding(
        UiDevelopmentProbe probe,
        ReplayAttestation replay,
        IWindowOwnerResolver windowOwnerResolver,
        long window)
    {
        var replayIdentity = new ProcessIdentity(
            replay.ProcessId!.Value,
            replay.ProcessStartedAt!.Value);
        if (probe.WindowBindingKind is null or
            WindowBindingResolver.ReplayKind)
        {
            if (probe.WindowProcessId != replayIdentity.ProcessId ||
                probe.WindowProcessStartedAt !=
                    replayIdentity.StartedAt ||
                !windowOwnerResolver.TryGetOwnerIdentity(
                    window,
                    out var owner) ||
                owner != replayIdentity)
            {
                throw new InvalidDataException(
                    "Probe window/process binding does not match the canonical replay and command.");
            }
            return;
        }
        if (probe.WindowBindingKind !=
                WindowBindingResolver.PickerHostKind ||
            probe.WindowProcessId == replayIdentity.ProcessId ||
            probe.WindowProcessStartedAt <= DateTimeOffset.MinValue ||
            probe.WindowOwnerHandle is not > 0 ||
            probe.ReplayOwnerWindowHandle is not > 0 ||
            probe.WindowPresentAfter is null ||
            !WindowBindingResolver.IsTrustedPickerHost(
                probe.WindowProcessName,
                probe.WindowExecutablePath))
        {
            throw new InvalidDataException(
                "Delegated system-window evidence is missing a trusted PickerHost process and replay-owner binding.");
        }
        if (probe.WindowPresentAfter == true)
        {
            var expected = new WindowBindingEvidence(
                WindowBindingResolver.PickerHostKind,
                new ProcessIdentity(
                    probe.WindowProcessId.GetValueOrDefault(),
                    probe.WindowProcessStartedAt.GetValueOrDefault()),
                probe.WindowProcessName!,
                probe.WindowExecutablePath,
                probe.WindowOwnerHandle.Value,
                probe.ReplayOwnerWindowHandle.Value);
            if (!WindowBindingResolver.MatchesLiveBinding(
                    windowOwnerResolver,
                    window,
                    replayIdentity,
                    expected))
            {
                throw new InvalidDataException(
                    "The delegated PickerHost window no longer has its sealed process and replay-owner binding.");
            }
        }
    }

    public static bool Verify(
        DirectoryInfo root,
        MigrationState state,
        ReplayAttestation? replay,
        DateTimeOffset now,
        out string reason,
        IWindowOwnerResolver? windowOwnerResolver = null) =>
        VerifyEvidence(
            root,
            state,
            replay,
            now,
            out _,
            out reason,
            windowOwnerResolver);

    internal static bool VerifyEvidence(
        DirectoryInfo root,
        MigrationState state,
        ReplayAttestation? replay,
        DateTimeOffset now,
        [NotNullWhen(true)] out StateClosureAttestation? evidence,
        out string reason,
        IWindowOwnerResolver? windowOwnerResolver = null) =>
        VerifyAttestation(
            root,
            state,
            replay,
            now,
            out evidence,
            out reason,
            windowOwnerResolver,
            requireParity: true);

    internal static bool VerifyRecordedLevel(
        DirectoryInfo root,
        MigrationState state,
        ReplayAttestation? replay,
        DateTimeOffset now,
        [NotNullWhen(true)] out StateClosureAttestation? evidence,
        out string reason,
        IWindowOwnerResolver? windowOwnerResolver = null) =>
        VerifyAttestation(
            root,
            state,
            replay,
            now,
            out evidence,
            out reason,
            windowOwnerResolver,
            requireParity: false);

    public static StateVerificationSummary AssessLevel(
        DirectoryInfo root,
        MigrationState state,
        ReplayAttestation? replay,
        DateTimeOffset now,
        IWindowOwnerResolver? windowOwnerResolver = null)
    {
        if (state.Disposition == "excluded")
            return new StateVerificationSummary(
                state.Id,
                "excluded",
                state.ExclusionReason ?? "Excluded from UI surface parity.")
            {
                Result = "excluded"
            };
        return VerifyRecordedLevel(
            root,
            state,
            replay,
            now,
            out var evidence,
            out var reason,
            windowOwnerResolver)
            ? new StateVerificationSummary(
                state.Id,
                evidence.VerificationLevel,
                string.Join(" ", evidence.Issues))
            {
                Result = evidence.Result,
                ComparisonDiagnostics = evidence.ComparisonDiagnostics
            }
            : new StateVerificationSummary(
                state.Id,
                "unverified",
                reason);
    }

    private static bool VerifyAttestation(
        DirectoryInfo root,
        MigrationState state,
        ReplayAttestation? replay,
        DateTimeOffset now,
        [NotNullWhen(true)] out StateClosureAttestation? evidence,
        out string reason,
        IWindowOwnerResolver? windowOwnerResolver,
        bool requireParity)
    {
        evidence = null;
        using var verification = StateClosureSeal.BeginVerification();
        try
        {
            if (state.Disposition == "excluded")
                throw new InvalidDataException(
                    $"Surface {state.Id} is explicitly excluded.");
            if (state.Source is null ||
                state.Target is null ||
                state.Comparison is null ||
                state.Source.Evidence is null ||
                state.Target.Evidence is null)
            {
                throw new InvalidDataException(
                    $"Surface {state.Id} has invalid observation metadata.");
            }
            if (replay is null ||
                !replay.Success ||
                state.Target.Attestation !=
                    AttestationPath(state.Id))
            {
                throw new InvalidDataException(
                    $"Surface {state.Id} requires a product-generated surface closure attestation; use closure record-surface.");
            }
            var attestation =
                Read<StateClosureAttestation>(
                    root,
                    state.Target.Attestation);
            if (attestation.Probes is not { Count: > 0 } ||
                attestation.CheckIds is null ||
                attestation.Evidence is null ||
                attestation.SourceEvidenceHashes is null ||
                attestation.TargetEvidenceHashes is null)
            {
                throw new InvalidDataException(
                    "Surface closure attestation collections are invalid.");
            }
            if (!StateClosureSeal.Verify(attestation))
                throw new InvalidDataException(
                    "Surface closure attestation integrity seal is missing or invalid.");
            var derived = Derive(
                root,
                state,
                replay,
                attestation.Probes,
                now,
                windowOwnerResolver);
            if (attestation.SchemaVersion != "2.0" ||
                attestation.Producer !=
                    MigrationArtifactClass.Producer ||
                attestation.ArtifactClass !=
                    MigrationArtifactClass.CanonicalAttestation ||
                attestation.StateId != state.Id ||
                attestation.StateContractFingerprint !=
                    derived.StateContractFingerprint ||
                attestation.TargetFingerprint !=
                    replay.TargetFingerprint ||
                attestation.ReplayAttestationId != replay.Id ||
                attestation.RequiredContractHash !=
                    derived.RequiredContractHash ||
                attestation.VerificationLevel !=
                    derived.VerificationLevel ||
                attestation.Result != derived.Result ||
                !attestation.RequiredActionIds.SequenceEqual(
                    derived.RequiredActionIds) ||
                !attestation.VerifiedSteps.SequenceEqual(
                    derived.VerifiedSteps) ||
                !attestation.Issues.SequenceEqual(derived.Issues) ||
                !(attestation.ComparisonDiagnostics ?? []).SequenceEqual(derived.ComparisonDiagnostics ?? []) ||
                attestation.RecordedAt > now ||
                attestation.RecordedAt <
                    Read<UiDevelopmentProbe>(
                        root,
                        attestation.Probes[^1]).CompletedAt ||
                !attestation.CheckIds.SequenceEqual(
                    derived.CheckIds,
                    StringComparer.Ordinal) ||
                attestation.TargetTreeEvidence !=
                    derived.TargetTreeEvidence ||
                !attestation.Evidence.SequenceEqual(
                    derived.Evidence,
                    StringComparer.Ordinal) ||
                !attestation.SourceEvidenceHashes.SequenceEqual(
                    derived.SourceEvidenceHashes) ||
                !attestation.TargetEvidenceHashes.SequenceEqual(
                    derived.TargetEvidenceHashes) ||
                !state.Target.Evidence.SequenceEqual(
                    derived.Evidence,
                    StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Surface {state.Id} closure attestation is stale or modified.");
            }
            var verified =
                derived.Result == SurfaceVerificationResult.Verified;
            var reachable = derived.CheckIds.Contains(
                SurfaceCheckIds.TargetReachable,
                StringComparer.Ordinal);
            var expectedTarget =
                reachable ? "passed" :
                derived.Result == SurfaceVerificationResult.Unreachable
                    ? "failed"
                    : "unverified";
            var expectedComparison =
                verified ? "verified" :
                derived.Result == SurfaceVerificationResult.Mismatch
                    ? "failed"
                    : "unverified";
            if (state.Target.VerificationLevel !=
                    derived.VerificationLevel ||
                state.Target.Status != expectedTarget ||
                state.Target.TreeEvidence !=
                    derived.TargetTreeEvidence ||
                state.Comparison.Status != expectedComparison)
            {
                throw new InvalidDataException(
                    "Surface observation status does not match product derivation.");
            }
            if (requireParity && !verified)
                throw new InvalidDataException(
                    $"Surface {state.Id} result is {derived.Result}; UI surface parity is not verified.");
            reason =
                $"Surface {state.Id} has a re-derived machine-verifiable closure attestation.";
            evidence = derived;
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            IOException or
            UnauthorizedAccessException or
            JsonException or
            ArgumentException)
        {
            reason = exception.Message;
            return false;
        }
    }

    public static bool FindingSatisfied(
        DirectoryInfo root,
        SemanticFinding finding,
        StatePlanDocument? plan,
        ReplayAttestation? replay,
        DateTimeOffset now,
        IWindowOwnerResolver? windowOwnerResolver = null)
    {
        if (plan is null ||
            finding.StateIds is not { Count: > 0 } ||
            finding.CompletionAssertions is not { Count: > 0 } ||
            finding.CompletionAssertions.Any(item =>
                item is null ||
                !finding.StateIds.Contains(
                    item.StateId,
                    StringComparer.Ordinal)) ||
            finding.StateIds.Any(id =>
                !finding.CompletionAssertions.Any(item =>
                    item.StateId == id)))
        {
            return false;
        }
        return finding.StateIds.All(id =>
            FindingStateSatisfied(
                root,
                finding,
                plan,
                replay,
                now,
                id,
                windowOwnerResolver));
    }

    internal static bool FindingStateSatisfied(
        DirectoryInfo root,
        SemanticFinding finding,
        StatePlanDocument? plan,
        ReplayAttestation? replay,
        DateTimeOffset now,
        string stateId,
        IWindowOwnerResolver? windowOwnerResolver = null)
    {
        if (plan is null ||
            finding.StateIds is not { Count: > 0 } ||
            !finding.StateIds.Contains(
                stateId,
                StringComparer.Ordinal) ||
            finding.CompletionAssertions is not { Count: > 0 })
        {
            return false;
        }
        var checks = finding.CompletionAssertions
            .Where(item =>
                item is not null &&
                item.StateId == stateId)
            .ToArray();
        if (checks.Length == 0)
            return false;
        var state =
            plan.States.SingleOrDefault(
                candidate => candidate.Id == stateId);
        if (state is null ||
            !VerifyRecordedLevel(
                root,
                state,
                replay,
                now,
                out var proof,
                out _,
                windowOwnerResolver))
        {
            return false;
        }
        return checks.All(item =>
            proof.CheckIds.Contains(
                item.AssertionId,
                StringComparer.Ordinal));
    }

    private static void CheckFile(
        DirectoryInfo root,
        string path,
        string? hash,
        DateTimeOffset after)
    {
        var fullPath = Resolve(root, path);
        if (!File.Exists(fullPath) ||
            File.GetLastWriteTimeUtc(fullPath) < after.UtcDateTime ||
            hash is not null && hash != FileHash(root, path) ||
            hash is null &&
                !path.EndsWith(
                    "/probe.json",
                    StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Probe output is missing, stale, or modified: {path}.");
        }
    }

    private static void RejectReportedFailure(
        DirectoryInfo root,
        string path)
    {
        try
        {
            using var document =
                JsonDocument.Parse(
                    File.ReadAllText(Resolve(root, path)),
                    new JsonDocumentOptions
                    {
                        AllowDuplicateProperties = false
                    });
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object)
                return;
            foreach (var name in new[] { "success", "Success" })
            {
                if (value.TryGetProperty(name, out var success) &&
                    success.ValueKind == JsonValueKind.False)
                {
                    throw new InvalidDataException(
                        "Probe structured output reports a failure.");
                }
            }
            foreach (var name in new[] { "error", "Error" })
            {
                if (value.TryGetProperty(name, out var error) &&
                    error.ValueKind is not
                        (JsonValueKind.Null or JsonValueKind.False) &&
                    (error.ValueKind != JsonValueKind.String ||
                     !string.IsNullOrEmpty(error.GetString())))
                {
                    throw new InvalidDataException(
                        "Probe structured output reports a failure.");
                }
            }
        }
        catch (JsonException)
        {
            // Navigation action output can be unstructured; the final inspect
            // tree is validated separately.
        }
    }

    private static T Read<T>(
        DirectoryInfo root,
        string path)
        where T : class =>
        JsonSerializer.Deserialize<T>(
            File.ReadAllText(Resolve(root, path)),
            EnforcementStore.JsonOptions) ??
        throw new InvalidDataException(
            $"Missing or null closure artifact: {path}.");

    private static string Resolve(
        DirectoryInfo root,
        string path) =>
        EnforcementStore.TryResolveContainedPath(
            root,
            path,
            out var fullPath,
            out var reason)
            ? fullPath
            : throw new InvalidDataException(reason);

    private static string HashText(string text) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();
}
