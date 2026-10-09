using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace AllTemp;

enum DeviceKind { Cpu, Gpu, Memory, Motherboard, Storage, Cooler, Other }

sealed record SensorReading(string Id, string Name, float Value);

sealed record DeviceReading(
    string Id,
    DeviceKind Kind,
    string Name,
    string? Detail,
    SensorReading Primary,
    IReadOnlyList<SensorReading> Sensors);

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
        // everything for the first few rounds before skipping hardware that has no temperatures at all.
        bool discovering = _polls++ < 5;
        foreach (var hw in _computer.Hardware)
        {
            if (!discovering && !HasTempSensor(hw)) continue; // e.g. "Virtual Memory": nothing to show
            UpdateTree(hw);
            var sensors = new List<SensorReading>();
            CollectTemps(hw, sensors);
            if (sensors.Count == 0) continue;

            var kind = KindOf(hw.HardwareType);
            var primary = PickPrimary(kind, sensors);
            sensors.Remove(primary);
            sensors.Insert(0, primary); // primary first when a row is expanded
            var labeled = sensors.ConvertAll(r => r with { Name = Label(kind, r.Name) });
            devices.Add(new DeviceReading(
                hw.Identifier.ToString(),
                kind,
                CleanName(hw.Name),
                kind == DeviceKind.Storage ? LettersFor(hw) : null,
                labeled[0],
                labeled));
        }

        devices.Sort((a, b) => a.Kind != b.Kind
            ? a.Kind.CompareTo(b.Kind)
            : a.Kind == DeviceKind.Storage
                ? string.CompareOrdinal(a.Detail ?? "~", b.Detail ?? "~")
                : string.CompareOrdinal(a.Name, b.Name));
        return devices;
    }

    static bool HasTempSensor(IHardware hw) =>
        hw.Sensors.Any(s => s.SensorType == SensorType.Temperature) || hw.SubHardware.Any(HasTempSensor);

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
