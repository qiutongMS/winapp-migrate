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
    public string SchemaVersion { get; set; } = "1.0";
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
    [JsonPropertyName("states")]
    public List<MigrationState> States { get; set; } = [];
}

public static class RequiredStateContracts
{
    public const string Path = ".migration-evidence/enforcement/required-contract.json";

    public static async Task<RequiredStateContractDocument> EstablishAsync(
        DirectoryInfo root, StatePlanDocument plan, bool extend = false, CancellationToken cancellationToken = default)
    {
        ValidateRequirements(plan);
        if (plan.States.Any(state => state.Source.Status != "not-run" || state.Source.Evidence.Count != 0 ||
                state.Assertions.Any(assertion => Exists(root, assertion.SourceEvidence))) ||
            Exists(root, EnforcementStore.ProgressPath) || Exists(root, EnforcementStore.BuildAttestationPath) ||
            Directory.Exists(System.IO.Path.Combine(root.FullName, ".migration-evidence", "source", "gateway")))
            throw new InvalidDataException("Establish/extend requirements before source capture and the first workflow assessment. Captured legacy plans cannot be retroactively blessed.");
        var (existing, error) = await EnforcementStore.TryLoadAsync<RequiredStateContractDocument>(root, Path, cancellationToken);
        if (error is not null) throw new InvalidDataException(error);
        var association = StateClosureSeal.ReadContractAnchor(RootAnchor(root));
        if (existing is null && association is not null)
            throw new InvalidDataException("The independently anchored contract is missing; deleting its JSON does not reset requirements.");
        var document = Create(plan);
        if (existing is not null)
        {
            VerifyDocument(root, existing);
            if (!extend || Frozen(existing) || !IsExtension(existing.States, document.States))
                throw new InvalidDataException("Required actions/assertions/order cannot be removed or changed. Only explicit append-only extensions before workflow/source start are supported.");
            document.Id = existing.Id;
            document.BaselineHash = existing.BaselineHash;
        }
        StateClosureSeal.Sign(document);
        await EnforcementStore.WriteAtomicAsync(root, Path, document, cancellationToken);
        Anchor(root, document, frozen: false);
        return document;
    }

    internal static RequiredStateContractDocument Create(StatePlanDocument plan)
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

    internal static void Anchor(DirectoryInfo root, RequiredStateContractDocument document, bool frozen)
    {
        StateClosureSeal.WriteContractAnchor("rc-doc-" + document.Id, document.RequiredContractHash + (frozen ? "|frozen" : "|open"));
        StateClosureSeal.WriteContractAnchor(RootAnchor(root), document.Id);
    }

    public static string Require(DirectoryInfo root, MigrationState state, DateTimeOffset? before = null)
    {
        var (document, error) = EnforcementStore.TryLoadAsync<RequiredStateContractDocument>(root, Path).GetAwaiter().GetResult();
        if (document is null) throw new InvalidDataException(error ?? "A pre-source immutable required contract is missing; run closure seal-contract before starting a fresh source/workflow.");
        VerifyDocument(root, document);
        if (before is { } time && document.EstablishedAt > time)
            throw new InvalidDataException("Required contract was established after the target replay/evidence and cannot authorize it retroactively.");
        var required = document.States.SingleOrDefault(item => item.Id == state.Id);
        if (required is null || Serialize(required) != Serialize(Project(state)))
            throw new InvalidDataException($"State {state.Id} changed its immutable required contract. Source/workflow observations cannot redefine requirements.");
        return document.RequiredContractHash;
    }

    public static string Freeze(DirectoryInfo root, StatePlanDocument plan)
    {
        var (document, error) = EnforcementStore.TryLoadAsync<RequiredStateContractDocument>(root, Path).GetAwaiter().GetResult();
        if (document is null) throw new InvalidDataException(error ?? "No immutable required contract has been established.");
        VerifyDocument(root, document);
        if (plan.SchemaVersion != StatePlanDocument.CurrentSchemaVersion || Hash(plan.States.Select(Project).ToList()) != document.RequiredContractHash)
            throw new InvalidDataException("The plan changed required states/actions/assertions/order after contract establishment.");
        if (!Frozen(document)) Anchor(root, document, frozen: true);
        return document.RequiredContractHash;
    }

