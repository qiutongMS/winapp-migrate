// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

public sealed class FourCheckSurfaceMap
{
    public string SchemaVersion { get; set; } = "1.0";
    public string Profile { get; set; } = "four-checks";
    public string SourceInputFingerprint { get; set; } = string.Empty;
    public List<SurfaceMapping> Mappings { get; set; } = [];
    public Dictionary<string, List<UiStructureNormalization>> StructureExceptions { get; set; } = [];
    public List<DeclaredSurface> AdditionalSurfaces { get; set; } = [];
}

public sealed class SurfaceMapping
{
    public string SurfaceId { get; set; } = string.Empty;
    public string Disposition { get; set; } = "pending";
    public string? TargetFile { get; set; }
    public string? TargetNode { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<string> StateIds { get; set; } = [];
}

public sealed record DeclaredSurface(string Id, string Kind, string Reason);
public sealed record SurfaceInput(string Path, string Sha256);
public sealed record AcceptanceSurface(
    string Id, string Kind, string Origin, string Description,
    bool RequiresReachability, string? ExclusiveGroup = null);

public sealed class FourCheckInventory
{
    public string SchemaVersion { get; set; } = "1.0";
    public string Profile { get; set; } = "four-checks";
    public string SourceInputFingerprint { get; set; } = string.Empty;
    public List<SurfaceInput> Inputs { get; set; } = [];
    public List<AcceptanceSurface> Surfaces { get; set; } = [];
    public List<string> DiscoveryIssues { get; set; } = [];
}

public sealed record AcceptanceCheck(
    string Id, string Status, int Required, int Satisfied, IReadOnlyList<string> Issues);
public sealed record FourCheckStateResult(
    string Id, string Reachability, UiStructureResult? Structure, string? Issue);
public sealed record FourCheckResult(
    string SchemaVersion, string Profile, string Status, string TargetFingerprint,
    string SourceInputFingerprint, IReadOnlyList<AcceptanceCheck> Checks,
    IReadOnlyList<FourCheckStateResult> States, IReadOnlyList<string> ExcludedClaims);

internal static class FourCheckJson
{
    internal static JsonSerializerOptions Options { get; } = new(EnforcementStore.JsonOptions)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 256
    };

    internal static T Read<T>(DirectoryInfo root, string path)
    {
        if (!EnforcementStore.TryResolveContainedPath(root, path, out var fullPath, out var error))
            throw new InvalidDataException(error);
        return JsonSerializer.Deserialize<T>(File.ReadAllText(fullPath), Options)
            ?? throw new InvalidDataException($"Empty acceptance artifact: {path}");
    }

    internal static Task WriteAsync<T>(
        DirectoryInfo root, string path, T value, CancellationToken cancellationToken) =>
        EnforcementStore.WriteTextAtomicAsync(
            root, path, JsonSerializer.Serialize(value, Options), cancellationToken);
}
