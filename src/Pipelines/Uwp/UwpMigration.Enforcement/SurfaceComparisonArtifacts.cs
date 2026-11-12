// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UwpMigration.Mechanical;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

internal static class SurfaceComparisonArtifacts
{
    internal const string IndexPath = ".migration-evidence/comparison/index.html";
    private const string ReceiptName = "comparison-capture.json";

    internal static async Task CaptureAsync(
        DirectoryInfo target, IEnforcementProcessRunner runner, IWindowOwnerResolver resolver,
        string executable, long window, string directory, string treePath, string side,
        string? stateId, string fingerprint, string? runId, CancellationToken cancellationToken)
    {
        var receipt = new ComparisonCapture
        {
            Side = side, StateId = stateId, Fingerprint = fingerprint, RunId = runId,
            TreePath = treePath, WindowHandle = window, CapturedAt = DateTimeOffset.UtcNow
        };
        try
        {
            receipt.TreeSha256 = StateClosureProtocol.FileHash(target, treePath);
            var relative = $"{directory}/comparison.png";
            if (!EnforcementStore.TryResolveContainedPath(target, relative, out var output, out var error))
                throw new InvalidDataException(error);
            if (File.Exists(output))
                throw new InvalidDataException("Refusing to reuse an existing screenshot.");
            if (!resolver.TryGetOwnerIdentity(window, out var owner))
                throw new InvalidDataException("The capture window no longer has a current process identity.");
            receipt.ProcessId = owner.ProcessId;
            receipt.ProcessStartedAt = owner.StartedAt;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await runner.RunAsync(executable, target,
                ["ui", "screenshot", "-w", window.ToString(CultureInfo.InvariantCulture), "-o", output],
                timeout.Token);
            if (result.ExitCode != 0 || result.InfrastructureFailure)
                throw new IOException("The optional screenshot command did not succeed.");
            if (!resolver.TryGetOwnerIdentity(window, out var after) || owner != after)
                throw new InvalidDataException("The capture window process changed during the screenshot.");
            if (!File.Exists(output) || PngEvidence.Classify(output, timeout.Token) == PngInformation.Invalid)
                throw new InvalidDataException("The optional screenshot did not produce a valid fresh PNG.");
            receipt.ScreenshotPath = relative;
            receipt.ScreenshotSha256 = StateClosureProtocol.FileHash(target, relative);
            receipt.Status = "saved";
        }
        catch (Exception exception) when (OptionalFailure(exception, cancellationToken))
        {
            receipt.Reason = exception is OperationCanceledException
                ? "Optional screenshot timed out." : exception.Message;
        }
        try
        {
            await EnforcementStore.WriteAtomicAsync(target, $"{directory}/{ReceiptName}", receipt, cancellationToken);
        }
        catch (Exception exception) when (OptionalFailure(exception, cancellationToken))
        {
            EnforcementTrace.Mark("comparison.capture-unavailable");
        }
    }

