using System.Diagnostics;
using System.Security;

namespace AllTemp;

/// <summary>
/// Start-with-Windows via a Task Scheduler logon task. A Run registry key can't start an
/// admin app without a UAC prompt; a task with "highest privileges" can.
/// </summary>
static class Autostart
{
    const string TaskName = "AllTemp";

    public static bool IsEnabled => Schtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static bool Set(bool enable)
    {
        if (!enable) return Schtasks($"/Delete /TN \"{TaskName}\" /F") == 0;

        var exe = SecurityElement.Escape(Environment.ProcessPath!);
        var user = SecurityElement.Escape(Environment.UserDomainName + "\\" + Environment.UserName);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command></Exec></Actions>
            </Task>
            """;
        var tmp = Path.Combine(Path.GetTempPath(), "AllTemp-task.xml");
        File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
        try { return Schtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F") == 0; }
        finally { File.Delete(tmp); }
    }

    static int Schtasks(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.WaitForExit(10_000);
        return p.ExitCode;
    }
}
