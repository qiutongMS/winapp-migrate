// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;

namespace WinAppMigration.Enforcement;

public sealed class EnforcementTrace : IDisposable
{
    private static readonly AsyncLocal<EnforcementTrace?> Active = new();
    private readonly EnforcementTrace? _previous;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly object _writeLock = new();

    private EnforcementTrace(string path)
    {
        Path = path;
        _previous = Active.Value;
        Active.Value = this;
        Record("trace.started");
    }

    public string Path { get; }
    public static EnforcementTrace? Current => Active.Value;

    public static EnforcementTrace? Start(DirectoryInfo target)
    {
        if (Environment.GetEnvironmentVariable("WINAPP_MIGRATE_TRACE") != "1" ||
            !target.Exists)
            return null;
        try
        {
            var directory = System.IO.Path.Combine(
                target.FullName, ".migration-evidence", "enforcement", "diagnostics");
            Directory.CreateDirectory(directory);
            return new EnforcementTrace(System.IO.Path.Combine(
                directory, $"attest-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Mark(string phase, object? details = null) =>
        Current?.Record(phase, details);

    public static IDisposable? Measure(string phase) =>
        Current is { } trace ? new Phase(trace, phase) : null;

    public void Record(string phase, object? details = null)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow,
                elapsedMs = (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                processId = Environment.ProcessId,
                phase,
                details
            });
            lock (_writeLock)
                File.AppendAllText(Path, line + Environment.NewLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        Record("trace.completed");
        Active.Value = _previous;
    }

    private sealed class Phase : IDisposable
    {
        private readonly EnforcementTrace _trace;
        private readonly string _name;
        private readonly long _started = Stopwatch.GetTimestamp();

        internal Phase(EnforcementTrace trace, string name)
        {
            _trace = trace;
            _name = name;
            trace.Record(name + ".begin");
        }

        public void Dispose() => _trace.Record(_name + ".end", new
        {
            durationMs = (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds
        });
    }
}