    internal static async Task WriteIndexAsync(
        DirectoryInfo target, EnforcementSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            var report = await MigrationReportStore.LoadAsync(
                Path.Combine(target.FullName, "migration-report.json"), cancellationToken);
            var (plan, _) = await EnforcementStore.TryLoadAsync<StatePlanDocument>(
                target, report.Validation.StatePlan, cancellationToken);
            if (plan is null)
                return;
            var captures = new List<ComparisonCapture>();
            foreach (var root in new[] { report.Validation.SourceBaseline.EvidenceRoot, EnforcementStore.EnforcementDirectory })
            {
                if (!EnforcementStore.TryResolveContainedPath(target, root, out var fullRoot, out _) ||
                    !Directory.Exists(fullRoot))
                    continue;
                foreach (var file in Directory.EnumerateFiles(fullRoot, ReceiptName,
                    new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    var (capture, _) = await EnforcementStore.TryLoadAsync<ComparisonCapture>(
                        target, Path.GetRelativePath(target.FullName, file), cancellationToken);
                    if (capture is not null)
                        captures.Add(capture);
                }
            }
            var html = new StringBuilder("""
                <!doctype html><html lang="en"><meta charset="utf-8">
                <title>Migration surface comparisons</title>
                <style>body{font:15px system-ui;margin:2em}table{width:100%;table-layout:fixed}td{vertical-align:top;padding:1em;border:1px solid #ccc}img{max-width:100%;height:auto}code{overflow-wrap:anywhere}</style>
                <h1>Migration surface comparisons</h1>
                <p>Screenshots are advisory, not acceptance proof. They are associated with the exact captured UIA bytes. Missing, stale, or ambiguous images are not replaced with baseline images.</p>
                """);
            html.Append("<p>Assessment: ").Append(E(snapshot.Status)).Append(" at ")
                .Append(E(snapshot.GeneratedAt.ToString("O", CultureInfo.InvariantCulture)))
                .Append("</p><p>Target fingerprint: <code>").Append(E(snapshot.TargetFingerprint)).Append("</code></p>");
            foreach (var state in plan.States)
            {
                var result = snapshot.StateVerifications.SingleOrDefault(item => item.Id == state.Id);
                html.Append("<h2>").Append(E(state.Id)).Append("</h2><p>")
                    .Append(E(state.Disposition == "excluded" ? $"excluded: {state.ExclusionReason}" :
                        $"{result?.Result ?? "not-run"}: {result?.Reason}"))
                    .Append("</p><table><tr><th>Before (UWP)</th><th>After (WinUI)</th></tr><tr>");
                AddSide("source", state.Source.TreeEvidence);
                AddSide("target", state.Target.TreeEvidence);
                html.Append("</tr></table>");

                void AddSide(string side, string? tree)
                {
                    html.Append("<td>");
                    if (tree is null || !EnforcementStore.TryResolveContainedPath(target, tree, out var treeFile, out _) ||
                        !File.Exists(treeFile))
                    {
                        html.Append("UIA evidence not available; screenshot not paired.</td>");
                        return;
                    }
                    var hash = StateClosureProtocol.FileHash(target, tree);
                    html.Append("<a href=\"").Append(Link(tree)).Append("\">UIA tree</a>");
                    var matches = captures.Where(capture => capture.Side == side && capture.TreeSha256 == hash &&
                        (side == "source" || capture.StateId == state.Id &&
                         capture.Fingerprint == snapshot.TargetFingerprint && capture.RunId == snapshot.Replay?.Id)).ToArray();
                    var exact = matches.Where(capture => capture.TreePath == tree).ToArray();
                    var candidates = exact.Length > 0 ? exact : matches;
                    if (candidates.Length != 1)
                    {
                        html.Append("<p>Screenshot missing or ambiguous.</p></td>");
                        return;
                    }
                    var capture = candidates[0];
                    if (capture.Status == "saved" && capture.ScreenshotPath is { } image &&
                        EnforcementStore.TryResolveContainedPath(target, image, out var fullImage, out _) &&
                        File.Exists(fullImage) && StateClosureProtocol.FileHash(target, image) == capture.ScreenshotSha256)
                        html.Append("<p><a href=\"").Append(Link(image)).Append("\"><img alt=\"")
                            .Append(E(side)).Append(" screenshot\" src=\"").Append(Link(image)).Append("\"></a></p>");
                    else
                        html.Append("<p>Screenshot unavailable or stale: ").Append(E(capture.Reason)).Append("</p>");
                    html.Append("<p>Captured: ").Append(E(capture.CapturedAt.ToString("O", CultureInfo.InvariantCulture)))
                        .Append("<br>Run: <code>").Append(E(capture.RunId))
                        .Append("</code><br>Code: <code>").Append(E(capture.Fingerprint)).Append("</code></p></td>");
                }
            }
            html.Append("</html>");
            await EnforcementStore.WriteTextAtomicAsync(target, IndexPath, html.ToString(), cancellationToken);

            string Link(string path)
            {
                if (!EnforcementStore.TryResolveContainedPath(target, path, out var full, out var error))
                    throw new InvalidDataException(error);
                var indexDirectory = Path.Combine(target.FullName, ".migration-evidence", "comparison");
                return string.Join("/", Path.GetRelativePath(indexDirectory, full).Split(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Select(Uri.EscapeDataString));
            }
        }
        catch (Exception exception) when (OptionalFailure(exception, cancellationToken))
        {
            EnforcementTrace.Mark("comparison.index-unavailable");
        }
    }

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static bool OptionalFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or JsonException or ArgumentException ||
        exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    internal sealed class ComparisonCapture
    {
        public ComparisonCapture() { }

        [JsonPropertyName("side")] public string Side { get; set; } = "";
        [JsonPropertyName("stateId")] public string? StateId { get; set; }
        [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = "";
        [JsonPropertyName("runId")] public string? RunId { get; set; }
        [JsonPropertyName("treePath")] public string TreePath { get; set; } = "";
        [JsonPropertyName("treeSha256")] public string? TreeSha256 { get; set; }
        [JsonPropertyName("windowHandle")] public long WindowHandle { get; set; }
        [JsonPropertyName("processId")] public int? ProcessId { get; set; }
        [JsonPropertyName("processStartedAt")] public DateTimeOffset? ProcessStartedAt { get; set; }
        [JsonPropertyName("capturedAt")] public DateTimeOffset CapturedAt { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; } = "missing";
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        [JsonPropertyName("screenshotPath")] public string? ScreenshotPath { get; set; }
        [JsonPropertyName("screenshotSha256")] public string? ScreenshotSha256 { get; set; }
    }
}
