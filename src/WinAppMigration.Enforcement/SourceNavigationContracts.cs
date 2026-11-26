// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinAppMigration.Enforcement;

public sealed class SourceNavigationContract
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0";
    [JsonPropertyName("requiredContractHash")]
    public required string RequiredContractHash { get; set; }
    [JsonPropertyName("stateId")]
    public required string StateId { get; set; }
    [JsonPropertyName("navigation")]
    public required StateVerificationContract Navigation { get; set; }
    [JsonPropertyName("source")]
    public required StateObservation Source { get; set; }
    [JsonPropertyName("sourceRunId")]
    public required string SourceRunId { get; set; }
    [JsonPropertyName("establishedAt")]
    public DateTimeOffset EstablishedAt { get; set; }
    [JsonPropertyName("evidenceHashes")]
    public List<StateEvidenceHash> EvidenceHashes { get; set; } = [];
    [JsonPropertyName("seal")]
    public string? Seal { get; set; }
    [JsonPropertyName("sealPublicKey")]
    public string? SealPublicKey { get; set; }
}

public static class SourceNavigationContracts
{
    public static string ContractPath(string stateId) =>
        $"{EnforcementStore.EnforcementDirectory}/navigation/{Hash(stateId)}/contract.json";

    internal static async Task<SourceNavigationContract> EstablishAsync(
        DirectoryInfo root, MigrationState state, string sourceRunId,
        IReadOnlyList<string> evidence, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var requiredHash = RequiredStateContracts.Require(root, state, now);
        if (!RequiredStateContracts.UsesSourceNavigation(root))
            throw new InvalidDataException("A legacy navigation contract cannot be replaced; use a fresh workspace.");
        ValidateNavigation(state);
        var (existing, error) = await EnforcementStore.TryLoadAsync<SourceNavigationContract>(
            root, ContractPath(state.Id), cancellationToken);
        if (error is not null)
            throw new InvalidDataException(error);
        if (existing is not null)
            return Require(root, state)!;
        if (StateClosureSeal.ReadContractAnchor(AnchorName(root, state.Id)) is not null)
            throw new InvalidDataException("The anchored source-navigation contract is missing; deleting it does not reset navigation.");
        var document = new SourceNavigationContract
        {
            RequiredContractHash = requiredHash,
            StateId = state.Id,
            Navigation = state.Verification!,
            Source = state.Source,
            SourceRunId = sourceRunId,
            EstablishedAt = now,
            EvidenceHashes = evidence.Distinct(StringComparer.Ordinal).Select(path =>
                new StateEvidenceHash(path, StateClosureProtocol.FileHash(root, path))).ToList()
        };
        var signed = StateClosureSeal.SignDocument("source-navigation", Serialize(document));
        document.Seal = signed.Seal;
        document.SealPublicKey = signed.PublicKey;
        await EnforcementStore.WriteAtomicAsync(root, ContractPath(state.Id), document, cancellationToken);
        StateClosureSeal.WriteContractAnchor(AnchorName(root, state.Id), Hash(Serialize(document)));
        return document;
    }

