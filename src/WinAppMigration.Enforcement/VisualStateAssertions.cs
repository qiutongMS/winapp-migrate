// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinAppMigration.Enforcement;

internal static class VisualStateAssertions
{
    internal sealed record Result(bool TargetSufficient, bool ParityVerified, string Reason);
    private readonly record struct Rectangle(int X, int Y, int Width, int Height);

    internal static Result Compare(DirectoryInfo root, MigrationState state, StateAssertion assertion,
        IReadOnlyList<UiDevelopmentProbe> probes)
    {
        if (assertion.ProbeIndex < 0 || assertion.ProbeIndex >= probes.Count ||
            probes[assertion.ProbeIndex].Command[2] != "screenshot" ||
            !state.Source.Evidence.Contains(assertion.SourceEvidence, StringComparer.Ordinal) ||
            assertion.Comparison is not ("pixel-exact" or "sampled-mae") ||
            !double.IsFinite(assertion.MaximumMeanError) || assertion.MaximumMeanError is < 0 or > 0.10)
            throw new InvalidDataException($"Visual assertion {assertion.Id} lacks a supported bound screenshot/comparison.");
        var target = PngEvidence.ReadRaster(Resolve(root, probes[assertion.ProbeIndex].ScreenshotPath!));
        if (target is null) return new(false, false, $"Visual assertion {assertion.Id}: target PNG is invalid or valid-unclassified, not visual proof.");
        var targetRegion = TargetRegion(root, assertion, probes, target);
        if (!Sufficient(target, targetRegion))
            return new(false, false, $"Visual assertion {assertion.Id}: semantic ROI is blank/low-information, regardless of informative window chrome.");
        var source = PngEvidence.ReadRaster(Resolve(root, assertion.SourceEvidence));
        if (source is null) return new(true, false, $"Visual assertion {assertion.Id}: source PNG is not analyzable; supplemental evidence cannot prove parity.");
        Rectangle sourceRegion;
        if ((assertion.SourceRegion ?? assertion.Region) is { } explicitSource)
            sourceRegion = Explicit(explicitSource, source);
        else
        {
            if (assertion.BoundsEvidence is null || !state.Source.Evidence.Contains(assertion.BoundsEvidence, StringComparer.Ordinal))
                throw new InvalidDataException("A visual assertion requires source ROI bounds evidence or an explicit source region.");
            sourceRegion = FromTree(root, assertion.BoundsEvidence, assertion.AutomationId, source);
        }
        if (!Sufficient(source, sourceRegion))
            return new(true, false, $"Visual assertion {assertion.Id}: the source semantic ROI is low-information and cannot authorize visual parity.");
        var equal = Equal(source, sourceRegion, target, targetRegion, assertion);
        return new(true, equal, equal ? $"Visual assertion {assertion.Id} meets the contracted ROI comparison." :
            $"Visual assertion {assertion.Id}: source/target semantic regions differ.");
    }

    internal static bool Restored(DirectoryInfo root, StateAssertion baseline, StateAssertion restored,
        IReadOnlyList<UiDevelopmentProbe> probes)
    {
        var first = PngEvidence.ReadRaster(Resolve(root, probes[baseline.ProbeIndex].ScreenshotPath!));
        var last = PngEvidence.ReadRaster(Resolve(root, probes[restored.ProbeIndex].ScreenshotPath!));
        return first is not null && last is not null &&
            Equal(first, TargetRegion(root, baseline, probes, first), last, TargetRegion(root, restored, probes, last), restored);
    }

    private static Rectangle TargetRegion(DirectoryInfo root, StateAssertion assertion,
        IReadOnlyList<UiDevelopmentProbe> probes, PngEvidence.Raster raster)
    {
        if (assertion.Region is { } region) return Explicit(region, raster);
        if (assertion.BoundsProbeIndex is not { } index || index < 0 || index >= probes.Count ||
            index > assertion.ProbeIndex || probes[index].Command[2] != "inspect")
            throw new InvalidDataException("Visual ROI must be explicitly contracted or bound to a preceding inspect probe.");
        return FromTree(root, probes[index].StandardOutputPath, assertion.AutomationId, raster);
    }

