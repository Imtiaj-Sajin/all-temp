# AllTemp

A small Windows overlay that shows every temperature sensor on the machine in one place (CPU package and cores, each GPU, each SSD/HDD, RAM sticks, motherboard / VRM / chipset, AIO liquid temp), and can be pinned on top of other windows like the Xbox Game Bar performance widget.

- One row per device with a 60-sample sparkline; click a row to expand all of that device's sensors.
- Green / amber / red per device type, e.g. CPU 70 / 85 °C, GPU core 75 / 85 °C, GPU hot spot 85 / 100 °C, NVMe 60 / 70 °C (or the drive's own warning temperature if lower; controller sensors 70 / 80), SATA SSD 55 / 70 °C, hard drive 45 / 55 °C.
- Disks are labelled with their drive letters.
- **Pin** (📌): always on top, header hidden, adjustable transparency; hover to get the unpin button. Optional click-through.
- **Show** temperatures, usage, or both (right-click → Show): CPU load and clock, GPU load and VRAM, RAM used/total, disk activity and transfer rate. "Both" adds a usage column.
- Tray icon shows the live CPU temperature (or CPU load in usage mode); tooltip lists every device.
- Right-click the window or tray icon for options: opacity, click-through, °F, refresh rate, graphs, start with Windows.
- ~35–40 MB RAM, ~0.2 % CPU at 1 s refresh.

## Download

Get the latest from [Releases](https://github.com/Imtiaj-Sajin/all-temp/releases/latest):

- **`AllTemp-Setup-x.y.z.exe`** (recommended): installs to Program Files with a Start menu entry. Setup lets you choose the sensor driver and start with Windows, and downloads the Microsoft .NET 8 Desktop Runtime only if your PC doesn't have it yet.
- **`AllTemp-x.y.z-portable.exe`**: just the app, no install. Needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

Requirements: Windows 10/11 x64. AllTemp runs as administrator (it asks via UAC) because disk SMART temps and all low-level sensors need it.

## Sensor driver (PawnIO)

CPU, RAM and motherboard sensors can only be read through a kernel driver. Like HWMonitor, CPU-Z and HWiNFO, AllTemp ships one, so nothing needs to be downloaded separately: `AllTemp.exe` includes the official, signed [PawnIO](https://pawnio.eu) installer (v2.2.0, by namazso, also used by LibreHardwareMonitor and FanControl).

- The installer has a ticked "Install the PawnIO sensor driver" option. With the portable exe (or if you unticked it), AllTemp asks once on first launch instead. **Yes** installs it silently and carries on. **No** is remembered, and the window and right-click menu keep an install link.
- GPU and disk temperatures work without it.
- Uninstall it any time from *Settings → Apps → PawnIO*.
- The installer is embedded unmodified, as its license allows. PawnIO is GPL-2.0; source code at <https://github.com/namazso/PawnIO>. The build downloads it from the official release and checks its SHA-256 hash.

## Build

```powershell
.uild.ps1
```

Needs the .NET 8 SDK and [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`). Writes the installer and the portable exe to `dist\`. The installer script is `installer\AllTemp.iss`.

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
| `src/DriverSetup.cs` | Silent install of the bundled PawnIO driver |

Sensor access is via [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0).
