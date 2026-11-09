// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinAppMigration.Enforcement;

public sealed class SelectorBindingAmendment
{
    [JsonPropertyName("stateId")]
    public required string StateId { get; set; }

    [JsonPropertyName("actionId")]
    public required string ActionId { get; set; }

    [JsonPropertyName("replacementCommand")]
    public List<string> ReplacementCommand { get; set; } = [];

    [JsonPropertyName("reason")]
    public required string Reason { get; set; }

    [JsonPropertyName("recordedAt")]
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class SelectorBindingAmendmentDocument
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("requiredContractHash")]
    public required string RequiredContractHash { get; set; }

    [JsonPropertyName("generation")]
    public int Generation { get; set; }

    [JsonPropertyName("previousDocumentHash")]
    public string? PreviousDocumentHash { get; set; }

    [JsonPropertyName("amendments")]
    public List<SelectorBindingAmendment> Amendments { get; set; } = [];

    [JsonPropertyName("seal")]
    public string? Seal { get; set; }

    [JsonPropertyName("sealPublicKey")]
    public string? SealPublicKey { get; set; }
}

public static class SelectorBindingAmendments
{
    public const string Path =
        ".migration-evidence/enforcement/selector-bindings.json";

    internal static bool HasHistory(DirectoryInfo root) =>
        File.Exists(System.IO.Path.Combine(
            root.FullName,
            Path.Replace(
                '/',
                System.IO.Path.DirectorySeparatorChar))) ||
        StateClosureSeal.ReadContractAnchor(AnchorName(root)) is not null;

    public static async Task<SelectorBindingAmendmentDocument> RebindAsync(
        DirectoryInfo root,
        MigrationState state,
        string actionId,
        IReadOnlyList<string> replacementCommand,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(actionId))
            throw new ArgumentException("Action ID must not be empty.", nameof(actionId));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A selector-rebinding reason is required.", nameof(reason));

        var requiredHash = RequiredStateContracts.Require(root, state);
        var action = state.Verification?.RequiredActions
            .SingleOrDefault(candidate => candidate.Id == actionId)
            ?? throw new InvalidDataException(
                $"State {state.Id} has no required action {actionId}.");
        var existing = await LoadAsync(root, requiredHash, cancellationToken);
        var anchorName = AnchorName(root);
        if (existing is null &&
            StateClosureSeal.ReadContractAnchor(anchorName) is not null)
        {
            throw new InvalidDataException(
                "The anchored selector-binding generation exists but its current document is missing.");
        }
        var baseline = action.Command;
        var replacement = replacementCommand.ToList();
        ValidateSelectorOnlyChange(baseline, replacement);
        var active = ResolveCommand(existing, state.Id, action);
        if (active.SequenceEqual(replacement, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The replacement selector binding is unchanged.");
        }

        var document = new SelectorBindingAmendmentDocument
        {
            RequiredContractHash = requiredHash,
            Generation = (existing?.Generation ?? 0) + 1,
            PreviousDocumentHash = existing is null
                ? null
                : DocumentHash(existing),
            Amendments = existing?.Amendments.ToList() ?? []
        };
        document.Amendments.Add(new SelectorBindingAmendment
        {
            StateId = state.Id,
            ActionId = actionId,
            ReplacementCommand = replacement,
            Reason = reason.Trim(),
            RecordedAt = DateTimeOffset.UtcNow
        });
        Sign(document);
        await EnforcementStore.WriteAtomicAsync(
            root,
            Path,
            document,
            cancellationToken);
        StateClosureSeal.WriteContractAnchor(
            anchorName,
            DocumentHash(document));
        return document;
    }

    internal static IReadOnlyList<string> ResolveCommand(
        DirectoryInfo root,
        MigrationState state,
        RequiredStateAction action)
    {
        var requiredHash = RequiredStateContracts.Require(root, state);
        var document = Load(root, requiredHash);
        ValidateStateAmendments(document, state);
        return ResolveCommand(document, state.Id, action);
    }

