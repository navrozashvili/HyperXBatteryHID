using HyperXBatteryHID.Logging;
using HyperXBatteryHID.Settings;
using HyperXBatteryHID.Startup;
using HyperXBatteryHID.Devices;
using HyperXBatteryHID.Devices.Protocols;
using HyperXBatteryHID.Ui;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HyperXBatteryHID;

internal sealed class TrayAppContext : ApplicationContext
{
    private const string AppName = "HyperXBatteryHID";

    private readonly AppLogger _log;
    private readonly AppSettings _settings;
    private readonly SynchronizationContext _ui;

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _devicesRoot;
    private readonly ToolStripMenuItem _startupItem;
    private readonly IReadOnlyList<IBatteryProtocol> _protocols;
    private readonly HidProbeService _probe;
    private HidBatteryMonitor? _monitor;

    private List<DeviceCandidate> _devices = [];
    private DeviceCandidate? _selected;
    private BatterySnapshot? _lastSnapshot;

    private readonly object _settingsSaveLock = new();
    private CancellationTokenSource? _settingsSaveCts;

    public TrayAppContext()
    {
        AppPaths.EnsureCreated();

        var logPath = Path.Combine(AppPaths.LogsDir, $"app-{DateTime.Now:yyyyMMdd}.log");
        _log = new AppLogger(logPath);
        _settings = AppSettings.LoadOrCreateDefault(AppPaths.SettingsPath);
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);

