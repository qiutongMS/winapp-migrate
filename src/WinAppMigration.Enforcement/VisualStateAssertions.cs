// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinAppMigration.Enforcement;

internal static class VisualStateAssertions
{
    internal sealed record Result(bool TargetSufficient, bool ParityVerified, string Reason);
    private readonly record struct Rectangle(double Left, double Top, double Right, double Bottom)
    {
        // Only pixels whose centers belong to the semantic region count as content.
        internal int X => (int)Math.Ceiling(Left - 0.5);
        internal int Y => (int)Math.Ceiling(Top - 0.5);
        internal int Width => (int)Math.Ceiling(Right - 0.5) - X;
        internal int Height => (int)Math.Ceiling(Bottom - 0.5) - Y;
    }
    private sealed record CaptureFrame(long Hwnd, int ProcessId, double Left, double Top, double Right, double Bottom,
        double PixelLeft, double PixelTop, double PixelRight, double PixelBottom);

    internal static Result Compare(DirectoryInfo root, MigrationState state, StateAssertion assertion,
        IReadOnlyList<UiDevelopmentProbe> probes, IReadOnlyList<WorkflowOperationRecord> operations)
    {
        if (assertion.ProbeIndex < 0 || assertion.ProbeIndex >= probes.Count ||
            probes[assertion.ProbeIndex].Command[2] != "screenshot" ||
            !state.Source.Evidence.Contains(assertion.SourceEvidence, StringComparer.Ordinal) ||
            assertion.Comparison is not ("pixel-exact" or "sampled-mae") ||
            !double.IsFinite(assertion.MaximumMeanError) || assertion.MaximumMeanError is < 0 or > 0.10)
            throw new InvalidDataException($"Visual assertion {assertion.Id} lacks a supported bound screenshot/comparison.");
        var target = PngEvidence.ReadRaster(Resolve(root, probes[assertion.ProbeIndex].ScreenshotPath!));
        if (target is null) return new(false, false, $"Visual assertion {assertion.Id}: target PNG is invalid or valid-unclassified, not visual proof.");
        var targetRegion = TargetRegion(root, assertion, probes, target, operations);
        if (!Sufficient(target, targetRegion, assertion.Sufficiency ?? new VisualSufficiency()))
            return new(false, false, $"Visual assertion {assertion.Id}: semantic ROI is blank/low-information, regardless of informative window chrome.");
        var source = PngEvidence.ReadRaster(Resolve(root, assertion.SourceEvidence));
        if (source is null) return new(true, false, $"Visual assertion {assertion.Id}: source PNG is not analyzable; supplemental evidence cannot prove parity.");
        Rectangle sourceRegion;
        if ((assertion.SourceRegion ?? assertion.Region) is { } explicitSource)
            sourceRegion = Explicit(explicitSource, source);
        else
        {
            if (assertion.BoundsEvidence is null || !state.Source.Evidence.Contains(assertion.BoundsEvidence, StringComparer.Ordinal) ||
                assertion.SourceCaptureEvidence is null || !state.Source.Evidence.Contains(assertion.SourceCaptureEvidence, StringComparer.Ordinal))
                throw new InvalidDataException("A visual assertion requires source ROI bounds evidence or an explicit source region.");
            sourceRegion = FromTree(root, assertion.BoundsEvidence, assertion.AutomationId, source,
                ReadCapture(root, assertion.SourceCaptureEvidence, source, null, null, requireCoordinates: true));
        }
        if (!Sufficient(source, sourceRegion, assertion.Sufficiency ?? new VisualSufficiency()))
            return new(true, false, $"Visual assertion {assertion.Id}: the source semantic ROI is low-information and cannot authorize visual parity.");
        var equal = Equal(source, sourceRegion, target, targetRegion, assertion);
        return new(true, equal, equal ? $"Visual assertion {assertion.Id} meets the contracted ROI comparison." :
            $"Visual assertion {assertion.Id}: source/target semantic regions differ.");
    }

    internal static bool Restored(DirectoryInfo root, StateAssertion baseline, StateAssertion restored,
        IReadOnlyList<UiDevelopmentProbe> probes, IReadOnlyList<WorkflowOperationRecord> operations)
    {
        var first = PngEvidence.ReadRaster(Resolve(root, probes[baseline.ProbeIndex].ScreenshotPath!));
        var last = PngEvidence.ReadRaster(Resolve(root, probes[restored.ProbeIndex].ScreenshotPath!));
        return first is not null && last is not null &&
            Equal(first, TargetRegion(root, baseline, probes, first, operations),
                last, TargetRegion(root, restored, probes, last, operations), restored);
    }

