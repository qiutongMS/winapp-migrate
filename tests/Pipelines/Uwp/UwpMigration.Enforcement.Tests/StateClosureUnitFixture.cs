// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class EnforcementEvaluatorTests
{
    private static void AttestState(EnforcementEvaluationInput input, MigrationState? state = null)
    {
        state ??= input.StatePlan!.States[0];
        var root = input.TargetRoot;
        const string sourceTree = ".migration-evidence/unit-source-ui.json";
        const string tree = """{"elements":[{"type":"Window","automationId":"main","name":"Ready"}]}""";
        EnforcementStore.WriteTextAtomicAsync(root, sourceTree, tree).GetAwaiter().GetResult();
        if (!state.Source.Evidence.Contains(sourceTree, StringComparer.Ordinal))
            state.Source.Evidence.Add(sourceTree);
        state.Assertions =
        [
            new()
            {
                Id = "main-name", Kind = "ui-property-equals-source", ProbeIndex = 0,
                SourceEvidence = sourceTree, AutomationId = "main", Property = "name"
            }
        ];
        var replay = input.Replay!;
        SealCanonicalFixture(input);
        var id = $"unit-{Guid.NewGuid():N}";
        var directory = $"{EnforcementStore.EnforcementDirectory}/probes/{id}";
        var probe = new UiDevelopmentProbe
        {
            Id = id, StateId = state.Id,
            OperationId = "op-" + id,
            StateContractFingerprint = StateClosureProtocol.ContractFingerprint(root, state),
            TargetFingerprint = replay.TargetFingerprint, ReplayAttestationId = replay.Id,
            StartedAt = replay.CompletedAt.AddSeconds(1), CompletedAt = replay.CompletedAt.AddSeconds(2),
            Command = ["winapp", "ui", "inspect", "--json", "-w", "42"],
            WindowHandle = TestWindowOwnerResolver.WindowHandle, WindowProcessId = replay.ProcessId,
            WindowProcessStartedAt = replay.ProcessStartedAt,
            ArtifactPath = $"{directory}/probe.json",
            StandardOutputPath = $"{directory}/stdout.log", StandardErrorPath = $"{directory}/stderr.log"
        };
        probe.CommandSignature = DiagnosticGrouper.CreateSignature(
            "ui-command", [], string.Join('\u001f', probe.Command.Skip(1)));
        EnforcementStore.WriteTextAtomicAsync(root, probe.StandardOutputPath, tree).GetAwaiter().GetResult();
        EnforcementStore.WriteTextAtomicAsync(root, probe.StandardErrorPath, "").GetAwaiter().GetResult();
        probe.StandardOutputSha256 = StateClosureProtocol.FileHash(root, probe.StandardOutputPath);
        probe.StandardErrorSha256 = StateClosureProtocol.FileHash(root, probe.StandardErrorPath);
        StateClosureSeal.Sign(probe);
        EnforcementStore.WriteAtomicAsync(root, probe.ArtifactPath, probe).GetAwaiter().GetResult();
        var progressPath = System.IO.Path.Combine(root.FullName,
            EnforcementStore.ProgressPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var progress = File.Exists(progressPath)
            ? JsonSerializer.Deserialize<WorkflowProgress>(File.ReadAllText(progressPath), EnforcementStore.JsonOptions)!
            : new WorkflowProgress
            {
                PolicyFingerprint = "unit-fixture", LastTargetFingerprint = replay.TargetFingerprint,
                LastObservedAt = input.GeneratedAt
            };
        progress.Operations.Add(new WorkflowOperationRecord
        {
            Id = probe.OperationId,
            Kind = WorkflowOperation.UiAction, Stage = WorkflowStage.SemanticConvergence,
            TargetFingerprint = replay.TargetFingerprint, ReplayAttestationId = replay.Id,
            Signature = probe.CommandSignature, RecordedAt = probe.StartedAt,
            ProbeId = probe.Id, ProbeSha256 = StateClosureProtocol.FileHash(root, probe.ArtifactPath)
        });
        StateClosureSeal.Sign(progress.Operations[^1]);
        EnforcementStore.WriteAtomicAsync(root, EnforcementStore.ProgressPath, progress).GetAwaiter().GetResult();
        var attestation = StateClosureProtocol.Derive(root, state, replay, [probe.ArtifactPath], input.GeneratedAt, input.WindowOwnerResolver);
        StateClosureSeal.Sign(attestation);
        state.Target.Status = "passed";
        state.Target.Evidence = attestation.Evidence;
        state.Target.Attestation = StateClosureProtocol.AttestationPath(state.Id);
        state.Comparison.Status = "verified";
        EnforcementStore.WriteAtomicAsync(root, state.Target.Attestation, attestation).GetAwaiter().GetResult();
        foreach (var path in attestation.Evidence)
            File.SetLastWriteTimeUtc(System.IO.Path.Combine(root.FullName, path), input.GeneratedAt.UtcDateTime);
    }

    private static void SealCanonicalFixture(EnforcementEvaluationInput input)
    {
        var root = input.TargetRoot;
        var build = input.Build!;
        var replay = input.Replay!;
        build.OperationId ??= "unit-build-" + Guid.NewGuid().ToString("N");
        replay.OperationId ??= "unit-replay-" + Guid.NewGuid().ToString("N");
        build.StandardOutputSha256 = StateClosureProtocol.FileHash(root, build.StandardOutputPath);
        build.StandardErrorSha256 = StateClosureProtocol.FileHash(root, build.StandardErrorPath);
        replay.StandardOutputSha256 = StateClosureProtocol.FileHash(root, replay.StandardOutputPath);
        replay.StandardErrorSha256 = StateClosureProtocol.FileHash(root, replay.StandardErrorPath);
        StateClosureSeal.Sign(build);
        StateClosureSeal.Sign(replay);
        EnforcementStore.WriteAtomicAsync(root, EnforcementStore.BuildAttestationPath, build).GetAwaiter().GetResult();
        EnforcementStore.WriteAtomicAsync(root, EnforcementStore.ReplayAttestationPath, replay).GetAwaiter().GetResult();
        var (progress, _) = EnforcementStore.TryLoadAsync<WorkflowProgress>(root, EnforcementStore.ProgressPath).GetAwaiter().GetResult();
        progress ??= new WorkflowProgress
        {
            PolicyFingerprint = "unit-fixture", LastTargetFingerprint = input.TargetFingerprint,
            LastObservedAt = input.GeneratedAt
        };
        foreach (var item in new[]
        {
            (build.OperationId, build.Id, build.StartedAt, WorkflowOperation.Build, EnforcementStore.BuildAttestationPath),
            (replay.OperationId, replay.Id, replay.StartedAt, WorkflowOperation.Replay, EnforcementStore.ReplayAttestationPath)
        })
        {
            progress.Operations.RemoveAll(operation => operation.Id == item.Item1);
            var operation = new WorkflowOperationRecord
            {
                Id = item.Item1, Kind = item.Item4, TargetFingerprint = input.TargetFingerprint,
                Stage = item.Item4 == WorkflowOperation.Build ? WorkflowStage.MechanicalStabilization : WorkflowStage.TargetBootstrap,
                RecordedAt = item.Item3, ArtifactId = item.Id,
                ArtifactSha256 = StateClosureProtocol.FileHash(root, item.Item5)
            };
            StateClosureSeal.Sign(operation);
            progress.Operations.Add(operation);
        }
        EnforcementStore.WriteAtomicAsync(root, EnforcementStore.ProgressPath, progress).GetAwaiter().GetResult();
    }
}
