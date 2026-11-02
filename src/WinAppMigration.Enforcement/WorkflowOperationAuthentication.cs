// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinAppMigration.Enforcement;

internal static class WorkflowOperationAuthentication
{
    internal static string? ReadHead(DirectoryInfo root) => StateClosureSeal.ReadContractAnchor(HeadName(root));

    internal static bool VerifyAnchored(DirectoryInfo root, IReadOnlyList<WorkflowOperationRecord> operations) =>
        Verify(operations) && ReadHead(root) == Head(operations);

    internal static void CommitHead(DirectoryInfo root, string? previous, IReadOnlyList<WorkflowOperationRecord> operations)
    {
        if (ReadHead(root) != previous || !Verify(operations))
            throw new InvalidDataException("Operation history changed while committing its independent head.");
        StateClosureSeal.WriteContractAnchor(HeadName(root), Head(operations) ?? string.Empty);
    }

    internal static void Seal(WorkflowOperationRecord operation, WorkflowOperationRecord? previous, int sequence)
    {
        operation.Sequence = sequence;
        operation.PreviousOperationSha256 = previous is null ? null : Digest(previous);
        StateClosureSeal.Sign(operation);
    }

    internal static bool Verify(IReadOnlyList<WorkflowOperationRecord> operations)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(operations, EnforcementStore.JsonOptions))));
        return StateClosureSeal.VerifyHistory(digest, () => VerifyCore(operations));
    }

    private static bool VerifyCore(IReadOnlyList<WorkflowOperationRecord> operations)
    {
        string? previous = null;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation is null || string.IsNullOrWhiteSpace(operation.Id) || !ids.Add(operation.Id) ||
                operation.Sequence != index + 1 || operation.PreviousOperationSha256 != previous ||
                !StateClosureSeal.Verify(operation))
                return false;
            previous = Digest(operation);
        }
        return true;
    }

    private static string Digest(WorkflowOperationRecord operation) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(operation, EnforcementStore.JsonOptions)))).ToLowerInvariant();

    private static string? Head(IReadOnlyList<WorkflowOperationRecord> operations) =>
        operations.Count == 0 ? null : operations.Count + ":" + Digest(operations[^1]);

    private static string HeadName(DirectoryInfo root) => "oh-" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(root.FullName).ToUpperInvariant())))[..32];
}