        _menu = new ContextMenuStrip();
        _devicesRoot = new ToolStripMenuItem("Device");
        _startupItem = new ToolStripMenuItem("Start with Windows");

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = _menu,
            Text = "HyperX Battery Tray"
        };

        BuildStaticMenu();

        _protocols = new IBatteryProtocol[]
        {
            new HyperXCloud3SBatteryProtocol()
        };

        _probe = new HidProbeService(_log, _protocols, _settings.ProbeIntervalMs);
        _probe.DevicesChanged += devices => _ui.Post(_ => HandleDevicesChanged(devices), null);
        _probe.Start();
        _probe.ForceProbe();

        UpdateTrayDisplay();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelPendingSettingsSave();

            _monitor?.Dispose();
            _monitor = null;

            _probe.Dispose();

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();

            _menu.Dispose();
            BatteryTrayIconRenderer.DisposeCache();
            _log.Dispose();
        }
        base.Dispose(disposing);
    }

    private void BuildStaticMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(_devicesRoot);
        _menu.Items.Add(new ToolStripSeparator());

        _startupItem.Checked = StartupManager.IsEnabled(AppName);
        _startupItem.CheckOnClick = false;
        _startupItem.Click += (_, _) =>
        {
            try
            {
                var enabled = StartupManager.IsEnabled(AppName);
                if (enabled)
                {
                    StartupManager.Disable(AppName);
                    _startupItem.Checked = false;
                    _settings.StartWithWindows = false;
                }
                else
                {
                    StartupManager.Enable(AppName, Application.ExecutablePath);
                    _startupItem.Checked = true;
                    _settings.StartWithWindows = true;
                }
                RequestSettingsSave();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to toggle startup option");
            }
        };
        _menu.Items.Add(_startupItem);

        var refreshItem = new ToolStripMenuItem("Refresh devices");
        refreshItem.Click += (_, _) => _probe.ForceProbe();
        _menu.Items.Add(refreshItem);

        var openLogsItem = new ToolStripMenuItem("Open log folder");
        openLogsItem.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.LogsDir) { UseShellExecute = true }); }
            catch { /* ignore */ }
        };
        _menu.Items.Add(openLogsItem);

        _menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitThread();
        _menu.Items.Add(exitItem);
    }

    private void HandleDevicesChanged(IReadOnlyList<DeviceCandidate> devices)
    {
        try
        {
            _devices = devices.ToList();

            var nextSelected = PickSelection(_devices);
            var changed = !IsSameDevice(_selected, nextSelected);
            _selected = nextSelected;

            if (_selected is not null)
            {
                _settings.SelectedDevicePath = _selected.DevicePath;
                _settings.SelectedProtocolId = _selected.ProtocolId;
                RequestSettingsSave();
            }

            RebuildDeviceMenu();

            if (changed)
                RestartMonitorForSelection();

            UpdateTrayDisplay();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to handle device list update");
        }
    }

    private DeviceCandidate? PickSelection(List<DeviceCandidate> devices)
    {
        if (_selected is not null)
        {
            var stillThere = devices.FirstOrDefault(d => IsSameDevice(d, _selected));
            if (stillThere is not null)
                return stillThere;
        }

        if (!string.IsNullOrWhiteSpace(_settings.SelectedDevicePath))
        {
            var byPath = devices.FirstOrDefault(d =>
                string.Equals(d.DevicePath, _settings.SelectedDevicePath, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(_settings.SelectedProtocolId) ||
                 string.Equals(d.ProtocolId, _settings.SelectedProtocolId, StringComparison.OrdinalIgnoreCase)));

            if (byPath is not null)
                return byPath;
        }

        return devices.FirstOrDefault();
    }

    private static bool IsSameDevice(DeviceCandidate? a, DeviceCandidate? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return string.Equals(a.ProtocolId, b.ProtocolId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.DevicePath, b.DevicePath, StringComparison.OrdinalIgnoreCase);
    }

    private void RestartMonitorForSelection()
    {
        _monitor?.Dispose();
        _monitor = null;
        _lastSnapshot = null;

        if (_selected is null)
            return;

        var protocol = _protocols.FirstOrDefault(p => string.Equals(p.Id, _selected.ProtocolId, StringComparison.OrdinalIgnoreCase));
        if (protocol is null)
        {
            _log.Warn($"No protocol registered for selection: {_selected.ProtocolId}");
            return;
        }

        var options = new ProtocolSessionOptions(
            QueryTimeoutMs: _settings.QueryTimeoutMs,
            ChargingStaleMs: _settings.ChargingStaleMs
        );

        _monitor = new HidBatteryMonitor(_log, _selected, protocol, options, _settings.QueryIntervalMs);
        _monitor.SnapshotUpdated += snap => _ui.Post(_ => OnSnapshot(snap), null);
    }

    private void OnSnapshot(BatterySnapshot snap)
    {
        _lastSnapshot = snap;
        UpdateTrayDisplay();
    }

    private void RebuildDeviceMenu()
    {
        _devicesRoot.DropDownItems.Clear();

        if (_devices.Count == 0)
        {
            var none = new ToolStripMenuItem("(no supported HID devices found)") { Enabled = false };
            _devicesRoot.DropDownItems.Add(none);
            return;
        }

        foreach (var d in _devices)
        {
            var label = $"{d.ProtocolName}: {d.DisplayName}";
            var item = new ToolStripMenuItem(label)
            {
                Checked = _selected is not null && IsSameDevice(_selected, d)
            };
            item.Click += (_, _) =>
            {
                _selected = d;
                _settings.SelectedDevicePath = d.DevicePath;
                _settings.SelectedProtocolId = d.ProtocolId;
                RequestSettingsSave();
                RebuildDeviceMenu();
                RestartMonitorForSelection();
                UpdateTrayDisplay();
            };
            _devicesRoot.DropDownItems.Add(item);
        }
    }

    private void RequestSettingsSave()
    {
        // All settings mutations happen on the UI thread; take a snapshot now and do disk I/O in the background.
        var json = JsonSerializer.Serialize(_settings, AppSettings.JsonOptions);

        CancellationTokenSource cts;
        lock (_settingsSaveLock)
        {
            _settingsSaveCts?.Cancel();
            _settingsSaveCts?.Dispose();
            _settingsSaveCts = new CancellationTokenSource();
            cts = _settingsSaveCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Debounce rapid changes (device probe updates, multiple clicks, etc.).
                await Task.Delay(250, cts.Token).ConfigureAwait(false);

                Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SettingsPath)!);
                File.WriteAllText(AppPaths.SettingsPath, json);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // ignore
            }
            catch (Exception ex)
            {
                _log.Warn(ex, "Failed to save settings");
            }
        });
    }

    private void CancelPendingSettingsSave()
    {
        lock (_settingsSaveLock)
        {
            _settingsSaveCts?.Cancel();
            _settingsSaveCts?.Dispose();
            _settingsSaveCts = null;
        }
    }

    private void UpdateTrayDisplay()
    {
        var selected = _selected;
        var snap = _lastSnapshot;

        var isConnected = snap?.IsConnected ?? false;
        var pct = isConnected ? snap?.BatteryPercent : null;
        var isCharging = snap?.Charging == ChargingStatus.Charging;

        _notifyIcon.Icon = BatteryTrayIconRenderer.GetIcon(pct, isCharging);

        var name = selected?.DisplayName ?? "HID battery tray";
        var status =
            selected is null ? "No device" :
            !isConnected ? "Disconnected" :
            pct is >= 0 and <= 100 ? $"{pct}%" : "N/A";

        var charge =
            selected is null ? "" :
            snap?.Charging switch
            {
                ChargingStatus.Charging => ", chg",
                ChargingStatus.NotCharging => ", no chg",
                _ => ", chg?"
            };

        // NotifyIcon.Text is limited (~63 chars).
        var text = $"{name}: {status}{charge}";
        _notifyIcon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
    }
}