    internal static SourceNavigationContract? Require(
        DirectoryInfo root, MigrationState state, DateTimeOffset? before = null)
    {
        var requiredHash = RequiredStateContracts.Require(root, state);
        if (!RequiredStateContracts.UsesSourceNavigation(root))
            return null;
        var (document, error) = EnforcementStore.TryLoadAsync<SourceNavigationContract>(
            root, ContractPath(state.Id)).GetAwaiter().GetResult();
        if (document is null)
        {
            if (error is not null || StateClosureSeal.ReadContractAnchor(AnchorName(root, state.Id)) is not null)
                throw new InvalidDataException(error ??
                    "The anchored source-navigation contract is missing; deleting it does not reset navigation.");
            ValidateNavigation(state, requireSource: false);
            return null;
        }
        ValidateNavigation(state);
        var seal = document.Seal;
        var key = document.SealPublicKey;
        var hash = Hash(Serialize(document));
        bool signature;
        try
        {
            document.Seal = null;
            document.SealPublicKey = null;
            signature = StateClosureSeal.VerifyDocument("source-navigation", Serialize(document), seal, key);
        }
        finally
        {
            document.Seal = seal;
            document.SealPublicKey = key;
        }
        if (!signature || document.SchemaVersion != "1.0" ||
            document.RequiredContractHash != requiredHash || document.StateId != state.Id ||
            document.EstablishedAt == default || document.EstablishedAt > (before ?? DateTimeOffset.UtcNow) ||
            Serialize(document.Navigation) != Serialize(state.Verification) ||
            Serialize(document.Source) != Serialize(state.Source) ||
            StateClosureSeal.ReadContractAnchor(AnchorName(root, state.Id)) != hash ||
            document.EvidenceHashes.Any(item => StateClosureProtocol.FileHash(root, item.Path) != item.Sha256))
            throw new InvalidDataException($"Surface {state.Id} source navigation, baseline, or evidence changed after source verification.");
        return document;
    }

    internal static void ValidateNavigation(MigrationState state, bool requireSource = true)
    {
        var navigation = state.Verification ??
            throw new InvalidDataException($"Surface {state.Id} has no declared navigation recipe.");
        var count = navigation.RequiredActions.Count;
        var firstAction = count == 0 ? 0 : 1;
        if (navigation.StartSurfaceId == state.Id ||
            navigation.StartSurfaceId is not null && string.IsNullOrWhiteSpace(navigation.StartSurfaceId) ||
            navigation.TreeProbeIndex != (count == 0 ? 0 : count + 1) ||
            !navigation.RequiredActions.Select(action => action.ProbeIndex)
                .SequenceEqual(Enumerable.Range(firstAction, count)))
            throw new InvalidDataException(
                "Navigation requires an initial inspect, contiguous actions at indices 1..N, and a final inspect at N+1; a zero-action surface uses inspect at index 0.");
        if (requireSource && (state.Source.Status != "captured" || state.Source.TreeEvidence is null ||
            !state.Source.Evidence.Contains(state.Source.TreeEvidence, StringComparer.Ordinal)))
            throw new InvalidDataException($"Surface {state.Id} needs its captured source tree.");
    }

