using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AllTemp;

static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WM_NCLBUTTONDOWN = 0xA1;
    public const int HTCAPTION = 2;

    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtr(IntPtr hWnd, int nIndex, nint value);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void RoundCorners(IntPtr hwnd)
    {
        int round = 2; // DWMWCP_ROUND
        try { DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); } catch { /* pre-Windows 11 */ }
    }

    public static void SetExStyle(IntPtr hwnd, int flag, bool on)
    {
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, on ? style | flag : style & ~(nint)flag);
    }

    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    public static void TrimWorkingSet()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
}

static class Elevation
{
    public static bool IsAdmin { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>LibreHardwareMonitor reads CPU / RAM / motherboard sensors through the PawnIO driver.</summary>
    public static bool PawnIoInstalled
    {
        get
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key != null;
        }
    }
}

static class DriveLetters
{
    const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

    /// <summary>Physical drive index → "C: D:" style letter list.</summary>
    public static Dictionary<int, string> ByPhysicalDisk()
    {
        var map = new SortedDictionary<int, List<string>>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            var letter = drive.Name.TrimEnd('\\');
            using var h = Native.CreateFile($@"\\.\{letter}", 0, 3 /* read|write share */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            var buf = new byte[8 + 24 * 16];
            if (!Native.DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0, buf, buf.Length, out _, IntPtr.Zero)) continue;
            int count = BitConverter.ToInt32(buf, 0);
            for (int i = 0; i < count && i < 16; i++)
            {
                int disk = BitConverter.ToInt32(buf, 8 + i * 24);
                if (!map.TryGetValue(disk, out var list)) map[disk] = list = [];
                if (!list.Contains(letter)) list.Add(letter);
            }
        }
        return map.ToDictionary(kv => kv.Key, kv => string.Join(" ", kv.Value));
    }
}
