// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinAppMigration.Enforcement;

internal static class StateClosureSeal
{
    private const string KeyName = "WinAppMigration.StateClosure.v1";
    private static readonly Lock KeyLock = new();
    private sealed record VerificationContext(byte[]? PublicKey)
    {
        internal Dictionary<string, string?> Anchors { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, bool> Histories { get; } = new(StringComparer.Ordinal);
    }
    private static readonly AsyncLocal<VerificationContext?> Verification = new();
    private sealed class VerificationScope(VerificationContext? previous) : IDisposable
    {
        public void Dispose() => Verification.Value = previous;
    }

    internal static IDisposable BeginVerification()
    {
        var previous = Verification.Value;
        Verification.Value ??= new VerificationContext(ReadTrustedPublicKey());
        return new VerificationScope(previous);
    }

    internal static bool VerifyHistory(string digest, Func<bool> verify)
    {
        var context = Verification.Value;
        if (context is null) return verify();
        if (context.Histories.TryGetValue(digest, out var valid)) return valid;
        valid = verify();
        context.Histories.Add(digest, valid);
        return valid;
    }

    private static byte[]? ReadTrustedPublicKey()
    {
        try
        {
            using var key = CngKey.Open(KeyName);
            using var local = new ECDsaCng(key);
            return local.ExportSubjectPublicKeyInfo();
        }
        catch (CryptographicException) { return null; }
    }

    internal static void Sign(UiDevelopmentProbe probe)
    {
        WithSigner(signer =>
        {
            probe.Seal = null;
            probe.SealPublicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
            probe.Seal = CreateSeal(signer, "ui-probe\n" + JsonSerializer.Serialize(probe, EnforcementStore.JsonOptions));
        });
    }

    internal static void Sign(StateClosureAttestation attestation)
    {
        WithSigner(signer =>
        {
            attestation.Seal = null;
            attestation.SealPublicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
            attestation.Seal = CreateSeal(signer, "state-closure\n" + JsonSerializer.Serialize(attestation, EnforcementStore.JsonOptions));
        });
    }

    internal static bool Verify(UiDevelopmentProbe probe)
        => VerifyProbe(probe, portable: false);

    internal static bool VerifyPortableIntegrity(UiDevelopmentProbe probe)
        => VerifyProbe(probe, portable: true);

    private static bool VerifyProbe(UiDevelopmentProbe probe, bool portable)
    {
        var seal = probe.Seal;
        try
        {
            probe.Seal = null;
            return VerifyPayload("ui-probe\n" + JsonSerializer.Serialize(probe, EnforcementStore.JsonOptions), seal, probe.SealPublicKey, portable);
        }
        finally { probe.Seal = seal; }
    }

    internal static bool Verify(StateClosureAttestation attestation)
        => VerifyAttestation(attestation, portable: false);

    internal static bool VerifyPortableIntegrity(StateClosureAttestation attestation)
        => VerifyAttestation(attestation, portable: true);

    private static bool VerifyAttestation(StateClosureAttestation attestation, bool portable)
    {
        var seal = attestation.Seal;
        try
        {
            attestation.Seal = null;
            return VerifyPayload("state-closure\n" + JsonSerializer.Serialize(attestation, EnforcementStore.JsonOptions), seal, attestation.SealPublicKey, portable);
        }
        finally { attestation.Seal = seal; }
    }

    internal static void Sign(WorkflowOperationRecord operation)
    {
        WithSigner(signer =>
        {
            operation.Seal = null;
            operation.SealPublicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
            operation.Seal = CreateSeal(signer, "operation\n" + JsonSerializer.Serialize(operation, EnforcementStore.JsonOptions));
        });
    }

    internal static bool Verify(WorkflowOperationRecord operation)
    {
        var seal = operation.Seal;
        try
        {
            operation.Seal = null;
            return VerifyPayload("operation\n" + JsonSerializer.Serialize(operation, EnforcementStore.JsonOptions), seal, operation.SealPublicKey);
        }
        finally { operation.Seal = seal; }
    }

    internal static void Sign(BuildAttestation attestation)
    {
        WithSigner(signer =>
        {
            attestation.Seal = null;
            attestation.SealPublicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
            attestation.Seal = CreateSeal(signer, "build\n" + JsonSerializer.Serialize(attestation, EnforcementStore.JsonOptions));
        });
    }

    internal static bool Verify(BuildAttestation attestation)
    {
        var seal = attestation.Seal;
        try
        {
            attestation.Seal = null;
            return VerifyPayload("build\n" + JsonSerializer.Serialize(attestation, EnforcementStore.JsonOptions), seal, attestation.SealPublicKey);
        }
        finally { attestation.Seal = seal; }
    }

    internal static void Sign(ReplayAttestation attestation)
    {
        WithSigner(signer =>
        {
            attestation.Seal = null;
            attestation.SealPublicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
            attestation.Seal = CreateSeal(signer, "replay\n" + JsonSerializer.Serialize(attestation, EnforcementStore.JsonOptions));
        });
    }