    private static Rectangle TargetRegion(DirectoryInfo root, StateAssertion assertion,
        IReadOnlyList<UiDevelopmentProbe> probes, PngEvidence.Raster raster, IReadOnlyList<WorkflowOperationRecord> operations)
    {
        var image = probes[assertion.ProbeIndex];
        var capture = ReadCapture(root, image.StandardOutputPath, raster, image.WindowHandle, image.WindowProcessId,
            requireCoordinates: assertion.Region is null);
        if (assertion.Region is { } region) return Explicit(region, raster);
        if (assertion.BoundsProbeIndex is not { } index || index < 0 || index >= probes.Count ||
            index >= assertion.ProbeIndex || probes[index].Command[2] != "inspect" ||
            probes[index].WindowHandle != image.WindowHandle || probes[index].WindowProcessId != image.WindowProcessId ||
            probes[index].WindowProcessStartedAt != image.WindowProcessStartedAt)
            throw new InvalidDataException("Visual ROI must be explicitly contracted or bound to a preceding inspect probe.");
        for (var between = index + 1; between < assertion.ProbeIndex; between++)
            if (probes[between].Command[2] != "get-property")
                throw new InvalidDataException("Stale ROI bounds: re-inspect after another capture, geometry observation, or state-changing action.");
        var before = FromTree(root, probes[index].StandardOutputPath, assertion.AutomationId, raster, capture);
        if (assertion.BoundsAfterProbeIndex is not { } after || after <= assertion.ProbeIndex || after >= probes.Count ||
            probes[after].Command[2] != "inspect" || probes[after].WindowHandle != image.WindowHandle ||
            probes[after].WindowProcessId != image.WindowProcessId || probes[after].WindowProcessStartedAt != image.WindowProcessStartedAt)
            throw new InvalidDataException("UIA-derived regions require an authenticated same-window post-capture bounds observation.");
        var start = operations.Single(operation => operation.Id == probes[index].OperationId).Sequence;
        var middle = operations.Single(operation => operation.Id == image.OperationId).Sequence;
        var end = operations.Single(operation => operation.Id == probes[after].OperationId).Sequence;
        if (start >= middle || middle >= end || operations.Any(operation =>
            operation.Sequence > start && operation.Sequence < end && operation.Id != image.OperationId))
            throw new InvalidDataException("Stale ROI bounds: complete authenticated operation history contains intervening activity, including omitted receipts.");
        for (var between = assertion.ProbeIndex + 1; between < after; between++)
            if (probes[between].Command[2] != "get-property")
                throw new InvalidDataException("Stale ROI bounds: intervening activity invalidates the post-capture geometry witness.");
        var observedAfter = FromTree(root, probes[after].StandardOutputPath, assertion.AutomationId, raster, capture);
        if (before != observedAfter)
            throw new InvalidDataException("Stale semantic ROI: element bounds changed across the screenshot capture.");
        return before;
    }

    private static Rectangle Explicit(VisualRegion region, PngEvidence.Raster raster)
    {
        if (!double.IsFinite(region.X) || !double.IsFinite(region.Y) || !double.IsFinite(region.Width) || !double.IsFinite(region.Height) ||
            region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            region.X + region.Width > 1 || region.Y + region.Height > 1)
            throw new InvalidDataException("Explicit visual regions use normalized, contained nonempty image coordinates.");
        return new Rectangle(region.X * raster.Width, region.Y * raster.Height,
            (region.X + region.Width) * raster.Width, (region.Y + region.Height) * raster.Height);
    }

