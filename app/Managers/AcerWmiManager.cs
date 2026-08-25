using System;
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
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (1ul | (0ul << 8)).ToString());
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (4ul | (0ul << 8)).ToString());
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanBehavior", 0x410005ul.ToString());
        }
        else if (profile == FanProfile.Max)
        {
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanBehavior", 0x820005ul.ToString());
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (1ul | (100ul << 8)).ToString());
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (4ul | (100ul << 8)).ToString());
        }
        else
        {
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanBehavior", 0xC30005ul.ToString());
            ulong speedPercent = (ulong)profile;
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (1ul | (speedPercent << 8)).ToString());
            await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (4ul | (speedPercent << 8)).ToString());
        }
    }

    public static async Task SetCustomFansAsync(int cpuSpeed, int gpuSpeed)
    {
        await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanBehavior", 0xC30005ul.ToString());
        await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (1ul | ((ulong)cpuSpeed << 8)).ToString());
        await InvokeWmiAsync("AcerGamingFunction", "SetGamingFanSpeed", (4ul | ((ulong)gpuSpeed << 8)).ToString());
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
