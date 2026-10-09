using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace AllTemp;

enum DeviceKind { Cpu, Gpu, Memory, Motherboard, Storage, Cooler, Other }

/// <param name="Warm">Shown amber from this temperature.</param>
/// <param name="Hot">Shown red from this temperature.</param>
sealed record SensorReading(string Id, string Name, float Value, float Warm = 0, float Hot = 0);

/// <param name="Percent">Load / activity, 0–100.</param>
/// <param name="Detail">Secondary figure: clock speed, memory used/total or transfer rate.</param>
sealed record UsageReading(float? Percent, string Detail);

/// <param name="Primary">Main temperature, or null for hardware that only reports usage (system RAM).</param>
/// <param name="Usage">Load and related figures, or null when the hardware has none (motherboard).</param>
sealed record DeviceReading(
    string Id,
    DeviceKind Kind,
    string Name,
    string? Detail,
    SensorReading? Primary,
    IReadOnlyList<SensorReading> Sensors,
    UsageReading? Usage);

/// <summary>
/// Owns the LibreHardwareMonitor <see cref="Computer"/> and polls it on a background thread.
/// The UI only ever sees immutable <see cref="DeviceReading"/> snapshots.
/// </summary>
sealed class SensorService : IDisposable
{
    static readonly string[] IgnoredNameParts =
        ["Distance to TjMax", "TjMax", "Warning", "Critical", "Limit", "Threshold", "Resolution"];

    readonly Computer _computer = new()
    {
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsStorageEnabled = true,
        IsControllerEnabled = true, // AIO / fan controllers (liquid temp)
        IsPsuEnabled = true,
    };

    readonly AutoResetEvent _wake = new(false);
    Thread? _thread;
    volatile bool _stopping;
    volatile int _intervalMs = 1000;
    Dictionary<int, string> _diskLetters = new();
    int _polls;

    public event Action<IReadOnlyList<DeviceReading>>? Updated;

    public int IntervalMs
    {
        get => _intervalMs;
        set { _intervalMs = Math.Clamp(value, 250, 60_000); _wake.Set(); }
    }

    /// <summary>Whether the PawnIO driver (CPU / RAM / motherboard sensors) is available. Valid once the first snapshot arrives.</summary>
    public bool PawnIoReady { get; private set; }

