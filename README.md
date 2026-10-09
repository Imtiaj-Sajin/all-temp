# AllTemp

A small Windows overlay that shows every temperature sensor on the machine in one place (CPU package and cores, each GPU, each SSD/HDD, RAM sticks, motherboard / VRM / chipset, AIO liquid temp), and can be pinned on top of other windows like the Xbox Game Bar performance widget.

- One row per device with a 60-sample sparkline; click a row to expand all of that device's sensors.
- Green / amber / red per device type (e.g. CPU 70 / 85 °C, GPU core 75 / 85 °C, GPU hot spot 85 / 100 °C, disks 55 / 70 °C).
- Disks are labelled with their drive letters.
- **Pin** (📌): always on top, header hidden, adjustable transparency; hover to get the unpin button. Optional click-through.
- Tray icon shows the live CPU temperature; tooltip lists every device.
- Right-click the window or tray icon for options: opacity, click-through, °F, refresh rate, graphs, start with Windows.
- ~35–40 MB RAM, ~0.2 % CPU at 1 s refresh. Single 5 MB exe.

## Requirements

- Windows 10/11 x64, [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
- Runs as administrator (it asks via UAC). Disk SMART temps and all low-level sensors need it.
- **PawnIO driver** for CPU, RAM and motherboard temps. LibreHardwareMonitor 0.9.5+ reads those through PawnIO instead of the old WinRing0 driver. GPU and disk temps work without it. AllTemp offers to install it (`winget install namazso.PawnIO`), or get it from <https://pawnio.eu>.

## Build

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish
```

`publish\AllTemp.exe` is the whole app. Add `--self-contained true` for a bigger exe that doesn't need the .NET runtime installed.

For UI work without the UAC prompt: `dotnet build -p:DevNoAdmin=true` (CPU/disk temps will be missing).

## Diagnostics

`AllTemp.exe --dump sensors.txt` writes every hardware node and sensor LibreHardwareMonitor sees. Useful when a sensor is missing or mislabeled.

Settings live in `%AppData%\AllTemp\settings.json`. "Start with Windows" creates a Task Scheduler logon task named `AllTemp` (needed so an admin app starts without a UAC prompt).

## Code

| File | Purpose |
|---|---|
| `src/SensorService.cs` | Opens LibreHardwareMonitor, polls on a background thread, filters to temperature sensors, emits snapshots |
| `src/OverlayForm.cs` | Custom-painted window, pin mode, tray icon, menu |
| `src/TrayIcon.cs` | Renders the temperature number as the tray icon |
| `src/Native.cs` | Win32 bits: click-through, rounded corners, drive letter → physical disk mapping, PawnIO check |
| `src/Autostart.cs` | Task Scheduler logon task |

Sensor access is via [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0).
