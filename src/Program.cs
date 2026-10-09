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

        using var instance = new Mutex(true, @"Local\AllTemp.Instance", out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AllTemp.Show");
        if (!first)
        {
            showSignal.Set(); // bring the running instance's window forward instead
            return;
        }

        ApplicationConfiguration.Initialize();
        ToolStripManager.Renderer = new DarkMenuRenderer();
        Application.Run(new OverlayForm(instance, showSignal));
    }
}
