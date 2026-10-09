namespace AllTemp;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // `AllTemp.exe --dump [file]` writes every detected hardware node and sensor to a text file.
        int dump = Array.IndexOf(args, "--dump");
        if (dump >= 0)
        {
            var path = dump + 1 < args.Length ? args[dump + 1] : "sensors.txt";
            File.WriteAllText(path, SensorService.Dump());
            return;
        }

#if DEV_NO_ADMIN
        const string name = "AllTemp.Dev"; // dev builds can run next to an installed copy
#else
        const string name = "AllTemp";
#endif
        using var instance = new Mutex(true, $@"Local\{name}.Instance", out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Show");
        if (!first)
        {
            showSignal.Set(); // bring the running instance's window forward instead
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        ToolStripManager.Renderer = new DarkMenuRenderer();
        Application.Run(new OverlayForm(instance, showSignal));
    }

    /// <summary>UI exceptions are logged and the app keeps running; %AppData%\AllTemp\error.log is the place to look.</summary>
    static void LogError(Exception? ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllTemp");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}
