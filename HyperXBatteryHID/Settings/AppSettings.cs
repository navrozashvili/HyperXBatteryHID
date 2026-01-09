using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyperXBatteryHID.Settings;

internal sealed class AppSettings
{
    // HID selection is persisted by device path so reconnects work reliably.
    public string? SelectedDevicePath { get; set; }
    public string? SelectedProtocolId { get; set; }
    public bool StartWithWindows { get; set; }

    /// <summary>How often we refresh the HID device list in the background.</summary>
    public int ProbeIntervalMs { get; set; } = 2000;

    /// <summary>How often we query battery while connected.</summary>
    public int QueryIntervalMs { get; set; } = 2000;

    /// <summary>Per-query deadline for receiving the expected HID reply.</summary>
    public int QueryTimeoutMs { get; set; } = 1000;

    /// <summary>
    /// Charging is treated as unknown if we haven't observed a charging-state report within this window.
    /// Some devices emit charging state only on plug/unplug transitions.
    /// </summary>
    public int ChargingStaleMs { get; set; } = 2500;

    [JsonIgnore]
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppSettings LoadOrDefault(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new AppSettings();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static AppSettings LoadOrCreateDefault(string path)
    {
        // Ensure a readable, writable config always exists on disk.
        try
        {
            if (!File.Exists(path))
            {
                var created = new AppSettings();
                created.Save(path);
                return created;
            }

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded is null)
                throw new JsonException("Settings JSON deserialized to null.");

            return loaded;
        }
        catch
        {
            try
            {
                if (File.Exists(path))
                {
                    var dir = Path.GetDirectoryName(path)!;
                    Directory.CreateDirectory(dir);
                    var backup = Path.Combine(dir, $"settings.bad-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                    File.Copy(path, backup, overwrite: false);
                }
            }
            catch
            {
                // ignore backup failures
            }

            var fallback = new AppSettings();
            try { fallback.Save(path); } catch { /* ignore */ }
            return fallback;
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }
}


