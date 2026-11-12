// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

internal static class CanonicalAttestationTrust
{
    internal static bool Verify(DirectoryInfo root, BuildAttestation value) =>
        StateClosureSeal.Verify(value) &&
        OutputsMatch(root, value.StandardOutputPath, value.StandardOutputSha256, value.StandardErrorPath, value.StandardErrorSha256) &&
        Matches(root, value.OperationId, value.Id,
            value.TargetFingerprint, value.RequiredContractHash, value.StateId, value.SliceId, WorkflowOperation.Build, EnforcementStore.BuildAttestationPath);

    internal static bool Verify(DirectoryInfo root, ReplayAttestation value)
    {
        if (!StateClosureSeal.Verify(value) ||
            !OutputsMatch(root, value.StandardOutputPath, value.StandardOutputSha256, value.StandardErrorPath, value.StandardErrorSha256) ||
            !Matches(root, value.OperationId, value.Id, value.TargetFingerprint, value.RequiredContractHash, value.StateId, value.SliceId, WorkflowOperation.Replay, EnforcementStore.ReplayAttestationPath))
            return false;
        var (build, error) = EnforcementStore.TryLoadAsync<BuildAttestation>(root, EnforcementStore.BuildAttestationPath).GetAwaiter().GetResult();
        return error is null && build is not null && build.Id == value.BuildAttestationId &&
            build.RequiredContractHash == value.RequiredContractHash &&
            build.TargetFingerprint == value.TargetFingerprint && Verify(root, build);
    }

    internal static bool Verify(DirectoryInfo root, DiagnosticRunAttestation value) =>
        StateClosureSeal.Verify(value) && Matches(root, value.OperationId, value.Id, value.TargetFingerprint,
            null, value.StateId, value.SliceId, WorkflowOperation.DiagnosticRun, EnforcementStore.DiagnosticAttestationPath, matchContract: false);

    // Historical negative evidence survives replacement of the latest canonical build.
    // This does not grant successful replay freshness or state closure.
    internal static bool VerifyFailure(DirectoryInfo root, ReplayAttestation value)
    {
        if (value.Success || !StateClosureSeal.Verify(value) ||
            !OutputsMatch(root, value.StandardOutputPath, value.StandardOutputSha256, value.StandardErrorPath, value.StandardErrorSha256) ||
            !Matches(root, value.OperationId, value.Id, value.TargetFingerprint, value.RequiredContractHash,
                value.StateId, value.SliceId, WorkflowOperation.Replay, EnforcementStore.ReplayAttestationPath)) return false;
        var (progress, error) = EnforcementStore.TryLoadAsync<WorkflowProgress>(root, EnforcementStore.ProgressPath).GetAwaiter().GetResult();
        return error is null && progress is not null && progress.Operations.Any(operation =>
            operation.Kind == WorkflowOperation.Build && operation.ArtifactId == value.BuildAttestationId &&
            operation.TargetFingerprint == value.TargetFingerprint && operation.RequiredContractHash == value.RequiredContractHash &&
            operation.RecordedAt <= value.StartedAt && !string.IsNullOrEmpty(operation.ArtifactSha256));
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
        string fingerprint, string? requiredHash, string? stateId, string? sliceId,
        WorkflowOperation kind, string artifactPath, bool matchContract = true)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return false;
        try
        {
            var (progress, error) = EnforcementStore.TryLoadAsync<WorkflowProgress>(root, EnforcementStore.ProgressPath)
                .GetAwaiter().GetResult();
            return error is null && progress is { SchemaVersion: WorkflowProgress.CurrentSchemaVersion, Operations: not null } &&
                WorkflowOperationAuthentication.VerifyAnchored(root, progress.Operations) &&
                progress.Operations.Count(operation => operation is not null &&
                    operation.Id == operationId && operation.Kind == kind &&
                    operation.TargetFingerprint == fingerprint && operation.ArtifactId == artifactId &&
                    (!matchContract || operation.RequiredContractHash == requiredHash) &&
                    operation.StateId == stateId && operation.IntendedSliceId == sliceId &&
                    operation.ArtifactSha256 == StateClosureProtocol.FileHash(root, artifactPath) &&
                    StateClosureSeal.Verify(operation)) == 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }
}
