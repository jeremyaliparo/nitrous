using System;
using System.Diagnostics;
using System.Management;
using System.Threading.Tasks;
using Nitrous.Enums;

namespace Nitrous.Managers;

public static class AcerWmiManager
{
    public static bool IsHardwareSupported()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AcerGamingFunction");
            using var collection = searcher.Get();
            return collection.Count > 0;
        }
        catch { return false; }
    }

    public static async Task SetPowerModeAsync(PowerProfile profile)
    {
        ulong payload = ((ulong)profile << 8) | 0x0B;
        await InvokeWmiAsync("AcerGamingFunction", "SetGamingMiscSetting", payload.ToString());
    }

    public static async Task SetFansAsync(FanProfile profile)
    {
        if (profile == FanProfile.Auto)
        {
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanBehavior", 0x41000Ful.ToString());
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (1ul | (0ul << 8)).ToString());
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (2ul | (0ul << 8)).ToString());
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (4ul | (0ul << 8)).ToString());
        }
        else if (profile == FanProfile.Max)
        {
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanBehavior", 0x82000Ful.ToString());
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (1ul | (100ul << 8)).ToString());
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (2ul | (100ul << 8)).ToString());
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (4ul | (100ul << 8)).ToString());
        }
        else // Custom (Unified)
        {
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanBehavior", 0xC3000Ful.ToString());
            ulong speedPercent = (ulong)profile;
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (1ul | (speedPercent << 8)).ToString());
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (2ul | (speedPercent << 8)).ToString());
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (4ul | (speedPercent << 8)).ToString());
        }
    }

    public static async Task SetCustomFansAsync(int cpuSpeed, int gpuSpeed)
    {
        await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanBehavior", 0xC3000Ful.ToString());
        await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (1ul | ((ulong)cpuSpeed << 8)).ToString());
        await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (2ul | ((ulong)gpuSpeed << 8)).ToString());
        await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (4ul | ((ulong)gpuSpeed << 8)).ToString());
    }

    public static (int CpuTemp, int CpuRpm, int GpuTemp, int GpuRpm) GetSystemTelemetry()
    {
        int cpuTemp = 0, cpuRpm = 0, gpuTemp = 0, gpuRpm = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(@"ROOT\WMI", "SELECT * FROM AcerGamingFunction");
            foreach (ManagementObject instance in searcher.Get())
            {
                using (instance)
                {
                    using var cpuTempParams = instance.GetMethodParameters("GetGamingSysInfo");
                    cpuTempParams["gmInput"] = 0x0101ul;
                    using var cpuTempOut = instance.InvokeMethod("GetGamingSysInfo", cpuTempParams, null);
                    if (cpuTempOut != null)
                    {
                        ulong rawCpuTemp = Convert.ToUInt64(cpuTempOut["gmOutput"]);
                        cpuTemp = (int)((rawCpuTemp >> 8) & 0xFF);
                    }

                    using var cpuRpmParams = instance.GetMethodParameters("GetGamingSysInfo");
                    cpuRpmParams["gmInput"] = 0x0201ul;
                    using var cpuRpmOut = instance.InvokeMethod("GetGamingSysInfo", cpuRpmParams, null);
                    if (cpuRpmOut != null)
                    {
                        ulong rawCpuRpm = Convert.ToUInt64(cpuRpmOut["gmOutput"]);
                        cpuRpm = (int)((rawCpuRpm >> 8) & 0xFFFF);
                    }

                    ulong[] gpuFanIds = { 2ul, 3ul, 4ul };
                    foreach (ulong id in gpuFanIds)
                    {
                        using var gpuRpmParams = instance.GetMethodParameters("GetGamingSysInfo");
                        gpuRpmParams["gmInput"] = 0x0200ul | id;
                        using var gpuRpmOut = instance.InvokeMethod("GetGamingSysInfo", gpuRpmParams, null);
                        if (gpuRpmOut != null)
                        {
                            int rpm = (int)((Convert.ToUInt64(gpuRpmOut["gmOutput"]) >> 8) & 0xFFFF);
                            if (rpm > gpuRpm) gpuRpm = rpm; // Take the highest valid RPM
                        }
                    }
                    break;
                }
            }
        }
        catch { }

        gpuTemp = GetGpuTempNvidia();

        return (cpuTemp, cpuRpm, gpuTemp, gpuRpm);
    }

    private static int GetGpuTempNvidia()
    {
        try
        {
            using var p = new Process();
            p.StartInfo.FileName = "nvidia-smi";
            p.StartInfo.Arguments = "--query-gpu=temperature.gpu --format=csv,noheader,nounits";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.CreateNoWindow = true;
            p.Start();

            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(1000); // 1-second timeout safety

            if (int.TryParse(output, out int temp)) return temp;
        }
        catch { }
        return 0; // Returns 0 if Nvidia is deeply asleep or missing
    }

    public static async Task SetChargeLimitAsync(bool enable)
    {
        byte param = enable ? (byte)1 : (byte)0;
        await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM BatteryControl");
                using var collection = searcher.Get();
                foreach (ManagementObject instance in collection)
                {
                    using (instance)
                    {
                        using var inParams = instance.GetMethodParameters("SetBatteryHealthControl");
                        inParams["uBatteryNo"] = (byte)1;
                        inParams["uFunctionMask"] = (byte)1;
                        inParams["uFunctionStatus"] = param;
                        inParams["uReservedIn"] = new byte[] { 0, 0, 0, 0, 0 };
                        instance.InvokeMethod("SetBatteryHealthControl", inParams, null);

                        break;
                    }
                }
            }
            catch { }
        });
    }

    private static async Task InvokeWmiAsync(string className, string methodName, string gmInputStr)
    {
        await Task.Run(() =>
        {
            try
            {
                // Grabbing a fresh instance ensures the COM object is alive and connected
                using var searcher = new ManagementObjectSearcher(@"root\wmi", $"SELECT * FROM {className}");
                using var collection = searcher.Get();
                foreach (ManagementObject instance in collection)
                {
                    using (instance)
                    {
                        using var inParams = instance.GetMethodParameters(methodName);
                        inParams["gmInput"] = gmInputStr;
                        instance.InvokeMethod(methodName, inParams, null);
                    }
                }
            }
            catch { }
        });
    }
}
