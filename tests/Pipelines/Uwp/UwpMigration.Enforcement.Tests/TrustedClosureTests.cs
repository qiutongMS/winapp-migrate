// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public async Task TrustedClosure_IndependentAttackerKeysCannotAuthenticateTheArtifactChain(string forged)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var probe = await CaptureClosureProbeAsync(fixture);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (forged == "state")
        {
            await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
            var path = StateClosureProtocol.AttestationPath("state-1");
            var state = await fixture.LoadAsync<StateClosureAttestation>(path);
            ForgeSeal(state, "state-closure", attacker, value => state.Seal = value, value => state.SealPublicKey = value);
            Assert.IsTrue(StateClosureSeal.VerifyPortableIntegrity(state));
            Assert.IsFalse(StateClosureSeal.Verify(state));
            await EnforcementStore.WriteAtomicAsync(fixture.Root, path, state);
            var assessed = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
            Assert.AreEqual("blocked", assessed.Gates.Single(gate => gate.Id == "state-closure").Status);
            return;
        }
        if (forged == "probe")
        {
            SealPortable(probe, attacker);
            Assert.IsTrue(StateClosureSeal.VerifyPortableIntegrity(probe));
            Assert.IsFalse(StateClosureSeal.Verify(probe));
            await PersistTrackedProbeAsync(fixture, probe);
        }
        else if (forged == "operation")
        {
            var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
            var operation = progress.Operations.Single(item => item.Id == probe.OperationId);
            ForgeSeal(operation, "operation", attacker, value => operation.Seal = value, value => operation.SealPublicKey = value);
            await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, progress);
        }
        else if (forged.StartsWith("build", StringComparison.Ordinal))
        {
            var build = await fixture.LoadAsync<BuildAttestation>(EnforcementStore.BuildAttestationPath);
            if (forged == "build-output")
                await File.AppendAllTextAsync(fixture.Path(build.StandardOutputPath), "tampered");
            else
            {
                ForgeSeal(build, "build", attacker, value => build.Seal = value, value => build.SealPublicKey = value);
                await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.BuildAttestationPath, build);
                await RefreshCanonicalOperationAsync(fixture, build.OperationId!, EnforcementStore.BuildAttestationPath);
            }
        }
        else
        {
            var replay = await fixture.LoadAsync<ReplayAttestation>(EnforcementStore.ReplayAttestationPath);
            if (forged == "replay-output")
                await File.AppendAllTextAsync(fixture.Path(replay.StandardOutputPath), "tampered");
            else
            {
                ForgeSeal(replay, "replay", attacker, value => replay.Seal = value, value => replay.SealPublicKey = value);
                await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.ReplayAttestationPath, replay);
                await RefreshCanonicalOperationAsync(fixture, replay.OperationId!, EnforcementStore.ReplayAttestationPath);
            }
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));
    }

    [TestMethod]
    [DataRow("record", "absent")]
    [DataRow("record", "reused")]
    [DataRow("record", "other-owner")]
    [DataRow("record", "unavailable")]
    [DataRow("finalize", "absent")]
    [DataRow("finalize", "reused")]
    [DataRow("finalize", "other-owner")]
    [DataRow("finalize", "unavailable")]
    [DataRow("assess", "absent")]
    [DataRow("assess", "reused")]
    [DataRow("assess", "other-owner")]
    [DataRow("assess", "unavailable")]
    public async Task TrustedClosure_RechecksLiveWindowIdentityAfterCapture(string phase, string replacement)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var owner = new LiveClosureOwner();
        await new UwpEnforcementRunner(SuccessfulLaunch(), windowOwnerResolver: owner)
            .RunAsync(fixture.Root, new UwpEnforcementOptions());
        var probe = await new UwpEnforcementRunner(new FakeProcessRunner(new EnforcementProcessResult(0, ClosureTree, "")),
            windowOwnerResolver: owner).InvokeUiAsync(fixture.Root, "winapp", ["inspect", "-w", "42", "--json"], [], "state-1");
        if (phase != "record")
            await UwpEnforcementRunner.RecordStateAsync(fixture.Root, owner, "state-1", [probe.ArtifactPath]);
        if (phase == "assess")
            Assert.AreEqual("passed", (await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, owner)).Status);
        switch (replacement)
        {
            case "absent": owner.WindowExists = false; break;
            case "reused": owner.StartedAt = owner.StartedAt.AddHours(1); break;
            case "other-owner": owner.ProcessId = 4343; break;
            case "unavailable": owner.Available = false; break;
        }
        if (phase == "record")
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                UwpEnforcementRunner.RecordStateAsync(fixture.Root, owner, "state-1", [probe.ArtifactPath]));
        else if (phase == "finalize")
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, owner));
        else
        {
            var assessed = await UwpEnforcementRunner.AssessAsync(fixture.Root, owner);
            Assert.AreNotEqual("passed", assessed.Status);
            Assert.AreNotEqual("complete", assessed.Disposition?.Kind);
            Assert.AreEqual("blocked", assessed.Gates.Single(gate => gate.Id == "state-closure").Status);
        }
    }

    [TestMethod]
    [DataRow("object")]
    [DataRow("compact-string")]
    [DataRow("legacy-absent")]
    public async Task TrustedClosure_GenuineElementMemberAndRuntimeSlugAreAccepted(string shape)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var source = PropertyObservation("IsSelected", "True", "tab-source-a1b2");
        var target = JsonNode.Parse(PropertyObservation("IsSelected", "True", "tab-target-c3d4"))!;
        if (shape == "compact-string") target["element"] = "tab-target-c3d4";
        if (shape == "legacy-absent") target.AsObject().Remove("element");
        await PlanPropertyAsync(fixture, "isSelected", source);
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(0, target.ToJsonString(), ""),
            arguments: ["get-property", "main", "-p", "IsSelected", "-w", "42", "--json"]);

        await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        Assert.AreEqual("passed", (await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared)).Status);
    }

    [TestMethod]
    [DataRow("unknown-root")]
    [DataRow("unknown-element")]
    [DataRow("element-null")]
    [DataRow("element-array")]
    [DataRow("element-string-mismatch")]
    [DataRow("children-object")]
    [DataRow("children-string")]
    [DataRow("ancestor-object")]
    [DataRow("ancestor-number")]
    [DataRow("wrong-selector")]
    [DataRow("bounds-string")]
    [DataRow("boolean-string")]
    [DataRow("boolean-null")]
    public async Task TrustedClosure_RejectsMalformedElementMembersAndVerdictFields(string malformed)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var source = PropertyObservation("IsSelected", "True");
        await PlanPropertyAsync(fixture, "isSelected", source);
        var node = JsonNode.Parse(source)!;
        switch (malformed)
        {
            case "unknown-root": node["verified"] = true; break;
            case "unknown-element": node["element"]!["passed"] = true; break;
            case "element-null": node["element"] = null; break;
            case "element-array": node["element"] = new JsonArray(); break;
            case "element-string-mismatch": node["element"] = "other-id"; break;
            case "children-object": node["element"]!["children"] = new JsonObject(); break;
            case "children-string": node["element"]!["children"] = new JsonArray("fake element"); break;
            case "ancestor-object": node["element"]!["ancestorPath"] = new JsonObject(); break;
            case "ancestor-number": node["element"]!["ancestorPath"] = new JsonArray(42); break;
            case "wrong-selector": node["element"]!["selector"] = "other-id"; break;
            case "bounds-string": node["element"]!["width"] = "wide"; break;
            case "boolean-string": node["element"]!["isEnabled"] = "True"; break;
            case "boolean-null": node["element"]!["isEnabled"] = null; break;
        }
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(0, node.ToJsonString(), ""),
            arguments: ["get-property", "main", "-p", "IsSelected", "-w", "42", "--json"]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));
    }

    private static void ForgeSeal<T>(T value, string domain, ECDsa key, Action<string?> seal, Action<string> publicKey)
    {
        seal(null);
        publicKey(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        seal(Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(domain + "\n" +
            JsonSerializer.Serialize(value, EnforcementStore.JsonOptions)), HashAlgorithmName.SHA256)));
    }

    private static async Task RefreshCanonicalOperationAsync(RunnerFixture fixture, string operationId, string path)
    {
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        var operation = progress.Operations.Single(item => item.Id == operationId);
        operation.ArtifactSha256 = StateClosureProtocol.FileHash(fixture.Root, path);
        StateClosureSeal.Sign(operation);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, progress);
    }

    private sealed class LiveClosureOwner : IWindowOwnerResolver
    {
        internal int ProcessId { get; set; } = 4242;
        internal DateTimeOffset StartedAt { get; set; } = TestWindowOwnerResolver.ProcessStartedAt;
        internal bool WindowExists { get; set; } = true;
        internal bool Available { get; set; } = true;
        public bool TryGetProcessId(long windowHandle, out int processId)
        {
            processId = ProcessId;
            return WindowExists && windowHandle == TestWindowOwnerResolver.WindowHandle;
        }
        public bool TryGetProcessIdentity(int processId, out ProcessIdentity identity)
        {
            identity = new ProcessIdentity(processId, StartedAt);
            return Available;
        }
    }
}
