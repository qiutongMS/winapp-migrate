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
                using var localKey = CngKey.Open(KeyName);
                using var local = new ECDsaCng(localKey);
                var trusted = local.ExportSubjectPublicKeyInfo();
                if (!CryptographicOperations.FixedTimeEquals(trusted, bytes)) return false;
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
