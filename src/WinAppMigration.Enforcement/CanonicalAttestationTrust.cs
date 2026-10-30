// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

internal static class CanonicalAttestationTrust
{
    internal static bool Verify(DirectoryInfo root, BuildAttestation value) =>
        StateClosureSeal.Verify(value) &&
        OutputsMatch(root, value.StandardOutputPath, value.StandardOutputSha256, value.StandardErrorPath, value.StandardErrorSha256) &&
        Matches(root, value.OperationId, value.Id,
            value.TargetFingerprint, WorkflowOperation.Build, EnforcementStore.BuildAttestationPath);

    internal static bool Verify(DirectoryInfo root, ReplayAttestation value)
    {
        if (!StateClosureSeal.Verify(value) ||
            !OutputsMatch(root, value.StandardOutputPath, value.StandardOutputSha256, value.StandardErrorPath, value.StandardErrorSha256) ||
            !Matches(root, value.OperationId, value.Id, value.TargetFingerprint, WorkflowOperation.Replay, EnforcementStore.ReplayAttestationPath))
            return false;
        var (build, error) = EnforcementStore.TryLoadAsync<BuildAttestation>(root, EnforcementStore.BuildAttestationPath).GetAwaiter().GetResult();
        return error is null && build is not null && build.Id == value.BuildAttestationId &&
            build.TargetFingerprint == value.TargetFingerprint && Verify(root, build);
    }

    private static bool OutputsMatch(DirectoryInfo root, string stdout, string? stdoutHash, string stderr, string? stderrHash)
    {
        try
        {
            return !string.IsNullOrEmpty(stdoutHash) && !string.IsNullOrEmpty(stderrHash) &&
                StateClosureProtocol.FileHash(root, stdout) == stdoutHash && StateClosureProtocol.FileHash(root, stderr) == stderrHash;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool Matches(DirectoryInfo root, string? operationId, string artifactId,
        string fingerprint, WorkflowOperation kind, string artifactPath)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return false;
        try
        {
            var (progress, error) = EnforcementStore.TryLoadAsync<WorkflowProgress>(root, EnforcementStore.ProgressPath)
                .GetAwaiter().GetResult();
            return error is null && progress?.Operations is not null &&
                progress.Operations.Count(operation => operation is not null &&
                    operation.Id == operationId && operation.Kind == kind &&
                    operation.TargetFingerprint == fingerprint && operation.ArtifactId == artifactId &&
                    operation.ArtifactSha256 == StateClosureProtocol.FileHash(root, artifactPath) &&
                    StateClosureSeal.Verify(operation)) == 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }
}
