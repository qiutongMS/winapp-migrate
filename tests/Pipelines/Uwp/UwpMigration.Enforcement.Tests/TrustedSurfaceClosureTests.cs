// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    [DataRow("probe")]
    [DataRow("state")]
    [DataRow("operation")]
    [DataRow("build")]
    [DataRow("replay")]
    [DataRow("build-output")]
    [DataRow("replay-output")]
    public async Task TrustedClosure_IndependentAttackerKeysCannotAuthenticateTheArtifactChain(
        string forged)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var probe = await CaptureClosureProbeAsync(fixture);
        using var attacker =
            ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (forged == "state")
        {
            await UwpEnforcementRunner.RecordSurfaceAsync(
                fixture.Root,
                TestWindowOwnerResolver.Shared,
                "state-1",
                [probe.ArtifactPath]);
            var path =
                StateClosureProtocol.AttestationPath("state-1");
            var state =
                await fixture.LoadAsync<StateClosureAttestation>(
                    path);
            ForgeSeal(
                state,
                "state-closure",
                attacker,
                value => state.Seal = value,
                value => state.SealPublicKey = value);
            Assert.IsTrue(
                StateClosureSeal.VerifyPortableIntegrity(state));
            Assert.IsFalse(StateClosureSeal.Verify(state));
            await EnforcementStore.WriteAtomicAsync(
                fixture.Root,
                path,
                state);
            var assessed =
                await UwpEnforcementRunner.AssessAsync(
                    fixture.Root,
                    TestWindowOwnerResolver.Shared);
            Assert.AreEqual(
                "blocked",
                assessed.Gates.Single(
                    gate => gate.Id == "state-closure").Status);
            return;
        }
        if (forged == "probe")
        {
            SealPortable(probe, attacker);
            Assert.IsTrue(
                StateClosureSeal.VerifyPortableIntegrity(probe));
            Assert.IsFalse(StateClosureSeal.Verify(probe));
            await PersistTrackedProbeAsync(fixture, probe);
        }
        else if (forged == "operation")
        {
            var progress =
                await fixture.LoadAsync<WorkflowProgress>(
                    EnforcementStore.ProgressPath);
            var operation =
                progress.Operations.Single(
                    item => item.Id == probe.OperationId);
            ForgeSeal(
                operation,
                "operation",
                attacker,
                value => operation.Seal = value,
                value => operation.SealPublicKey = value);
            await EnforcementStore.WriteAtomicAsync(
                fixture.Root,
                EnforcementStore.ProgressPath,
                progress);
        }
        else if (forged.StartsWith("build", StringComparison.Ordinal))
        {
            var build =
                await fixture.LoadAsync<BuildAttestation>(
                    EnforcementStore.BuildAttestationPath);
            if (forged == "build-output")
            {
                await File.AppendAllTextAsync(
                    fixture.Path(build.StandardOutputPath),
                    "tampered");
            }
            else
            {
                ForgeSeal(
                    build,
                    "build",
                    attacker,
                    value => build.Seal = value,
                    value => build.SealPublicKey = value);
                await EnforcementStore.WriteAtomicAsync(
                    fixture.Root,
                    EnforcementStore.BuildAttestationPath,
                    build);
                await RefreshCanonicalOperationAsync(
                    fixture,
                    build.OperationId!,
                    EnforcementStore.BuildAttestationPath);
            }
        }
        else
        {
            var replay =
                await fixture.LoadAsync<ReplayAttestation>(
                    EnforcementStore.ReplayAttestationPath);
            if (forged == "replay-output")
            {
                await File.AppendAllTextAsync(
                    fixture.Path(replay.StandardOutputPath),
                    "tampered");
            }
            else
            {
                ForgeSeal(
                    replay,
                    "replay",
                    attacker,
                    value => replay.Seal = value,
                    value => replay.SealPublicKey = value);
                await EnforcementStore.WriteAtomicAsync(
                    fixture.Root,
                    EnforcementStore.ReplayAttestationPath,
                    replay);
                await RefreshCanonicalOperationAsync(
                    fixture,
                    replay.OperationId!,
                    EnforcementStore.ReplayAttestationPath);
            }
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordSurfaceAsync(
                fixture.Root,
                TestWindowOwnerResolver.Shared,
                "state-1",
                [probe.ArtifactPath]));
    }

    [TestMethod]
    [DataRow("record", "absent")]
    [DataRow("record", "reused")]
    [DataRow("finalize", "other-owner")]
    [DataRow("assess", "unavailable")]
    public async Task TrustedClosure_RechecksLiveWindowIdentityAfterCapture(
        string phase,
        string replacement)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var owner = new LiveClosureOwner();
        await new UwpEnforcementRunner(
            SuccessfulLaunch(),
            windowOwnerResolver: owner).RunAsync(
                fixture.Root,
                new UwpEnforcementOptions());
        var probe = await new UwpEnforcementRunner(
            new FakeProcessRunner(
                new EnforcementProcessResult(0, ClosureTree, "")),
            windowOwnerResolver: owner).InvokeUiAsync(
                fixture.Root,
                "winapp",
                ["inspect", "-w", "42", "--json"],
                [],
                "state-1");
        if (phase != "record")
        {
            await UwpEnforcementRunner.RecordSurfaceAsync(
                fixture.Root,
                owner,
                "state-1",
                [probe.ArtifactPath]);
        }
        if (phase == "assess")
        {
            Assert.AreEqual(
                "passed",
                (await UwpEnforcementRunner.SynchronizeReportAsync(
                    fixture.Root,
                    owner)).Status);
        }
        switch (replacement)
        {
            case "absent":
                owner.WindowExists = false;
                break;
            case "reused":
                owner.StartedAt = owner.StartedAt.AddHours(1);
                break;
            case "other-owner":
                owner.ProcessId = 4343;
                break;
            case "unavailable":
                owner.Available = false;
                break;
        }
        if (phase == "record")
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                UwpEnforcementRunner.RecordSurfaceAsync(
                    fixture.Root,
                    owner,
                    "state-1",
                    [probe.ArtifactPath]));
        }
        else if (phase == "finalize")
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                UwpEnforcementRunner.SynchronizeReportAsync(
                    fixture.Root,
                    owner));
        }
        else
        {
            var assessed =
                await UwpEnforcementRunner.AssessAsync(
                    fixture.Root,
                    owner);
            Assert.AreNotEqual("passed", assessed.Status);
            Assert.AreNotEqual(
                "complete",
                assessed.Disposition?.Kind);
        }
    }

    private static void ForgeSeal<T>(
        T value,
        string domain,
        ECDsa key,
        Action<string?> seal,
        Action<string> publicKey)
    {
        seal(null);
        publicKey(
            Convert.ToBase64String(
                key.ExportSubjectPublicKeyInfo()));
        seal(
            Convert.ToBase64String(
                key.SignData(
                    Encoding.UTF8.GetBytes(
                        domain +
                        "\n" +
                        JsonSerializer.Serialize(
                            value,
                            EnforcementStore.JsonOptions)),
                    HashAlgorithmName.SHA256)));
    }

    private static async Task RefreshCanonicalOperationAsync(
        RunnerFixture fixture,
        string operationId,
        string path)
    {
        var progress =
            await fixture.LoadAsync<WorkflowProgress>(
                EnforcementStore.ProgressPath);
        var operation =
            progress.Operations.Single(
                item => item.Id == operationId);
        operation.ArtifactSha256 =
            StateClosureProtocol.FileHash(
                fixture.Root,
                path);
        ContractTestAuthority.SealOperations(
            progress,
            fixture.Root);
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            EnforcementStore.ProgressPath,
            progress);
    }

    private sealed class LiveClosureOwner : IWindowOwnerResolver
    {
        internal int ProcessId { get; set; } = 4242;
        internal DateTimeOffset StartedAt { get; set; } =
            TestWindowOwnerResolver.ProcessStartedAt;
        internal bool WindowExists { get; set; } = true;
        internal bool Available { get; set; } = true;

        public bool TryGetProcessId(
            long windowHandle,
            out int processId)
        {
            processId = ProcessId;
            return WindowExists &&
                windowHandle ==
                    TestWindowOwnerResolver.WindowHandle;
        }

        public bool TryGetProcessIdentity(
            int processId,
            out ProcessIdentity identity)
        {
            identity =
                new ProcessIdentity(processId, StartedAt);
            return Available;
        }
    }
}