    internal static bool Verify(ReplayAttestation attestation)
    {
        var seal = attestation.Seal;
        try
        {
            attestation.Seal = null;
            return VerifyPayload("replay\n" + JsonSerializer.Serialize(attestation, EnforcementStore.JsonOptions), seal, attestation.SealPublicKey);
        }
        finally { attestation.Seal = seal; }
    }

    private static void WithSigner(Action<ECDsa> sign)
    {
        try
        {
            using var key = OpenKey();
            using var signer = new ECDsaCng(key);
            sign(signer);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("The Windows user state-closure signing key could not be created or used.", exception);
        }
    }

    internal static void Sign(RequiredStateContractDocument document)
    {
        WithSigner(signer =>
        {
            document.Seal = null;
            document.SealPublicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
            document.Seal = CreateSeal(signer, "required-contract\n" + JsonSerializer.Serialize(document, EnforcementStore.JsonOptions));
        });
    }

    internal static void Sign(DiagnosticRunAttestation value)
    {
        WithSigner(signer =>
        {
            value.Seal = null;
            value.SealPublicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
            value.Seal = CreateSeal(signer, "diagnostic\n" + JsonSerializer.Serialize(value, EnforcementStore.JsonOptions));
        });
    }

    internal static bool Verify(DiagnosticRunAttestation value)
    {
        var seal = value.Seal;
        try
        {
            value.Seal = null;
            return VerifyPayload("diagnostic\n" + JsonSerializer.Serialize(value, EnforcementStore.JsonOptions), seal, value.SealPublicKey);
        }
        finally { value.Seal = seal; }
    }

    internal static bool Verify(RequiredStateContractDocument document)
    {
        var seal = document.Seal;
        try
        {
            document.Seal = null;
            return VerifyPayload("required-contract\n" + JsonSerializer.Serialize(document, EnforcementStore.JsonOptions), seal, document.SealPublicKey);
        }
        finally { document.Seal = seal; }
    }

    internal static string? ReadContractAnchor(string name)
    {
        if (Verification.Value?.Anchors.TryGetValue(name, out var cached) == true) return cached;
        lock (KeyLock)
        {
            string? value = null;
            var anchorName = "WinAppMigration.Anchor." + name;
            if (CngKey.Exists(anchorName))
            {
                using var anchor = CngKey.Open(anchorName);
                value = Encoding.UTF8.GetString(anchor.GetProperty("state", CngPropertyOptions.CustomProperty).GetValue()!);
            }
            else if (CngKey.Exists(KeyName))
            {
                using var legacy = CngKey.Open(KeyName);
                if (legacy.HasProperty(name, CngPropertyOptions.CustomProperty))
                    value = Encoding.UTF8.GetString(legacy.GetProperty(name, CngPropertyOptions.CustomProperty).GetValue()!);
            }
            if (Verification.Value is { } context) context.Anchors[name] = value;
            return value;
        }
    }

    internal static void WriteContractAnchor(string name, string value)
    {
        lock (KeyLock)
        {
            var anchorName = "WinAppMigration.Anchor." + name;
            using var key = CngKey.Exists(anchorName) ? CngKey.Open(anchorName) :
                CngKey.Create(CngAlgorithm.ECDsaP256, anchorName, new CngKeyCreationParameters
                {
                    ExportPolicy = CngExportPolicies.None,
                    KeyUsage = CngKeyUsages.Signing
                });
            // Separate anchor handles prevent one workspace's persisted property bag overwriting another's.
            key.SetProperty(new CngProperty("state", Encoding.UTF8.GetBytes(value),
                CngPropertyOptions.CustomProperty | CngPropertyOptions.Persist));
            if (Verification.Value is { } context) context.Anchors[name] = value;
        }
    }

    private static string CreateSeal(ECDsa signer, string payload) =>
        Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256));

    private static bool VerifyPayload(string payload, string? seal, string? publicKey, bool portable = false)
    {
        if (string.IsNullOrEmpty(seal) || string.IsNullOrEmpty(publicKey)) return false;
        try
        {
            using var verifier = ECDsa.Create();
            var bytes = Convert.FromBase64String(publicKey);
            if (!portable)
            {
                // This lookup is independent of every artifact and never creates a key while verifying.
                var trusted = Verification.Value is { } context ? context.PublicKey : ReadTrustedPublicKey();
                if (trusted is null || !CryptographicOperations.FixedTimeEquals(trusted, bytes)) return false;
                bytes = trusted;
            }
            verifier.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length) return false;
            return verifier.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(seal), HashAlgorithmName.SHA256);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private static CngKey OpenKey()
    {
        lock (KeyLock)
        {
            if (CngKey.Exists(KeyName))
                return CngKey.Open(KeyName);
            try
            {
                return CngKey.Create(CngAlgorithm.ECDsaP256, KeyName, new CngKeyCreationParameters
                {
                    ExportPolicy = CngExportPolicies.None,
                    KeyUsage = CngKeyUsages.Signing
                });
            }
            catch (CryptographicException) when (CngKey.Exists(KeyName))
            {
                return CngKey.Open(KeyName);
            }
        }
    }
}
