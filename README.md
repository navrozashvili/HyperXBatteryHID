# HyperXBatteryHID

Basically same as https://github.com/navrozashvili/NgenuityBattertyTrayApp but this uses HID to communicate with device

Small Windows tray app that displays the battery percentage (and charging state) for supported **HID devices**, by talking **directly to the device** over HID.

## Supported devices (currently)

- **HyperX Cloud III S Wireless (dongle)** (`VID 03F0`, `PID 06BE`) via an experimental, reverse-engineered HID protocol.

## How it works (high level)

- **Startup**: `Program.cs` starts a WinForms `ApplicationContext` (`TrayAppContext`) and creates a `NotifyIcon` with a context menu.
- **Background device probing**: `Devices/HidProbeService.cs` continuously scans for supported HID devices so the app can start **before** the dongle is plugged in (the “funky first connection” scenario).
- **Protocol layer (reusable for other devices)**:
  - `Devices/IBatteryProtocol.cs` / `Devices/IBatteryProtocolSession.cs` define a small abstraction for “how to query battery on a device”.
  - `Devices/Protocols/HyperXCloud3SBatteryProtocol.cs` implements the Cloud III S dongle protocol.
- **Battery monitoring**: `Devices/HidBatteryMonitor.cs` opens the selected HID device, polls battery at an interval, and automatically retries on disconnect/reconnect.
- **Tray rendering**: `Ui/BatteryTrayIconRenderer.cs` draws the tray icon for the current percentage and charging flag, and the tooltip text is kept within Windows’ ~63 character limit.

## Why charging can look “wrong” when you first start the app

Some devices model **charging / not charging as events** (edge-triggered), not periodic telemetry. That means:

- If your device **is already charging** and **this tray app was not running** at the time charging started, you may **not** see a charging indicator immediately after launching the app.
- The app will update the charging icon once the device emits a charging-state report again (for example, after unplug/replug).

To keep this honest, the app treats charging as:

- **Charging / Not charging** only if a charging-state report was seen recently (configurable “stale” window)
- **Unknown** otherwise

## Requirements

- **Windows 10/11**
- A supported **HID device** (currently the Cloud III S dongle)

## Build & run

- Open `HyperXBatteryHID.slnx` in Visual Studio (or open the project folder).
- Build and run the `HyperXBatteryHID` project.

Once running, look for the tray icon. Right-click it to:

- select a device
- refresh devices
- toggle “Start with Windows”
- open the log folder
- exit

## Logs / troubleshooting

- Logs are written under the app’s logs directory (see `AppPaths.cs` and the “Open log folder” menu item).
- If the device isn’t detected at startup, just plug/unplug the dongle — the app is probing in the background and will pick it up automatically.
- Settings live at `%APPDATA%\HyperXBatteryHID\settings.json` (probe/query intervals, timeouts, etc.).

## Legal / disclaimer

- This is an **unofficial** community project.
- HyperX and related names are trademarks of their respective owners.