    internal static string? StateHash(
        DirectoryInfo root,
        MigrationState state)
    {
        var requiredHash = RequiredStateContracts.Require(root, state);
        var document = Load(root, requiredHash);
        ValidateStateAmendments(document, state);
        var amendments = document?.Amendments
            .Where(item => item.StateId == state.Id)
            .ToList();
        return amendments is not { Count: > 0 }
            ? null
            : Hash(JsonSerializer.Serialize(
                amendments,
                EnforcementStore.JsonOptions));
    }

    private static List<string> ResolveCommand(
        SelectorBindingAmendmentDocument? document,
        string stateId,
        RequiredStateAction action) =>
        document?.Amendments
            .LastOrDefault(item =>
                item.StateId == stateId &&
                item.ActionId == action.Id)
            ?.ReplacementCommand
        ?? action.Command;

    private static async Task<SelectorBindingAmendmentDocument?> LoadAsync(
        DirectoryInfo root,
        string requiredHash,
        CancellationToken cancellationToken)
    {
        var (document, error) =
            await EnforcementStore
                .TryLoadAsync<SelectorBindingAmendmentDocument>(
                    root,
                    Path,
                    cancellationToken);
        if (error is not null)
            throw new InvalidDataException(error);
        if (document is not null)
            Verify(root, document, requiredHash);
        return document;
    }

    private static SelectorBindingAmendmentDocument? Load(
        DirectoryInfo root,
        string requiredHash)
    {
        var (document, error) =
            EnforcementStore
                .TryLoadAsync<SelectorBindingAmendmentDocument>(
                    root,
                    Path)
                .GetAwaiter()
                .GetResult();
        if (error is not null)
            throw new InvalidDataException(error);
        if (document is not null)
            Verify(root, document, requiredHash);
        return document;
    }

    private static void Verify(
        DirectoryInfo root,
        SelectorBindingAmendmentDocument document,
        string requiredHash)
    {
        if (document.SchemaVersion !=
                SelectorBindingAmendmentDocument.CurrentSchemaVersion ||
            document.Generation < 1 ||
            document.Generation != document.Amendments.Count ||
            document.RequiredContractHash != requiredHash ||
            document.Generation == 1 &&
                document.PreviousDocumentHash is not null ||
            document.Generation > 1 &&
                string.IsNullOrWhiteSpace(
                    document.PreviousDocumentHash) ||
            document.Amendments.Any(item =>
                string.IsNullOrWhiteSpace(item.StateId) ||
                string.IsNullOrWhiteSpace(item.ActionId) ||
                string.IsNullOrWhiteSpace(item.Reason) ||
                item.RecordedAt == default ||
                item.RecordedAt > DateTimeOffset.UtcNow ||
                item.ReplacementCommand.Count == 0) ||
            !document.Amendments
                .Select(item => item.RecordedAt)
                .SequenceEqual(document.Amendments
                    .Select(item => item.RecordedAt)
                    .OrderBy(item => item)) ||
            !VerifySeal(document))
        {
            throw new InvalidDataException(
                "Selector-binding amendments are invalid or not locally trusted.");
        }

        var anchor = StateClosureSeal.ReadContractAnchor(AnchorName(root));
        if (anchor != DocumentHash(document))
        {
            throw new InvalidDataException(
                "Selector-binding amendments do not match the independently anchored current generation.");
        }
    }

    private static void ValidateStateAmendments(
        SelectorBindingAmendmentDocument? document,
        MigrationState state)
    {
        if (document is null)
            return;
        foreach (var amendment in document.Amendments.Where(
                     item => item.StateId == state.Id))
        {
            var action = state.Verification?.RequiredActions
                .SingleOrDefault(candidate =>
                    candidate.Id == amendment.ActionId)
                ?? throw new InvalidDataException(
                    $"Selector binding references unknown required action {amendment.ActionId} in state {state.Id}.");
            ValidateSelectorOnlyChange(
                action.Command,
                amendment.ReplacementCommand);
        }
    }

