// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinAppMigration.Enforcement;

public sealed class RequiredStateContractDocument
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "2.0";
    [JsonPropertyName("seal")]
    public string? Seal { get; set; }
    [JsonPropertyName("sealPublicKey")]
    public string? SealPublicKey { get; set; }
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("establishedAt")]
    public DateTimeOffset EstablishedAt { get; set; }
    [JsonPropertyName("baselineHash")]
    public string BaselineHash { get; set; } = string.Empty;
    [JsonPropertyName("requiredContractHash")]
    public string RequiredContractHash { get; set; } = string.Empty;
    [JsonPropertyName("surfaces")]
    public List<MigrationState> States { get; set; } = [];
}

public static class RequiredStateContracts
{
    public const string Path =
        ".migration-evidence/enforcement/required-contract.json";

    public static async Task<RequiredStateContractDocument> EstablishAsync(
        DirectoryInfo root,
        StatePlanDocument plan,
        bool extend = false,
        CancellationToken cancellationToken = default)
    {
        ValidateRequirements(plan);
        var (progress, progressError) =
            await EnforcementStore.TryLoadAsync<WorkflowProgress>(
                root,
                EnforcementStore.ProgressPath,
                cancellationToken);
        if (progressError is not null)
            throw new InvalidDataException(progressError);
        if (plan.States.Any(surface =>
                surface.Disposition == "required" &&
                (surface.Source.Status != "not-run" ||
                 surface.Source.Evidence.Count != 0 ||
                 surface.Source.TreeEvidence is not null)) ||
            progress?.OperationsStartedAt is not null ||
            progress?.Operations is { Count: > 0 } ||
            !WorkflowOperationAuthentication.VerifyAnchored(
                root, progress?.Operations ?? []) ||
            Exists(root, EnforcementStore.BuildAttestationPath) ||
            Directory.Exists(System.IO.Path.Combine(
                root.FullName,
                ".migration-evidence",
                "source",
                "gateway")) ||
            SelectorBindingAmendments.HasHistory(root))
        {
            throw new InvalidDataException(
                "Establish or extend the surface contract before source capture or workflow operations.");
        }

        var (existing, error) =
            await EnforcementStore
                .TryLoadAsync<RequiredStateContractDocument>(
                    root,
                    Path,
                    cancellationToken);
        if (error is not null)
            throw new InvalidDataException(error);
        var association =
            StateClosureSeal.ReadContractAnchor(RootAnchor(root));
        if (existing is null && association is not null)
        {
            throw new InvalidDataException(
                "The independently anchored surface contract is missing; deleting its JSON does not reset requirements.");
        }

        var document = Create(plan);
        if (existing is not null)
        {
            VerifyDocument(root, existing);
            if (!extend ||
                Frozen(existing) ||
                !IsExtension(existing.States, document.States))
            {
                throw new InvalidDataException(
                    "Required surface inventory and navigation recipes cannot be removed or changed. Only append-only surface additions before workflow/source start are supported.");
            }
            document.Id = existing.Id;
            document.BaselineHash = existing.BaselineHash;
        }

        StateClosureSeal.Sign(document);
        await EnforcementStore.WriteAtomicAsync(
            root,
            Path,
            document,
            cancellationToken);
        Anchor(root, document, frozen: false);
        return document;
    }

    internal static RequiredStateContractDocument Create(
        StatePlanDocument plan)
    {
        ValidateRequirements(plan);
        var document = new RequiredStateContractDocument
        {
            EstablishedAt = DateTimeOffset.UtcNow,
            States = plan.States.Select(Project).ToList()
        };
        document.RequiredContractHash = Hash(document.States);
        document.BaselineHash = document.RequiredContractHash;
        return document;
    }

    internal static void Anchor(
        DirectoryInfo root,
        RequiredStateContractDocument document,
        bool frozen)
    {
        StateClosureSeal.WriteContractAnchor(
            "rc-doc-" + document.Id,
            document.RequiredContractHash +
            (frozen ? "|frozen" : "|open"));
        StateClosureSeal.WriteContractAnchor(
            RootAnchor(root),
            document.Id);
    }

