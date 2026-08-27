using System.Diagnostics;
using System.Threading.Tasks;

namespace Nitrous.Managers;

public static class CpuPowerManager
{
    public static async Task SetWindowsCpuLimitsAsync(int minPercent, int maxPercent)
    {
        await Task.Run(() =>
        {
            RunPowerCfg($"/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN {minPercent}");
            RunPowerCfg($"/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX {maxPercent}");
            RunPowerCfg($"/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN {minPercent}");
            RunPowerCfg($"/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX {maxPercent}");
            RunPowerCfg("/setactive SCHEME_CURRENT");
        });
    }

    private static void RunPowerCfg(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", args) { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit();
        }
        catch { }
    }
}