    private static void ValidateSelectorOnlyChange(
        List<string> baseline,
        List<string> replacement)
    {
        if (baseline.Count < 2 ||
            replacement.Count < 2 ||
            baseline[0] != replacement[0] ||
            ContainsRuntimeBinding(replacement) ||
            !SemanticShape(baseline).SequenceEqual(
                SemanticShape(replacement),
                StringComparer.Ordinal) ||
            RequiredActionProtocol.Normalize(baseline).SequenceEqual(
                RequiredActionProtocol.Normalize(replacement),
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "Selector rebinding may change only the primary selector and selector-refinement options; the UI command, payload, property, and semantic order must remain unchanged.");
        }
    }

    private static bool ContainsRuntimeBinding(
        List<string> arguments)
    {
        for (var index = 1; index < arguments.Count; index++)
        {
            if (WinAppUiArguments.TryOption(
                    arguments,
                    ref index,
                    "-w",
                    "--window",
                    out _) ||
                WinAppUiArguments.TryOption(
                    arguments,
                    ref index,
                    "-a",
                    "--app",
                    out _) ||
                WinAppUiArguments.TryOption(
                    arguments,
                    ref index,
                    "-o",
                    "--output",
                    out _) ||
                arguments[index] == "--json")
            {
                return true;
            }
        }
        return false;
    }

    private static List<string> SemanticShape(
        List<string> arguments)
    {
        var result = new List<string> { arguments[0] };
        var selectorSkipped = false;
        for (var index = 1; index < arguments.Count; index++)
        {
            if (TrySkipLocatorOption(arguments, ref index))
                continue;
            if (WinAppUiArguments.TryOption(
                    arguments,
                    ref index,
                    "-p",
                    "--property",
                    out var property))
            {
                result.Add("--property");
                result.Add(property!);
                continue;
            }
            if (!selectorSkipped &&
                !arguments[index].StartsWith('-'))
            {
                selectorSkipped = true;
                continue;
            }
            result.Add(arguments[index]);
        }
        if (!selectorSkipped)
        {
            throw new InvalidDataException(
                "Selector rebinding requires a primary selector.");
        }
        return result;
    }

    private static bool TrySkipLocatorOption(
        List<string> arguments,
        ref int index) =>
        WinAppUiArguments.TryOption(
            arguments,
            ref index,
            "--root",
            "--root",
            out _) ||
        WinAppUiArguments.TryOption(
            arguments,
            ref index,
            "--type",
            "--type",
            out _) ||
        WinAppUiArguments.TryOption(
            arguments,
            ref index,
            "--class-name",
            "--class-name",
            out _);

    private static void Sign(
        SelectorBindingAmendmentDocument document)
    {
        document.Seal = null;
        document.SealPublicKey = null;
        var payload = JsonSerializer.Serialize(
            document,
            EnforcementStore.JsonOptions);
        var signed = StateClosureSeal.SignDocument(
            "selector-bindings",
            payload);
        document.Seal = signed.Seal;
        document.SealPublicKey = signed.PublicKey;
    }

    private static bool VerifySeal(
        SelectorBindingAmendmentDocument document)
    {
        var seal = document.Seal;
        var publicKey = document.SealPublicKey;
        try
        {
            document.Seal = null;
            document.SealPublicKey = null;
            return StateClosureSeal.VerifyDocument(
                "selector-bindings",
                JsonSerializer.Serialize(
                    document,
                    EnforcementStore.JsonOptions),
                seal,
                publicKey);
        }
        finally
        {
            document.Seal = seal;
            document.SealPublicKey = publicKey;
        }
    }

    private static string DocumentHash(
        SelectorBindingAmendmentDocument document) =>
        Hash(JsonSerializer.Serialize(
            document,
            EnforcementStore.JsonOptions));

    private static string AnchorName(DirectoryInfo root) =>
        "selector-root-" +
        Hash(System.IO.Path.GetFullPath(root.FullName)
            .ToUpperInvariant())[..32];

    private static string Hash(string value) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