    /// <param name="installPawnIo">The user agreed to install the bundled driver; do it before opening the sensors.</param>
    public void Start(bool installPawnIo)
    {
        _thread = new Thread(() => Run(installPawnIo)) { IsBackground = true, Name = "SensorPoll", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    void Run(bool installPawnIo)
    {
        PawnIoReady = installPawnIo ? DriverSetup.InstallPawnIo() : Elevation.PawnIoInstalled;

        // LibreHardwareMonitor probes Intel GPUs through the Level Zero runtime (~50 MB resident) only when the
        // CPU group is enabled at Open(). Integrated Intel graphics expose no temperatures, so unless there's
        // an Arc card, open without the CPU and add it afterwards, which skips that probe.
        bool arc = HasIntelArc();
        _computer.IsCpuEnabled = arc;
        _computer.Open();
        _computer.IsCpuEnabled = true;
        _diskLetters = DriveLetters.ByPhysicalDisk();

        bool trimmed = false;
        while (!_stopping)
        {
            IReadOnlyList<DeviceReading> snapshot;
            try { snapshot = Poll(); }
            catch { snapshot = []; }
            if (_stopping) break;
            Updated?.Invoke(snapshot);
            if (!trimmed)
            {
                trimmed = true;
                Native.TrimWorkingSet(); // drop the one-off detection allocations from RAM
            }
            _wake.WaitOne(_intervalMs);
        }
    }

    static bool HasIntelArc()
    {
        try
        {
            using var q = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            return q.Get().Cast<ManagementObject>().Any(o => o["Name"]?.ToString()?.Contains("Arc", StringComparison.OrdinalIgnoreCase) == true);
        }
        catch { return true; } // unsure → keep the probe
    }

    IReadOnlyList<DeviceReading> Poll()
    {
        var devices = new List<DeviceReading>();
        // Some hardware (Super I/O chips, DIMMs) only activates its sensors during Update(), so poll
        // everything for the first few rounds before skipping hardware with nothing to show.
        bool discovering = _polls++ < 5;
        foreach (var hw in _computer.Hardware)
        {
            if (hw.Identifier.ToString() == "/vram") continue; // virtual memory: page file, not RAM
            if (!discovering && !HasUsefulSensor(hw)) continue;
            UpdateTree(hw);
            var sensors = new List<SensorReading>();
            CollectTemps(hw, sensors);
            var kind = KindOf(hw.HardwareType);
            var usage = ReadUsage(kind, hw);
            if (sensors.Count == 0)
            {
                if (usage != null)
                    devices.Add(new DeviceReading(hw.Identifier.ToString(), kind,
                        kind == DeviceKind.Memory ? "System memory" : CleanName(hw.Name),
                        kind == DeviceKind.Storage ? LettersFor(hw) : null, null, [], usage));
                continue;
            }

            var primary = PickPrimary(kind, sensors);
            sensors.Remove(primary);
            sensors.Insert(0, primary); // primary first when a row is expanded
            var (diskType, diskWarning) = kind == DeviceKind.Storage ? DiskInfo(hw) : ("", null);
            var labeled = sensors.Select((r, i) =>
            {
                var (warm, hot) = kind == DeviceKind.Storage ? DiskLimits(diskType, diskWarning, primary: i == 0) : Limits(kind, r.Name);
                return r with { Name = Label(kind, r.Name), Warm = warm, Hot = hot };
            }).ToList();
            devices.Add(new DeviceReading(
                hw.Identifier.ToString(),
                kind,
                CleanName(hw.Name),
                kind == DeviceKind.Storage ? LettersFor(hw) : null,
                labeled[0],
                labeled,
                usage));
        }

        devices.Sort((a, b) => a.Kind != b.Kind
            ? a.Kind.CompareTo(b.Kind)
            : a.Kind == DeviceKind.Storage
                ? string.CompareOrdinal(a.Detail ?? "~", b.Detail ?? "~")
                : string.CompareOrdinal(a.Name, b.Name));
        return devices;
    }

    static bool HasUsefulSensor(IHardware hw) =>
        hw.Sensors.Any(s => s.SensorType is SensorType.Temperature or SensorType.Load) || hw.SubHardware.Any(HasUsefulSensor);

    static UsageReading? ReadUsage(DeviceKind kind, IHardware hw)
    {
        float? Get(SensorType type, string name) =>
            hw.Sensors.FirstOrDefault(s => s.SensorType == type && s.Name == name)?.Value is float v && !float.IsNaN(v) ? v : null;

        switch (kind)
        {
            case DeviceKind.Cpu:
            {
                // AMD reports an average directly; Intel only per core ("CPU Core #1", "P-Core #1", …).
                var clock = Get(SensorType.Clock, "Cores (Average)")
                    ?? hw.Sensors.Where(s => s.SensorType == SensorType.Clock && s.Name.Contains("Core #") && s.Value > 0)
                        .Select(s => s.Value!.Value).DefaultIfEmpty(0).Average();
                var load = Get(SensorType.Load, "CPU Total");
                return load is null && clock is not > 0 ? null : new(load, clock > 0 ? $"{clock / 1000:0.00} GHz" : "");
            }
            case DeviceKind.Gpu:
            {
                var used = Get(SensorType.SmallData, "GPU Memory Used") ?? Get(SensorType.SmallData, "D3D Dedicated Memory Used");
                var total = Get(SensorType.SmallData, "GPU Memory Total");
                var detail = used is not float u ? ""
                    : total is float t && t > 0 ? $"{u / 1024:0.0}/{t / 1024:0.0} GB" : $"{u / 1024:0.0} GB";
                var load = Get(SensorType.Load, "GPU Core");
                return load is null && detail == "" ? null : new(load, detail);
            }
            case DeviceKind.Memory when hw.Identifier.ToString() == "/ram":
            {
                var used = Get(SensorType.Data, "Memory Used");
                var free = Get(SensorType.Data, "Memory Available");
                return new(Get(SensorType.Load, "Memory"), used is float u && free is float f ? $"{u:0.0}/{u + f:0.0} GB" : "");
            }
            case DeviceKind.Storage:
            {
                var load = Get(SensorType.Load, "Total Activity");
                var rate = (Get(SensorType.Throughput, "Read Rate") ?? 0) + (Get(SensorType.Throughput, "Write Rate") ?? 0);
                return load is null ? null : new(load, Rate(rate));
            }
            default:
                return null;
        }
    }

    static string Rate(float bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 * 100 => $"{bytesPerSecond / (1024 * 1024):0} MB/s",
        >= 1024 * 1024 => $"{bytesPerSecond / (1024 * 1024):0.0} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:0} KB/s",
        _ => "0 KB/s",
    };

    static void UpdateTree(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) UpdateTree(sub);
    }

    static void CollectTemps(IHardware hw, List<SensorReading> into)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType != SensorType.Temperature || s.Value is not float v) continue;
            if (float.IsNaN(v) || v <= 0f || v >= 150f) continue; // unconnected Super I/O inputs report 0 / -128 / 255
            if (IgnoredNameParts.Any(p => s.Name.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
            into.Add(new SensorReading(s.Identifier.ToString(), s.Name, v));
        }
        foreach (var sub in hw.SubHardware) CollectTemps(sub, into);
    }

    static DeviceKind KindOf(HardwareType t) => t switch
    {
        HardwareType.Cpu => DeviceKind.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => DeviceKind.Gpu,
        HardwareType.Memory => DeviceKind.Memory,
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => DeviceKind.Motherboard,
        HardwareType.Storage => DeviceKind.Storage,
        HardwareType.Cooler => DeviceKind.Cooler,
        _ => DeviceKind.Other,
    };