    public static string Require(
        DirectoryInfo root,
        MigrationState state,
        DateTimeOffset? before = null)
    {
        var (document, error) =
            EnforcementStore
                .TryLoadAsync<RequiredStateContractDocument>(
                    root,
                    Path)
                .GetAwaiter()
                .GetResult();
        if (document is null)
        {
            throw new InvalidDataException(
                error ??
                "A pre-source immutable surface contract is missing; run closure seal-contract before starting a fresh source/workflow.");
        }
        VerifyDocument(root, document);
        if (before is { } time && document.EstablishedAt > time)
        {
            throw new InvalidDataException(
                "The surface contract was established after the target replay/evidence and cannot authorize it retroactively.");
        }
        var required =
            document.States.SingleOrDefault(item => item.Id == state.Id);
        if (required is null ||
            Serialize(required) != Serialize(Project(state)))
        {
            throw new InvalidDataException(
                $"Surface {state.Id} changed its immutable inventory or navigation contract.");
        }
        return document.RequiredContractHash;
    }

    public static string Freeze(
        DirectoryInfo root,
        StatePlanDocument plan)
    {
        var (document, error) =
            EnforcementStore
                .TryLoadAsync<RequiredStateContractDocument>(
                    root,
                    Path)
                .GetAwaiter()
                .GetResult();
        if (document is null)
            throw new InvalidDataException(
                error ?? "No immutable surface contract has been established.");
        VerifyDocument(root, document);
        if (plan.SchemaVersion != StatePlanDocument.CurrentSchemaVersion ||
            Hash(plan.States.Select(Project).ToList()) !=
            document.RequiredContractHash)
        {
            throw new InvalidDataException(
                "The surface inventory or navigation recipes changed after contract establishment.");
        }
        if (!Frozen(document))
            Anchor(root, document, frozen: true);
        return document.RequiredContractHash;
    }

    internal static MigrationState Project(MigrationState state) =>
        new()
        {
            Id = state.Id,
            Kind = state.Kind,
            Disposition = state.Disposition,
            ExclusionReason = state.ExclusionReason,
            SourceDefinitions = [.. state.SourceDefinitions],
            Criticality = state.Criticality,
            Verification = state.Verification is null
                ? null
                : JsonSerializer.Deserialize<StateVerificationContract>(
                    Serialize(state.Verification),
                    EnforcementStore.JsonOptions),
            Source = new StateObservation
            {
                Status = state.Disposition == "excluded"
                    ? "excluded"
                    : "not-run"
            },
            Target = new StateObservation
            {
                Status = state.Disposition == "excluded"
                    ? "excluded"
                    : "not-run"
            },
            Comparison = new StateComparison
            {
                Status = state.Disposition == "excluded"
                    ? "excluded"
                    : "not-run"
            }
        };

    private static void VerifyDocument(
        DirectoryInfo root,
        RequiredStateContractDocument document)
    {
        if (document.SchemaVersion != "2.0" ||
            document.Id.Length != 32 ||
            document.EstablishedAt == default ||
            document.EstablishedAt > DateTimeOffset.UtcNow ||
            document.Id.Any(character => !char.IsAsciiHexDigit(character)) ||
            !StateClosureSeal.Verify(document) ||
            Hash(document.States) != document.RequiredContractHash)
        {
            throw new InvalidDataException(
                "Required surface contract signature/hash is not locally trusted.");
        }
        var association =
            StateClosureSeal.ReadContractAnchor(RootAnchor(root));
        if (association != document.Id)
        {
            throw new InvalidDataException(
                "The required surface contract is not independently established for this workspace.");
        }
        var anchor =
            StateClosureSeal.ReadContractAnchor("rc-doc-" + document.Id);
        if (anchor != document.RequiredContractHash + "|open" &&
            anchor != document.RequiredContractHash + "|frozen")
        {
            throw new InvalidDataException(
                "Required surface contract rollback/replacement does not match the independent CNG anchor.");
        }
    }

    private static bool Frozen(
        RequiredStateContractDocument document) =>
        StateClosureSeal.ReadContractAnchor(
            "rc-doc-" + document.Id) ==
        document.RequiredContractHash + "|frozen";

    private static bool IsExtension(
        List<MigrationState> old,
        List<MigrationState> current)
    {
        if (current.Count < old.Count)
            return false;
        return old
            .Select(Serialize)
            .SequenceEqual(
                current.Take(old.Count).Select(Serialize),
                StringComparer.Ordinal);
    }

