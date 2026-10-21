// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using UwpMigration.Mechanical;

namespace UwpMigration.Mechanical.Tests;

[TestClass]
[DoNotParallelize]
public class MigrationReportStoreTests : BaseCommandTests
{
    [TestMethod]
    public async Task AcquireTransactionLock_EvidenceJunctionIsRejected()
    {
        var target = _tempDirectory.CreateSubdirectory("EvidenceJunctionTarget");
        var external = _tempDirectory.CreateSubdirectory("EvidenceJunctionExternal");
        var junction = Path.Combine(target.FullName, ".migration-evidence");
        CreateJunctionOrSkip(junction, external.FullName);

        try
        {
            var exception = await Assert.ThrowsExactlyAsync<MigrationReportLockException>(
                () => MigrationReportStore.AcquireTransactionLockAsync(
                    Path.Combine(target.FullName, "migration-report.json"),
                    TimeSpan.FromMilliseconds(250),
                    TestContext.CancellationToken));

            StringAssert.Contains(exception.Message, "reparse point");
            Assert.IsFalse(Directory.Exists(Path.Combine(external.FullName, ".locks")));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [TestMethod]
    public async Task AcquireTransactionLock_LocksJunctionIsRejected()
    {
        var target = _tempDirectory.CreateSubdirectory("LocksJunctionTarget");
        var evidence = target.CreateSubdirectory(".migration-evidence");
        var external = _tempDirectory.CreateSubdirectory("LocksJunctionExternal");
        var junction = Path.Combine(evidence.FullName, ".locks");
        CreateJunctionOrSkip(junction, external.FullName);

        try
        {
            var exception = await Assert.ThrowsExactlyAsync<MigrationReportLockException>(
                () => MigrationReportStore.AcquireTransactionLockAsync(
                    Path.Combine(target.FullName, "migration-report.json"),
                    TimeSpan.FromMilliseconds(250),
                    TestContext.CancellationToken));

            StringAssert.Contains(exception.Message, "reparse point");
            Assert.IsFalse(File.Exists(Path.Combine(
                external.FullName,
                "migration-report.json.lock")));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [TestMethod]
    public async Task AcquireTransactionLock_LockFileSymbolicLinkIsRejected()
    {
        var target = _tempDirectory.CreateSubdirectory("LockFileLinkTarget");
        var locks = target
            .CreateSubdirectory(".migration-evidence")
            .CreateSubdirectory(".locks");
        var externalFile = Path.Combine(
            _tempDirectory.FullName,
            "external-lock-target");
        await File.WriteAllTextAsync(
            externalFile,
            "sentinel",
            TestContext.CancellationToken);
        var lockPath = Path.Combine(
            locks.FullName,
            "migration-report.json.lock");
        try
        {
            File.CreateSymbolicLink(lockPath, externalFile);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            Assert.Inconclusive(
                $"File symbolic links are unavailable: {exception.Message}");
        }

        var lockException = await Assert.ThrowsExactlyAsync<MigrationReportLockException>(
            () => MigrationReportStore.AcquireTransactionLockAsync(
                Path.Combine(target.FullName, "migration-report.json"),
                TimeSpan.FromMilliseconds(250),
                TestContext.CancellationToken));

        StringAssert.Contains(lockException.Message, "reparse point");
        Assert.AreEqual(
            "sentinel",
            await File.ReadAllTextAsync(
                externalFile,
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AcquireTransactionLock_RemainsExclusiveAndReusable()
    {
        var target = _tempDirectory.CreateSubdirectory("ExclusiveLockTarget");
        var reportPath = Path.Combine(
            target.FullName,
            "migration-report.json");
        await using (await MigrationReportStore.AcquireTransactionLockAsync(
            reportPath,
            TimeSpan.FromSeconds(1),
            TestContext.CancellationToken))
        {
            var exception = await Assert.ThrowsExactlyAsync<MigrationReportLockException>(
                () => MigrationReportStore.AcquireTransactionLockAsync(
                    reportPath,
                    TimeSpan.FromMilliseconds(100),
                    TestContext.CancellationToken));
            StringAssert.Contains(exception.Message, "Timed out");
        }

        await using var reacquired =
            await MigrationReportStore.AcquireTransactionLockAsync(
                reportPath,
                TimeSpan.FromSeconds(1),
                TestContext.CancellationToken);
    }

    private static void CreateJunctionOrSkip(
        string junctionPath,
        string targetPath)
    {
        var startInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments =
                $"/d /c mklink /J \"{junctionPath}\" \"{targetPath}\""
        };

        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            Assert.Inconclusive(
                "Directory junctions are unavailable: " +
                string.Join(
                    Environment.NewLine,
                    new[] { standardOutput, standardError }
                        .Where(value => !string.IsNullOrWhiteSpace(value))));
        }
    }
}