    internal static bool IsReadOnly(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && arguments[0] is "inspect" or "list-windows" or "screenshot" or "get-property";

    internal static void ValidateTargetStart(
        DirectoryInfo root, MigrationState state, ReplayAttestation replay,
        UiDevelopmentProbe initial, WorkflowProgress progress, DateTimeOffset now,
        IWindowOwnerResolver windowOwnerResolver)
    {
        Require(root, state, initial.StartedAt);
        if (!RequiredStateContracts.UsesSourceNavigation(root))
            return;
        if (initial.Command.ElementAtOrDefault(2) != "inspect" ||
            !SurfaceTreeComparison.IsTree(root, initial.StandardOutputPath, out _, requireComplete: false))
            throw new InvalidDataException("Navigation must begin with a supported current target inspect.");
        var startId = state.Verification!.StartSurfaceId;
        if (startId is null)
        {
            foreach (var operation in progress.Operations.Where(operation =>
                         operation.Kind == WorkflowOperation.UiAction &&
                         operation.ReplayAttestationId == replay.Id &&
                         operation.RecordedAt < initial.StartedAt && operation.ProbeId != initial.Id))
            {
                var previous = Read<UiDevelopmentProbe>(root,
                    $"{EnforcementStore.EnforcementDirectory}/probes/{operation.ProbeId}/probe.json");
                if (!IsReadOnly(previous.Command.Skip(2).ToArray()))
                    throw new InvalidDataException(
                        "This route starts at a fresh launch, but the canonical process has already been navigated. Declare a verified startSurfaceId or request a fresh replay.");
            }
            return;
        }
        var start = RequiredStateContracts.Read(root).States.SingleOrDefault(item => item.Id == startId) ??
            throw new InvalidDataException($"Unknown navigation start surface: {startId}.");
        var proof = Read<StateClosureAttestation>(root, StateClosureProtocol.AttestationPath(startId));
        var (contract, contractError) = EnforcementStore.TryLoadAsync<SourceNavigationContract>(
            root, ContractPath(startId)).GetAwaiter().GetResult();
        if (contractError is not null)
            throw new InvalidDataException(contractError);
        start.Verification = proof.Navigation ?? contract?.Navigation ?? start.Verification;
        start.Source = proof.Source ?? contract?.Source ?? start.Source;
        start.Target = new StateObservation
        {
            Status = proof.CheckIds.Contains(SurfaceCheckIds.TargetReachable) ? "passed" :
                proof.Result == SurfaceVerificationResult.Unreachable ? "failed" : "unverified",
            VerificationLevel = proof.VerificationLevel,
            Attestation = StateClosureProtocol.AttestationPath(startId),
            TreeEvidence = proof.TargetTreeEvidence,
            Evidence = proof.Evidence
        };
        start.Comparison.Status = proof.Result == SurfaceVerificationResult.Verified ? "verified" :
            proof.Result == SurfaceVerificationResult.Mismatch ? "failed" : "unverified";
        if (!StateClosureProtocol.VerifyRecordedLevel(root, start, replay, now, out var verified, out var reason, windowOwnerResolver) ||
            !verified.CheckIds.Contains(SurfaceCheckIds.TargetReachable) ||
            proof.RecordedAt > initial.StartedAt ||
            !SurfaceTreeComparison.Compare(root, verified.TargetTreeEvidence!, initial.StandardOutputPath,
                requireComplete: false).Matches)
            throw new InvalidDataException(
                $"Navigation start surface {startId} is not current and reachable, or its target UI state changed. Recover that state before retrying. {reason}");
    }

    internal static void ValidateTargetTrace(
        DirectoryInfo root, MigrationState state, ReplayAttestation replay,
        IReadOnlyList<UiDevelopmentProbe> probes, WorkflowProgress progress,
        DateTimeOffset now, IWindowOwnerResolver windowOwnerResolver,
        bool startAlreadyAuthenticated = false)
    {
        Require(root, state);
        if (!RequiredStateContracts.UsesSourceNavigation(root))
            return;
        if (probes.Count != state.Verification!.TreeProbeIndex + 1)
            throw new InvalidDataException("The complete source-verified navigation trace is required.");
        // A committed surface proof authenticates the start check performed during collection.
        // Rechecking it against a mutable, later parent proof would invalidate unrelated routes.
        if (!startAlreadyAuthenticated)
            ValidateTargetStart(root, state, replay, probes[0], progress, now, windowOwnerResolver);
        var expected = probes.Where(probe => !IsReadOnly(probe.Command.Skip(2).ToArray())).Select(probe => probe.Id);
        var actual = progress.Operations.Where(operation =>
                operation.Kind == WorkflowOperation.UiAction && operation.ReplayAttestationId == replay.Id &&
                operation.RecordedAt >= probes[0].StartedAt && operation.RecordedAt <= probes[^1].CompletedAt)
            .Select(operation => Read<UiDevelopmentProbe>(root,
                $"{EnforcementStore.EnforcementDirectory}/probes/{operation.ProbeId}/probe.json"))
            .Where(probe => !IsReadOnly(probe.Command.Skip(2).ToArray())).Select(probe => probe.Id);
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("Navigation includes an omitted, failed, or interleaved target action; recover the declared start state and capture a fresh complete trace.");
    }

    private static T Read<T>(DirectoryInfo root, string path) where T : class
    {
        var (value, error) = EnforcementStore.TryLoadAsync<T>(root, path).GetAwaiter().GetResult();
        return value ?? throw new InvalidDataException(error ?? $"Required navigation evidence is missing: {path}.");
    }

    private static string AnchorName(DirectoryInfo root, string stateId) =>
        "navigation-root-" + Hash(System.IO.Path.GetFullPath(root.FullName).ToUpperInvariant() + "\n" + stateId);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, EnforcementStore.JsonOptions);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