    internal static MigrationState Project(MigrationState state)
    {
        var assertions = JsonSerializer.Deserialize<List<StateAssertion>>(Serialize(state.Assertions), EnforcementStore.JsonOptions)!;
        foreach (var assertion in assertions.Where(item => item.Kind == "visual-region-equals-source"))
            assertion.Sufficiency ??= new VisualSufficiency();
        return new MigrationState
        {
        Id = state.Id, Criticality = state.Criticality,
        Preconditions = [.. state.Preconditions], Actions = [.. state.Actions], ExpectedOutcome = state.ExpectedOutcome,
        Assertions = assertions,
        Verification = state.Verification is null ? null :
            JsonSerializer.Deserialize<StateVerificationContract>(Serialize(state.Verification), EnforcementStore.JsonOptions),
        Source = new StateObservation { Status = "not-run" },
        Target = new StateObservation { Status = "not-run" },
        Comparison = new StateComparison { Status = "not-run" }
        };
    }

    private static void VerifyDocument(DirectoryInfo root, RequiredStateContractDocument document)
    {
        if (document.SchemaVersion != "1.0" || document.Id.Length != 32 ||
            document.EstablishedAt == default || document.EstablishedAt > DateTimeOffset.UtcNow ||
            document.Id.Any(character => !char.IsAsciiHexDigit(character)) ||
            !StateClosureSeal.Verify(document) || Hash(document.States) != document.RequiredContractHash)
            throw new InvalidDataException("Required contract signature/hash is not locally trusted.");
        var association = StateClosureSeal.ReadContractAnchor(RootAnchor(root));
        if (association != document.Id)
            throw new InvalidDataException("The required contract is not independently established for this workspace. Copying signed JSON is not pre-source establishment.");
        var anchor = StateClosureSeal.ReadContractAnchor("rc-doc-" + document.Id);
        if (anchor != document.RequiredContractHash + "|open" && anchor != document.RequiredContractHash + "|frozen")
            throw new InvalidDataException("Required contract rollback/replacement does not match the independent CNG anchor.");
    }

    private static bool Frozen(RequiredStateContractDocument document) =>
        StateClosureSeal.ReadContractAnchor("rc-doc-" + document.Id) == document.RequiredContractHash + "|frozen";

    private static bool IsExtension(List<MigrationState> old, List<MigrationState> current)
    {
        if (current.Count < old.Count) return false;
        for (var index = 0; index < old.Count; index++)
        {
            var before = old[index];
            var after = current[index];
            if (before.Id != after.Id || before.Criticality != after.Criticality || before.ExpectedOutcome != after.ExpectedOutcome ||
                !Prefix(before.Preconditions, after.Preconditions) || !Prefix(before.Actions, after.Actions) ||
                !Prefix(before.Assertions, after.Assertions) || before.Verification?.Kind != after.Verification?.Kind ||
                !Prefix(before.Verification!.RequiredActions, after.Verification!.RequiredActions) ||
                !Prefix(before.Verification.Steps, after.Verification.Steps) ||
                !Prefix(before.Verification.Restorations, after.Verification.Restorations))
                return false;
        }
        return true;
    }

    private static bool Prefix<T>(IReadOnlyList<T> old, IReadOnlyList<T> current) =>
        current.Count >= old.Count && old.Select(Serialize).SequenceEqual(current.Take(old.Count).Select(Serialize), StringComparer.Ordinal);

