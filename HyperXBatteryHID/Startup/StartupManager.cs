using Microsoft.Win32;
using System;

namespace HyperXBatteryHID.Startup;

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled(string appName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(appName) is string s && !string.IsNullOrWhiteSpace(s);
    }

    public static void Enable(string appName, string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(appName, $"\"{exePath}\"");
    }

    public static void Disable(string appName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(appName) is null)
            return;
        key.DeleteValue(appName, throwOnMissingValue: false);
    }
}


