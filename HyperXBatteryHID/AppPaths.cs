using System;
using System.IO;

namespace HyperXBatteryHID;

internal static class AppPaths
{
    public static string AppDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HyperXBatteryHID");

    public static string LogsDir { get; } = Path.Combine(AppDataDir, "logs");

    public static string SettingsPath { get; } = Path.Combine(AppDataDir, "settings.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(LogsDir);
    }
}



