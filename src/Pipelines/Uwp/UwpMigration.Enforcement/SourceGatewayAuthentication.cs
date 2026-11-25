// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

internal static class SourceGatewayAuthentication
{
    internal static async Task AppendProbeAsync(
        DirectoryInfo root, string gatewayRoot, string runId, string probePath, CancellationToken token)
    {
        var path = $"{gatewayRoot}/runs/{runId}/probe-history.json";
        var (history, error) = await EnforcementStore.TryLoadAsync<List<string>>(root, path, token);
        if (error is not null)
            throw new InvalidDataException(error);
        if (history is not null)
            Require(root, path);
        else if (StateClosureSeal.ReadContractAnchor(Anchor(root, path)) is not null)
            throw new InvalidDataException("The authenticated source probe history is missing.");
        history ??= [];
        history.Add(probePath);
        await EnforcementStore.WriteAtomicAsync(root, path, history, token);
        Record(root, path);
    }

    internal static async Task<IReadOnlyList<string>> ProbeHistoryAsync(
        DirectoryInfo root, string gatewayRoot, string runId, CancellationToken token)
    {
        var path = $"{gatewayRoot}/runs/{runId}/probe-history.json";
        Require(root, path);
        var (history, error) = await EnforcementStore.TryLoadAsync<List<string>>(root, path, token);
        return history ?? throw new InvalidDataException(error ?? "Source probe history is missing.");
    }

    internal static void Record(DirectoryInfo root, string artifact, params string[] outputs) =>
        StateClosureSeal.WriteContractAnchor(Anchor(root, artifact), Digest(root, artifact, outputs));

    internal static void Require(DirectoryInfo root, string artifact, params string[] outputs)
    {
        if (StateClosureSeal.ReadContractAnchor(Anchor(root, artifact)) != Digest(root, artifact, outputs))
            throw new InvalidDataException(
                "Source evidence is modified, foreign, or not authenticated by this workspace's source gateway; capture fresh source evidence.");
    }

    private static string Digest(DirectoryInfo root, string artifact, string[] outputs) =>
        string.Join("|", new[] { artifact }.Concat(outputs).Select(path => StateClosureProtocol.FileHash(root, path)));

    private static string Anchor(DirectoryInfo root, string artifact) =>
        "source-gateway-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(root.FullName).ToUpperInvariant() + "\n" + artifact))).ToLowerInvariant();
}
