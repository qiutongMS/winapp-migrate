// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace UwpMigration.Mechanical;

public sealed record MsBuildProjectItem(
    string Kind,
    string Identity,
    string FullPath,
    string DefiningProjectFullPath,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record MsBuildProjectItemEvaluation(
    bool Succeeded,
    IReadOnlyList<MsBuildProjectItem> Items,
    IReadOnlyList<string> RestoredPackageDirectories,
    string? Error)
{
    public bool DuplicatePriItemsValidated { get; init; }

    public static MsBuildProjectItemEvaluation Success(
        IReadOnlyList<MsBuildProjectItem> items,
        IReadOnlyList<string>? restoredPackageDirectories = null,
        bool duplicatePriItemsValidated = false) =>
        new(true, items, restoredPackageDirectories ?? [], null)
        {
            DuplicatePriItemsValidated = duplicatePriItemsValidated
        };

    public static MsBuildProjectItemEvaluation Failure(string error) =>
        new(false, [], [], error);
}

public interface IMsBuildProjectItemEvaluator
{
    Task<MsBuildProjectItemEvaluation> EvaluateAsync(
        FileInfo project,
        CancellationToken cancellationToken = default);
}
