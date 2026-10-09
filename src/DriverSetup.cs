using System.Diagnostics;

namespace AllTemp;

/// <summary>
/// Installs the bundled PawnIO driver (CPU / RAM / motherboard sensor access) once the user agrees,
/// so nothing has to be downloaded separately. The official signed installer is embedded unmodified,
/// which its license allows; see README.
/// </summary>
static class DriverSetup
{
    const string Resource = "AllTemp.PawnIO_setup.exe";

    public static bool Bundled => typeof(DriverSetup).Assembly.GetManifestResourceInfo(Resource) != null;

    /// <summary>The user unticked the driver option in the installer, so don't ask again on first launch.</summary>
    public static bool DeclinedInInstaller
    {
        get
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\AllTemp");
            return key?.GetValue("DriverDeclined") is int v && v != 0;
        }
    }

    /// <summary>Runs the bundled installer silently. Returns true if PawnIO is installed afterwards.</summary>
    public static bool InstallPawnIo()
    {
        if (Elevation.PawnIoInstalled) return true;
        if (!Elevation.IsAdmin || !Bundled) return false;

        var dir = Path.Combine(Path.GetTempPath(), "AllTemp-" + Guid.NewGuid().ToString("N"));
        var exe = Path.Combine(dir, "PawnIO_setup.exe");
        try
        {
            Directory.CreateDirectory(dir);
            using (var src = typeof(DriverSetup).Assembly.GetManifestResourceStream(Resource)!)
            using (var dst = File.Create(exe))
                src.CopyTo(dst);

            using var p = Process.Start(new ProcessStartInfo(exe, "-install -silent")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(120_000);
        }
        catch { /* fall through to the registry check */ }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
        return Elevation.PawnIoInstalled;
    }
}