    internal static void ValidateRequirements(StatePlanDocument plan)
    {
        if (plan.SchemaVersion != StatePlanDocument.CurrentSchemaVersion ||
            plan.States is not { Count: > 0 } ||
            plan.States.Any(surface =>
                surface is null ||
                string.IsNullOrWhiteSpace(surface.Id)) ||
            plan.States
                .Select(surface => surface.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() != plan.States.Count)
        {
            throw new InvalidDataException(
                "A schema 3.0 surface plan with unique inventoried surfaces is required.");
        }

        var definitionOwners = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var surface in plan.States)
        {
            if (surface.SourceDefinitions is null ||
                surface.Source is null ||
                surface.Target is null ||
                surface.Comparison is null ||
                surface.Kind is not
                    ("window" or "page" or "dialog" or
                     "flyout" or "user-control") ||
                surface.Disposition is not ("required" or "excluded") ||
                surface.SourceDefinitions is not { Count: > 0 } ||
                surface.SourceDefinitions.Any(string.IsNullOrWhiteSpace) ||
                surface.SourceDefinitions
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() != surface.SourceDefinitions.Count ||
                surface.SourceDefinitions.Any(path =>
                    !definitionOwners.Add(path)))
            {
                throw new InvalidDataException(
                    $"Surface {surface.Id} requires a supported kind, a required/excluded disposition, and unique source definition paths.");
            }

            if (surface.Disposition == "excluded")
            {
                if (string.IsNullOrWhiteSpace(surface.ExclusionReason) ||
                    surface.Verification is not null ||
                    surface.Source.Status != "excluded" ||
                    surface.Target.Status != "excluded" ||
                    surface.Comparison.Status != "excluded")
                {
                    throw new InvalidDataException(
                        $"Excluded surface {surface.Id} requires a reason and cannot define runtime verification.");
                }
                continue;
            }

            var navigation = surface.Verification;
            if (surface.Criticality != StateCriticality.Required ||
                navigation is null ||
                navigation.TreeProbeIndex < 0 ||
                navigation.RequiredActions is null ||
                navigation.RequiredActions.Any(action =>
                    action is null ||
                    string.IsNullOrWhiteSpace(action.Id) ||
                    action.ProbeIndex < 0 ||
                    action.ProbeIndex >= navigation.TreeProbeIndex ||
                    action.Command is not { Count: > 0 } ||
                    action.Command.Any(string.IsNullOrWhiteSpace)) ||
                navigation.RequiredActions
                    .Select(action => action.Id)
                    .Distinct(StringComparer.Ordinal)
                    .Count() != navigation.RequiredActions.Count ||
                navigation.RequiredActions
                    .Select(action => action.ProbeIndex)
                    .Distinct()
                    .Count() != navigation.RequiredActions.Count ||
                !navigation.RequiredActions
                    .Select(action => action.ProbeIndex)
                    .SequenceEqual(
                        navigation.RequiredActions
                            .Select(action => action.ProbeIndex)
                            .Order()))
            {
                throw new InvalidDataException(
                    $"Required surface {surface.Id} needs an ordered navigation recipe and one later inspect tree probe.");
            }

            if (surface.Source.Status == "captured" &&
                (string.IsNullOrWhiteSpace(surface.Source.TreeEvidence) ||
                 !surface.Source.Evidence.Contains(
                     surface.Source.TreeEvidence,
                     StringComparer.Ordinal)) ||
                surface.Target.Status == "passed" &&
                (string.IsNullOrWhiteSpace(surface.Target.TreeEvidence) ||
                 !surface.Target.Evidence.Contains(
                     surface.Target.TreeEvidence,
                     StringComparer.Ordinal)))
            {
                throw new InvalidDataException(
                    $"Surface {surface.Id} captured observations must identify their complete UIA tree evidence.");
            }
        }
    }

    private static bool Exists(
        DirectoryInfo root,
        string path) =>
        EnforcementStore.TryResolveContainedPath(
            root,
            path,
            out var full,
            out _) &&
        File.Exists(full);

    private static string RootAnchor(DirectoryInfo root) =>
        "rc-root-" +
        HashText(
            System.IO.Path
                .GetFullPath(root.FullName)
                .ToUpperInvariant())[..32];

    private static string Hash(List<MigrationState> states) =>
        HashText(Serialize(states));

    private static string HashText(string value) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, EnforcementStore.JsonOptions);
}
