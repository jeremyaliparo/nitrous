using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

public static class StartupManager
{
    private const string TaskName = "Nitrous";

    public static bool CheckStartupTask()
    {
        var result = RunSchtasks($"/query /tn \"{TaskName}\"");
        return result.ExitCode == 0;
    }

    public static Dictionary<string, object?> GetDiagnosticsSnapshot()
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var query = RunSchtasks($"/query /tn \"{TaskName}\" /v /fo LIST");
            result["taskExists"] = query.ExitCode == 0;
            result["exitCode"] = query.ExitCode;
            result["query"] = query.Output.Trim();
        }
        catch (Exception ex)
        {
            Logger.Warn("Startup: diagnostics query failed.", ex);
            result["error"] = ex.Message;
        }

        return result;
    }

    public static bool ToggleStartupTask(bool enable, string exePath)
    {
        try
        {
            if (enable)
            {
                if (!File.Exists(exePath))
                {
                    Logger.Warn($"Startup: executable not found at '{exePath}'.");
                    return false;
                }

                var escapedPath = SecurityElement.Escape(exePath);
                var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT5S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{escapedPath}</Command>
    </Exec>
  </Actions>
</Task>";

                var tempFile = Path.Combine(Path.GetTempPath(), $"nitrous-task-{Guid.NewGuid():N}.xml");
                try
                {
                    // The task XML declaration says UTF-16; schtasks rejects a mismatched file.
                    File.WriteAllText(tempFile, xml, Encoding.Unicode);
                    var result = RunSchtasks($"/create /tn \"{TaskName}\" /xml \"{tempFile}\" /f");
                    if (result.ExitCode != 0)
                        Logger.Warn($"Startup: task creation failed (exit {result.ExitCode}): {result.Output.Trim()}");
                    return result.ExitCode == 0;
                }
                finally
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
            else
            {
                var result = RunSchtasks($"/delete /tn \"{TaskName}\" /f");
                if (result.ExitCode == 0) return true;

                // Deleting a task that does not exist is not a failure.
                if (!CheckStartupTask()) return true;

                Logger.Warn($"Startup: task deletion failed (exit {result.ExitCode}): {result.Output.Trim()}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Startup: failed to update the startup task.", ex);
            return false;
        }
    }

    private static (int ExitCode, string Output) RunSchtasks(string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("schtasks.exe", arguments)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();

            // Read both pipes concurrently; reading one to completion while the other fills
            // its buffer can deadlock.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(); } catch { }
                return (-1, "schtasks timed out");
            }

            var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Startup: could not run schtasks {arguments}.", ex);
            return (-1, ex.Message);
        }
    }
}