    private static Rectangle Explicit(VisualRegion region, PngEvidence.Raster raster)
    {
        if (!double.IsFinite(region.X) || !double.IsFinite(region.Y) || !double.IsFinite(region.Width) || !double.IsFinite(region.Height) ||
            region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            region.X + region.Width > 1 || region.Y + region.Height > 1)
            throw new InvalidDataException("Explicit visual regions use normalized, contained nonempty image coordinates.");
        var x = (int)Math.Floor(region.X * raster.Width);
        var y = (int)Math.Floor(region.Y * raster.Height);
        return new Rectangle(x, y, (int)Math.Ceiling((region.X + region.Width) * raster.Width) - x,
            (int)Math.Ceiling((region.Y + region.Height) * raster.Height) - y);
    }

    private static Rectangle FromTree(DirectoryInfo root, string path, string automationId, PngEvidence.Raster raster)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Resolve(root, path)), new JsonDocumentOptions { AllowDuplicateProperties = false });
        var matches = new List<(JsonElement Element, JsonElement Window)>();
        Collect(document.RootElement, default, automationId, matches);
        if (matches.Count != 1 || matches[0].Window.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("ROI bounds must identify exactly one UIA element inside one window.");
        var (element, window) = matches[0];
        var wx = Number(window, "x"); var wy = Number(window, "y");
        var ww = Number(window, "width"); var wh = Number(window, "height");
        if (Math.Abs(ww - raster.Width) > 1 || Math.Abs(wh - raster.Height) > 1)
            throw new InvalidDataException("UIA-bound ROI requires a 1:1 single-window screenshot, not a crop/composite with unknown origin.");
        var x = (int)Math.Floor(Number(element, "x") - wx);
        var y = (int)Math.Floor(Number(element, "y") - wy);
        var right = (int)Math.Ceiling(Number(element, "x") - wx + Number(element, "width"));
        var bottom = (int)Math.Ceiling(Number(element, "y") - wy + Number(element, "height"));
        if (x < 0 || y < 0 || right > raster.Width || bottom > raster.Height || right <= x || bottom <= y)
            throw new InvalidDataException("UIA semantic ROI is outside the screenshot/window.");
        return new Rectangle(x, y, right - x, bottom - y);
    }

    private static void Collect(JsonElement value, JsonElement window, string id, List<(JsonElement, JsonElement)> matches)
    {
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) Collect(child, window, id, matches);
        else if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "Window") window = value;
            if (value.TryGetProperty("automationId", out var candidate) && candidate.ValueKind == JsonValueKind.String && candidate.GetString() == id)
                matches.Add((value, window));
            foreach (var field in new[] { "windows", "elements", "children" })
                if (value.TryGetProperty(field, out var children)) Collect(children, window, id, matches);
        }
    }

    private static double Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) && double.IsFinite(number) ? number :
        throw new InvalidDataException($"UIA bounds require a finite {property}.");

    private static bool Sufficient(PngEvidence.Raster raster, Rectangle region)
    {
        if (region.Width < 16 || region.Height < 16 || region.Width * region.Height < 1024) return false;
        var histogram = new Dictionary<uint, int>();
        for (var y = region.Y; y < region.Y + region.Height; y++)
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                var color = raster.Pixels[y * raster.Width + x] & 0x00f0f0f0;
                histogram[color] = histogram.GetValueOrDefault(color) + 1;
            }
        return histogram.Count >= 2 && histogram.Values.Max() <= (long)region.Width * region.Height * 0.98;
    }

    private static bool Equal(PngEvidence.Raster source, Rectangle sr, PngEvidence.Raster target, Rectangle tr, StateAssertion assertion)
    {
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
                var a = source.Pixels[(sr.Y + y * sr.Height / grid) * source.Width + sr.X + x * sr.Width / grid];
                var b = target.Pixels[(tr.Y + y * tr.Height / grid) * target.Width + tr.X + x * tr.Width / grid];
                error += Math.Abs((int)(a >> 16) - (int)(b >> 16)) +
                    Math.Abs((int)((a >> 8) & 255) - (int)((b >> 8) & 255)) + Math.Abs((int)(a & 255) - (int)(b & 255));
            }
        return error / (grid * grid * 3 * 255) <= assertion.MaximumMeanError;
    }

    private static string Resolve(DirectoryInfo root, string path) =>
        EnforcementStore.TryResolveContainedPath(root, path, out var full, out var error) ? full : throw new InvalidDataException(error);
}