    static SensorReading PickPrimary(DeviceKind kind, List<SensorReading> sensors)
    {
        string[] preferred = kind switch
        {
            DeviceKind.Cpu => ["CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Core (Tdie)", "Core Max"],
            DeviceKind.Gpu => ["GPU Core"],
            DeviceKind.Storage => ["Composite Temperature", "Temperature", "Temperature 1"],
            DeviceKind.Motherboard => ["Motherboard", "System", "Mainboard", "VRM MOS", "PCH", "Chipset"],
            _ => [],
        };
        foreach (var name in preferred)
        {
            var hit = sensors.Find(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        if (kind == DeviceKind.Storage) return sensors[0];
        // The board's "CPU" input just repeats the CPU row, so prefer anything else.
        return sensors.Where(s => !s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)).MaxBy(s => s.Value)
            ?? sensors.MaxBy(s => s.Value)!;
    }

    static (float warm, float hot) Limits(DeviceKind kind, string sensor) => kind switch
    {
        DeviceKind.Cpu => (70, 85),
        DeviceKind.Gpu when sensor.Contains("Hot Spot") || sensor.Contains("Memory") || sensor.Contains("Junction") => (85, 100),
        DeviceKind.Gpu => (75, 85),
        DeviceKind.Memory or DeviceKind.Motherboard => (60, 80),
        DeviceKind.Cooler => (40, 50),
        _ => (70, 90),
    };

    /// <summary>
    /// Most SSDs are rated for 0–70 °C and hard drives for 0–55/60 °C. NVMe drives also report their own
    /// warning temperature (where they start throttling), which wins when it is lower.
    /// </summary>
    static (float warm, float hot) DiskLimits(string type, float? warning, bool primary)
    {
        if (type == "hdd") return (45, 55);
        if (type != "nvme") return (55, 70);
        if (primary) return (60, Math.Min(70, warning ?? 70)); // composite: the drive's overall reading
        return (70, Math.Max(75, warning ?? 80));              // extra sensors are usually the controller, which runs hotter
    }

    /// <summary>Drive type from the identifier ("/nvme/2", "/ssd/0", "/hdd/1") and the NVMe warning threshold, if reported.</summary>
    static (string type, float? warning) DiskInfo(IHardware hw)
    {
        var type = hw.Identifier.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var w = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name == "Warning Temperature")?.Value;
        return (type, w is > 30f and < 150f ? w : null);
    }

    /// <summary>Friendlier sensor names for the expanded rows.</summary>
    static string Label(DeviceKind kind, string name)
    {
        if (kind != DeviceKind.Storage) return name;
        if (name.Equals("Composite Temperature", StringComparison.OrdinalIgnoreCase)) return "Composite";
        if (name.Equals("Temperature", StringComparison.OrdinalIgnoreCase)) return "Drive";
        return name.StartsWith("Temperature #", StringComparison.OrdinalIgnoreCase) ? "Sensor " + name[13..] : name;
    }

    string? LettersFor(IHardware hw)
    {
        // Storage identifiers look like "/nvme/2" or "/hdd/0"; the trailing number is the physical drive index.
        var id = hw.Identifier.ToString();
        var tail = id[(id.LastIndexOf('/') + 1)..];
        return int.TryParse(tail, out var idx) && _diskLetters.TryGetValue(idx, out var letters) ? letters : null;
    }

    // The row tag already says CPU/GPU, so vendor boilerplate is dropped to keep the model visible.
    static readonly Regex NameNoise = new(@"\(R\)|\(TM\)|\b\d+(st|nd|rd|th) Gen\b|\bNVIDIA GeForce\b|\bAMD Radeon\b(?= \S)|\bProcessor\b|\b\d+-Core\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string CleanName(string name) => Regex.Replace(NameNoise.Replace(name, ""), @"\s{2,}", " ").Trim();

    /// <summary>Plain-text dump of every hardware node and sensor, for diagnosing what this machine exposes.</summary>
    public static string Dump()
    {
        var c = new Computer
        {
            IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true, IsMotherboardEnabled = true,
            IsStorageEnabled = true, IsControllerEnabled = true, IsPsuEnabled = true,
        };
        c.Open();
        var sb = new StringBuilder();
        sb.AppendLine($"Admin: {Elevation.IsAdmin}   PawnIO installed: {Elevation.PawnIoInstalled}");
        sb.AppendLine("Drive letters: " + string.Join(", ", DriveLetters.ByPhysicalDisk().Select(kv => $"{kv.Key}={kv.Value}")));
        void Walk(IHardware hw, int depth)
        {
            hw.Update();
            var pad = new string(' ', depth * 2);
            sb.AppendLine($"{pad}[{hw.HardwareType}] {hw.Name}  ({hw.Identifier})");
            foreach (var s in hw.Sensors.OrderBy(s => s.SensorType))
                sb.AppendLine($"{pad}  {s.SensorType,-12} {s.Name,-36} {s.Value?.ToString("0.0") ?? "-"}  ({s.Identifier})");
            foreach (var sub in hw.SubHardware) Walk(sub, depth + 1);
        }
        foreach (var hw in c.Hardware) Walk(hw, 0);
        c.Close();
        return sb.ToString();
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread?.Join(3000);
        try { _computer.Close(); } catch { /* driver already gone */ }
    }
}
