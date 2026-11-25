// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;

internal static class StartupHook
{
    public static void Initialize()
    {
        var marker = Environment.GetEnvironmentVariable("WINAPP_MIGRATE_TEST_DESCENDANT");
        if (string.IsNullOrEmpty(marker))
            return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" })
                start.ArgumentList.Add(argument);
            using var child = Process.Start(start)!;
            child.StandardInput.Close();
            File.WriteAllText(marker, child.Id.ToString(CultureInfo.InvariantCulture));
        };
    }
}