    internal static void ValidateRequirements(StatePlanDocument plan)
    {
        if (plan.SchemaVersion != StatePlanDocument.CurrentSchemaVersion || plan.States is not { Count: > 0 } ||
            plan.States.Any(state => state is null || string.IsNullOrWhiteSpace(state.Id)) ||
            plan.States.Select(state => state.Id).Distinct(StringComparer.Ordinal).Count() != plan.States.Count)
            throw new InvalidDataException("A schema 2.0 plan with unique states and explicit machine requirements is required.");
        foreach (var state in plan.States)
        {
            var spec = state.Verification;
            if (spec is null || spec.Kind is not ("nonvisual" or "visual") || spec.RequiredActions is not { Count: > 0 } ||
                spec.Steps is not { Count: > 0 } || spec.Restorations is null ||
                state.Actions.Count != spec.RequiredActions.Count || state.Assertions is not { Count: > 0 } ||
                spec.RequiredActions.Any(action => action is null || string.IsNullOrWhiteSpace(action.Id) ||
                    action.Command is not { Count: > 0 } || action.Command.Any(string.IsNullOrWhiteSpace)) ||
                state.Assertions.Any(assertion => assertion is null || string.IsNullOrWhiteSpace(assertion.Id) ||
                    assertion.ProbeIndex < 0 ||
                    assertion.Kind is not ("ui-property-equals-source" or "visual-region-equals-source") ||
                    assertion.Kind == "ui-property-equals-source" &&
                        (!UiAssertionProtocol.Supports(assertion.Property) || string.IsNullOrWhiteSpace(assertion.AutomationId))) ||
                spec.RequiredActions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count() != spec.RequiredActions.Count ||
                state.Assertions.Select(assertion => assertion.Id).Distinct(StringComparer.Ordinal).Count() != state.Assertions.Count ||
                spec.Steps.Any(step => step is null || step.Kind is not ("action" or "assertion")) ||
                !spec.Steps.Where(step => step.Kind == "action").Select(step => step.Id).SequenceEqual(spec.RequiredActions.Select(action => action.Id)) ||
                !spec.Steps.Where(step => step.Kind == "assertion").Select(step => step.Id).SequenceEqual(state.Assertions.Select(assertion => assertion.Id)) ||
                spec.Kind == "visual" && !state.Assertions.Any(assertion => assertion.Kind == "visual-region-equals-source"))
                throw new InvalidDataException($"State {state.Id} requires explicit unique ordered action/assertion IDs covering all declared actions.");
            foreach (var restore in spec.Restorations)
                if (restore is null || string.IsNullOrWhiteSpace(restore.Id) ||
                    !spec.RequiredActions.Any(action => action.Id == restore.ReturnActionId) ||
                    !state.Assertions.Any(assertion => assertion.Id == restore.BaselineAssertionId) ||
                    !state.Assertions.Any(assertion => assertion.Id == restore.RestoredAssertionId) ||
                    restore.BaselineAssertionId == restore.RestoredAssertionId)
                    throw new InvalidDataException($"Restoration {restore?.Id ?? "(missing)"} requires baseline, explicit return action, and a distinct restored comparison.");
            if (spec.Restorations.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != spec.Restorations.Count)
                throw new InvalidDataException("Restoration IDs must be unique.");
            foreach (var assertion in state.Assertions.Where(item => item.Kind == "visual-region-equals-source"))
            {
                var policy = assertion.Sufficiency ?? new VisualSufficiency();
                if (assertion.Comparison is not ("pixel-exact" or "sampled-mae") ||
                    !double.IsFinite(assertion.MaximumMeanError) || assertion.MaximumMeanError is < 0 or > 0.1 ||
                    assertion.Region is null && (string.IsNullOrWhiteSpace(assertion.AutomationId) ||
                        assertion.BoundsProbeIndex is null || assertion.BoundsEvidence is null || assertion.SourceCaptureEvidence is null ||
                        assertion.BoundsAfterProbeIndex is null || assertion.BoundsAfterProbeIndex <= assertion.ProbeIndex) ||
                    !double.IsFinite(policy.MinimumContentFraction) ||
                    policy.MinimumContentFraction is <= 0 or > 0.5 ||
                    policy.MinimumDistinctColors is < 2 or > 4096 ||
                    policy.MinimumSamplePixels is < 4 or > 16777216)
                    throw new InvalidDataException("Visual contracts require an explicit semantic ROI or bound source/target UIA bounds and a supported comparison.");
                foreach (var region in new[] { assertion.Region, assertion.SourceRegion }.OfType<VisualRegion>())
                    if (!double.IsFinite(region.X) || !double.IsFinite(region.Y) || !double.IsFinite(region.Width) ||
                        !double.IsFinite(region.Height) || region.X < 0 || region.Y < 0 || region.Width <= 0 ||
                        region.Height <= 0 || region.X + region.Width > 1 || region.Y + region.Height > 1)
                        throw new InvalidDataException("Visual regions must be contained, normalized and nonempty.");
            }
        }
    }

    private static bool Exists(DirectoryInfo root, string path) =>
        EnforcementStore.TryResolveContainedPath(root, path, out var full, out _) && File.Exists(full);
    private static string RootAnchor(DirectoryInfo root) => "rc-root-" + HashText(System.IO.Path.GetFullPath(root.FullName).ToUpperInvariant())[..32];
    private static string Hash(List<MigrationState> states) => HashText(Serialize(states));
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, EnforcementStore.JsonOptions);
}