    private static Rectangle FromTree(DirectoryInfo root, string path, string automationId, PngEvidence.Raster raster, CaptureFrame capture)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Resolve(root, path)), new JsonDocumentOptions { AllowDuplicateProperties = false });
        var matches = new List<(JsonElement Element, JsonElement Window, JsonElement Owner)>();
        Collect(document.RootElement, default, default, automationId, matches);
        if (matches.Count != 1 || matches[0].Window.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("ROI bounds must identify exactly one UIA element inside one window.");
        var (element, window, owner) = matches[0];
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty("hwnd", out var handle) ||
            handle.ValueKind != JsonValueKind.Number || !handle.TryGetInt64(out var hwnd) || hwnd != capture.Hwnd ||
            owner.TryGetProperty("processId", out var process) &&
                (process.ValueKind != JsonValueKind.Number || !process.TryGetInt32(out var pid) || pid != capture.ProcessId) ||
            owner.TryGetProperty("coordinateSpace", out var space) &&
                (space.ValueKind != JsonValueKind.String || space.GetString() != "screen-physical-pixels"))
            throw new InvalidDataException("ROI bounds window/coordinate ownership does not match the capture/replay.");
        if (element.TryGetProperty("windowHandle", out var elementHandle) && elementHandle.ValueKind != JsonValueKind.Null &&
            (elementHandle.ValueKind != JsonValueKind.Number || !elementHandle.TryGetInt64(out var elementHwnd) || elementHwnd != capture.Hwnd))
            throw new InvalidDataException("ROI element belongs to a different window.");
        var wx = Number(window, "x"); var wy = Number(window, "y");
        var ww = Number(window, "width"); var wh = Number(window, "height");
        if (ww <= 0 || wh <= 0 || Math.Abs(wx - capture.Left) > 1 || Math.Abs(wy - capture.Top) > 1 ||
            Math.Abs(wx + ww - capture.Right) > 1 || Math.Abs(wy + wh - capture.Bottom) > 1)
            throw new InvalidDataException("Stale ROI window geometry differs from the screenshot's physical capture bounds.");
        var ex = Number(element, "x"); var ey = Number(element, "y");
        var ew = Number(element, "width"); var eh = Number(element, "height");
        if (ew <= 0 || eh <= 0 || ex < wx || ey < wy || ex + ew > wx + ww || ey + eh > wy + wh)
            throw new InvalidDataException("UIA semantic ROI is outside the replay window.");
        var sx = (capture.PixelRight - capture.PixelLeft) / ww;
        var sy = (capture.PixelBottom - capture.PixelTop) / wh;
        var x = capture.PixelLeft + (ex - wx) * sx;
        var y = capture.PixelTop + (ey - wy) * sy;
        var right = capture.PixelLeft + (ex - wx + ew) * sx;
        var bottom = capture.PixelTop + (ey - wy + eh) * sy;
        if (x < 0 || y < 0 || right > raster.Width || bottom > raster.Height || right <= x || bottom <= y)
            throw new InvalidDataException("UIA semantic ROI is outside the screenshot/window.");
        return new Rectangle(x, y, right, bottom);
    }

    private static void Collect(JsonElement value, JsonElement window, JsonElement owner,
        string id, List<(JsonElement, JsonElement, JsonElement)> matches)
    {
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) Collect(child, window, owner, id, matches);
        else if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("hwnd", out _) && value.TryGetProperty("elements", out _)) owner = value;
            if (value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "Window") window = value;
            if (value.TryGetProperty("automationId", out var candidate) && candidate.ValueKind == JsonValueKind.String && candidate.GetString() == id)
                matches.Add((value, window, owner));
            foreach (var field in new[] { "windows", "elements", "children" })
                if (value.TryGetProperty(field, out var children)) Collect(children, window, owner, id, matches);
        }
    }

    private static double Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) && double.IsFinite(number) ? number :
        throw new InvalidDataException($"UIA bounds require a finite {property}.");

    private static bool Sufficient(PngEvidence.Raster raster, Rectangle region, VisualSufficiency policy)
    {
        if (region.Width < 2 || region.Height < 2 || region.Width * region.Height < policy.MinimumSamplePixels) return false;
        var histogram = new Dictionary<uint, int>();
        for (var y = region.Y; y < region.Y + region.Height; y++)
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                var color = raster.Pixels[y * raster.Width + x] & 0x00f0f0f0;
                histogram[color] = histogram.GetValueOrDefault(color) + 1;
            }
        return histogram.Count >= policy.MinimumDistinctColors &&
            histogram.Values.Max() <= (long)region.Width * region.Height * (1 - policy.MinimumContentFraction);
    }

    private static CaptureFrame ReadCapture(DirectoryInfo root, string path, PngEvidence.Raster raster,
        long? expectedHwnd, int? expectedPid, bool requireCoordinates)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Resolve(root, path)), new JsonDocumentOptions { AllowDuplicateProperties = false });
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("hwnd", out var handle) || handle.ValueKind != JsonValueKind.Number || !handle.TryGetInt64(out var hwnd) || hwnd <= 0 ||
            !value.TryGetProperty("processId", out var process) || process.ValueKind != JsonValueKind.Number || !process.TryGetInt32(out var pid) || pid <= 0 ||
            expectedHwnd is not null && hwnd != expectedHwnd || expectedPid is not null && pid != expectedPid ||
            Number(value, "width") != raster.Width || Number(value, "height") != raster.Height)
            throw new InvalidDataException("Visual capture metadata dimensions/owner do not match the bound screenshot/replay.");
        if (value.TryGetProperty("windows", out var windows) && windows.ValueKind != JsonValueKind.Null &&
            (windows.ValueKind != JsonValueKind.Array || windows.GetArrayLength() > 1))
            throw new InvalidDataException("Composite captures cannot supply a single-window semantic ROI.");
        if (!value.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind == JsonValueKind.Null)
        {
            if (requireCoordinates)
                throw new InvalidDataException("UIA-bound ROI needs physical sourceBounds/contentRect capture metadata; use normalized explicit regions for legacy captures.");
            return new CaptureFrame(hwnd, pid, 0, 0, raster.Width, raster.Height, 0, 0, raster.Width, raster.Height);
        }
        if (coordinates.ValueKind != JsonValueKind.Object ||
            !coordinates.TryGetProperty("space", out var space) || space.ValueKind != JsonValueKind.String || space.GetString() != "screen-physical-pixels" ||
            !coordinates.TryGetProperty("sourceBounds", out var source) || source.ValueKind != JsonValueKind.Object ||
            !coordinates.TryGetProperty("contentRect", out var content) || content.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Unknown visual capture coordinate mapping.");
        var result = new CaptureFrame(hwnd, pid, Number(source, "left"), Number(source, "top"),
            Number(source, "right"), Number(source, "bottom"), Number(content, "left"), Number(content, "top"),
            Number(content, "right"), Number(content, "bottom"));
        if (result.Right <= result.Left || result.Bottom <= result.Top || result.PixelLeft < 0 || result.PixelTop < 0 ||
            result.PixelRight > raster.Width || result.PixelBottom > raster.Height ||
            result.PixelRight <= result.PixelLeft || result.PixelBottom <= result.PixelTop)
            throw new InvalidDataException("Out-of-bounds or empty visual capture coordinate mapping.");
        return result;
    }

    private static bool Equal(PngEvidence.Raster source, Rectangle sr, PngEvidence.Raster target, Rectangle tr, StateAssertion assertion)
    {
        if (sr.Width <= 0 || sr.Height <= 0 || tr.Width <= 0 || tr.Height <= 0) return false;
        if (assertion.Comparison == "pixel-exact")
        {
            if (sr.Width != tr.Width || sr.Height != tr.Height) return false;
            for (var y = 0; y < sr.Height; y++)
                for (var x = 0; x < sr.Width; x++)
                    if (source.Pixels[(sr.Y + y) * source.Width + sr.X + x] != target.Pixels[(tr.Y + y) * target.Width + tr.X + x])
                        return false;
            return true;
        }
        double error = 0;
        const int grid = 64;
        for (var y = 0; y < grid; y++)
            for (var x = 0; x < grid; x++)
            {
                var ax = Math.Clamp((int)Math.Floor(sr.Left + (x + 0.5) * (sr.Right - sr.Left) / grid), sr.X, sr.X + sr.Width - 1);
                var ay = Math.Clamp((int)Math.Floor(sr.Top + (y + 0.5) * (sr.Bottom - sr.Top) / grid), sr.Y, sr.Y + sr.Height - 1);
                var bx = Math.Clamp((int)Math.Floor(tr.Left + (x + 0.5) * (tr.Right - tr.Left) / grid), tr.X, tr.X + tr.Width - 1);
                var by = Math.Clamp((int)Math.Floor(tr.Top + (y + 0.5) * (tr.Bottom - tr.Top) / grid), tr.Y, tr.Y + tr.Height - 1);
                var a = source.Pixels[ay * source.Width + ax];
                var b = target.Pixels[by * target.Width + bx];
                error += Math.Abs((int)(a >> 16) - (int)(b >> 16)) +
                    Math.Abs((int)((a >> 8) & 255) - (int)((b >> 8) & 255)) + Math.Abs((int)(a & 255) - (int)(b & 255));
            }
        return error / (grid * grid * 3 * 255) <= assertion.MaximumMeanError;
    }

    private static string Resolve(DirectoryInfo root, string path) =>
        EnforcementStore.TryResolveContainedPath(root, path, out var full, out var error) ? full : throw new InvalidDataException(error);
}
